#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bim.Cad;
using Bim.Gltf;
using BriefSerializer = Bim.Core.BriefSerializer;

namespace Bim.Cli;

// P6-07 `cad-draft` and `cad-brief`: one DXF or PDF drawing (bounded input, at most MaxInputBytes, through BoundedInput)
// to a new JSON file holding the P6-04 BIM-IR draft (CadToBimIr) or the P6-05 brief draft (BriefExtract) plus its
// uncertainty report. Nothing is persisted to a project store; the file is a proposal for review ("persisted": false).
//
// Options (each at most once): --page n (PDF only, default 1); the drawing scale as either --metres-per-unit m (DXF:
// overrides $INSUNITS; PDF: a uniform page-unit scale) or --calibrate x1,y1,x2,y2,mm (PDF only: two page points and
// their real distance in millimetres, PdfCalibration.FromKnownDimension, no alignment); cad-draft only: --project-id
// guid (default: the first 16 bytes of the input SHA-256, so reruns are byte-identical) and --pdf-door-forms /
// --pdf-window-forms n,... (PDF Form XObject numbers). The format comes from the input extension (.dxf or .pdf).
//
// The output path follows GltfExportCommand.OutputProblem with extension .json: never an existing file, directory or
// link, and it is checked before the input is read. Every refusal prints one {"status":"failed","code":...[,"detail"]}
// envelope, exits 1 and writes nothing. Codes: usage, cad_option_unknown, cad_option_duplicate,
// cad_option_value_missing, cad_option_value_invalid, cad_option_not_applicable, cad_scale_conflict,
// cad_input_format_unsupported, output_* (OutputProblem, output_exists, output_io_error), input_* (BoundedInput),
// dxf_* / pdf_* (reader refusal codes with '-' as '_'), cad_page_missing, cad_uncalibrated, cad_draft_limit,
// cad_options_invalid, cad_output_not_serializable.
//
// Output document (UTF-8, no BOM, compact): {"kind":"cad-draft"|"cad-brief","version":1,"persisted":false,
// "source":{"file","format","bytes","sha256","page","scale",...},...}. cad-draft adds "millimetres_per_unit" to source
// and "project" (CadDraft.ToProject(), BIM-IR as the CLI serializes Project), "walls", "openings", "uncertainties".
// cad-brief adds "metres_per_unit" (null when unknown) to source and "brief" (BriefSerializer, the P6-03 schema),
// "rooms", "uncertainties", "issues" (BriefValidator issues). scale is insunits | metres-per-unit | known-dimension | none.
internal static class CadCommand
{
    // 32 MiB: four times the JSON input cap (BoundedInput.MaxBytes); below the DXF (256 MiB) and PDF (64 MiB) reader caps.
    public const int MaxInputBytes = 32 * 1024 * 1024;
    public const int MaxPage = 10_000;
    public const int MaxFormNumbers = 1_000;

    public const string DraftUsage = "usage: cad-draft input.dxf|input.pdf output.json [--page n] [--metres-per-unit m | --calibrate x1,y1,x2,y2,mm] [--project-id guid] [--pdf-door-forms n,...] [--pdf-window-forms n,...]";
    public const string BriefUsage = "usage: cad-brief input.dxf|input.pdf output.json [--page n] [--metres-per-unit m | --calibrate x1,y1,x2,y2,mm]";

    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    sealed record Options(int? Page, double? MetresPerUnit, double[]? Calibrate, Guid? ProjectId, int[]? DoorForms, int[]? WindowForms);

    public static void Run(string[] args)
    {
        var draft = args[0] == "cad-draft";
        if (args.Length < 3) throw new ArgumentException(draft ? DraftUsage : BriefUsage);
        try { Execute(draft, args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
    }

    static void Execute(bool draft, string[] args)
    {
        var options = ParseOptions(draft, args);
        var input = args[1];
        var output = args[2];
        var format = Path.GetExtension(input).ToLowerInvariant() switch
        {
            ".dxf" => "dxf",
            ".pdf" => "pdf",
            _ => throw new Refusal("cad_input_format_unsupported", "the input extension must be .dxf or .pdf"),
        };
        if (format == "dxf")
        {
            if (options.Page is not null) throw new Refusal("cad_option_not_applicable", "--page applies to PDF input");
            if (options.Calibrate is not null) throw new Refusal("cad_option_not_applicable", "--calibrate applies to PDF input; use --metres-per-unit for DXF");
            if (options.DoorForms is not null || options.WindowForms is not null) throw new Refusal("cad_option_not_applicable", "--pdf-door-forms and --pdf-window-forms apply to PDF input");
        }
        if (options.Calibrate is not null && options.MetresPerUnit is not null) throw new Refusal("cad_scale_conflict", "give either --metres-per-unit or --calibrate");
        PdfCalibration? calibration = null;
        if (options.Calibrate is { } c)
        {
            try { calibration = PdfCalibration.FromKnownDimension(new Point2(c[0], c[1]), new Point2(c[2], c[3]), c[4]); }
            catch (ArgumentException e) { throw new Refusal("cad_option_value_invalid", "--calibrate: " + e.Message); }
        }
        // Output is checked before the input is read, so a bad path costs no parse.
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string refused) throw new Refusal(refused);

        byte[] bytes;
        try { bytes = BoundedInput.ReadBytes(input, MaxInputBytes); }
        catch (InputRejectedException e) { throw new Refusal(e.Message); }
        var sourceFile = Path.GetFileName(input);
        var sha = SHA256.HashData(bytes);
        int? page = format == "pdf" ? options.Page ?? 1 : null;

        object document;
        JsonObject counts;
        try
        {
            DxfDrawing? drawing = null;
            PdfDocument? pdf = null;
            if (format == "dxf") drawing = DxfReader.Parse(bytes, sourceFile, new DxfReadOptions(MaxInputBytes));
            else
            {
                pdf = PdfReader.Parse(bytes, sourceFile, new PdfReadOptions(MaxBytes: MaxInputBytes));
                if (!pdf.Pages.Any(p => p.Number == page))
                    throw new Refusal("cad_page_missing", "the PDF has " + I(pdf.Pages.Count) + " page(s) and no page " + I(page!.Value));
            }
            if (draft)
            {
                var cadOptions = new CadToBimIrOptions(options.ProjectId ?? new Guid(sha.AsSpan(0, 16)))
                {
                    PdfDoorForms = options.DoorForms ?? Array.Empty<int>(),
                    PdfWindowForms = options.WindowForms ?? Array.Empty<int>(),
                };
                string scale;
                CadDraft result;
                if (drawing is not null)
                {
                    scale = options.MetresPerUnit is null ? "insunits" : "metres-per-unit";
                    result = CadToBimIr.FromDxf(drawing, cadOptions with { MillimetresPerUnit = options.MetresPerUnit * 1000 });
                }
                else
                {
                    if (options.MetresPerUnit is { } m) calibration = new PdfCalibration(m * 1000, PdfMatrix.Scaling(m * 1000));
                    scale = options.MetresPerUnit is not null ? "metres-per-unit" : "known-dimension";
                    // Without --calibrate or --metres-per-unit the library refuses the page as uncalibrated.
                    result = CadToBimIr.FromPdf(pdf!, page!.Value, calibration!, cadOptions);
                }
                document = new
                {
                    kind = "cad-draft",
                    version = 1,
                    persisted = false,
                    source = new { file = sourceFile, format, bytes = bytes.LongLength, sha256 = Convert.ToHexString(sha), page, scale, millimetres_per_unit = result.MillimetresPerUnit },
                    project = result.ToProject(),
                    walls = result.Walls,
                    openings = result.Openings,
                    uncertainties = result.Uncertainties,
                };
                counts = new JsonObject { ["walls"] = result.Walls.Count, ["openings"] = result.Openings.Count, ["uncertainties"] = result.Uncertainties.Count };
            }
            else
            {
                var briefOptions = new BriefExtractOptions { MetresPerUnit = options.MetresPerUnit };
                var result = drawing is not null ? BriefExtract.FromDxf(drawing, briefOptions) : BriefExtract.FromPdf(pdf!, page!.Value, calibration, briefOptions);
                var scale = calibration is not null ? "known-dimension"
                    : options.MetresPerUnit is not null ? "metres-per-unit"
                    : drawing?.MetresPerUnit is not null ? "insunits" : "none";
                document = new
                {
                    kind = "cad-brief",
                    version = 1,
                    persisted = false,
                    source = new { file = sourceFile, format, bytes = bytes.LongLength, sha256 = Convert.ToHexString(sha), page, scale, metres_per_unit = result.MetresPerUnit },
                    brief = JsonNode.Parse(BriefSerializer.Serialize(result.Brief)),
                    rooms = result.Rooms,
                    uncertainties = result.Uncertainties,
                    issues = result.Issues,
                };
                counts = new JsonObject { ["rooms"] = result.Rooms.Count, ["uncertainties"] = result.Uncertainties.Count, ["issues"] = result.Issues.Count };
            }
        }
        catch (DxfFormatException e) { throw new Refusal("dxf_" + Snake(e.Code), e.Message); }
        catch (PdfFormatException e) { throw new Refusal("pdf_" + Snake(e.Code), e.Message); }
        catch (CadDraftException e) { throw new Refusal("cad_" + Snake(e.Code), e.Message); }
        catch (ArgumentException e) { throw new Refusal("cad_options_invalid", e.Message); }

        byte[] written;
        // Non-finite numbers cannot be written as JSON; such a draft is refused rather than written in part.
        try { written = JsonSerializer.SerializeToUtf8Bytes(document, Json); }
        catch (ArgumentException e) { throw new Refusal("cad_output_not_serializable", e.Message); }
        try { GltfExporter.WriteNew(written, output); }
        catch (GltfExportException e) { throw new Refusal(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", e.GetType().Name); }

        var envelope = new JsonObject
        {
            ["status"] = "succeeded",
            ["kind"] = draft ? "cad-draft" : "cad-brief",
            ["format"] = format,
            ["output"] = output,
            ["bytes"] = written.LongLength,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(written)),
            ["persisted"] = false,
        };
        foreach (var (name, value) in counts.ToArray()) envelope[name] = value?.DeepClone();
        Console.WriteLine(envelope.ToJsonString());
    }

    static Options ParseOptions(bool draft, string[] args)
    {
        int? page = null; double? metres = null; double[]? calibrate = null; Guid? projectId = null; int[]? doors = null, windows = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i += 2)
        {
            var name = args[i];
            var known = name is "--page" or "--metres-per-unit" or "--calibrate" || (draft && name is "--project-id" or "--pdf-door-forms" or "--pdf-window-forms");
            if (!known) throw new Refusal("cad_option_unknown", name);
            if (!seen.Add(name)) throw new Refusal("cad_option_duplicate", name);
            if (i + 1 >= args.Length) throw new Refusal("cad_option_value_missing", name);
            var value = args[i + 1];
            var invalid = name + ": ";
            switch (name)
            {
                case "--page":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p < 1 || p > MaxPage)
                        throw new Refusal("cad_option_value_invalid", invalid + "a page number in [1, " + I(MaxPage) + "]");
                    page = p;
                    break;
                case "--metres-per-unit":
                    if (Number(value) is not { } m || m < 1e-9 || m > 1000) throw new Refusal("cad_option_value_invalid", invalid + "a finite number of metres in [1e-9, 1000]");
                    metres = m;
                    break;
                case "--calibrate":
                    var numbers = value.Split(',').Select(Number).ToArray();
                    if (numbers.Length != 5 || numbers.Any(n => n is null)) throw new Refusal("cad_option_value_invalid", invalid + "x1,y1,x2,y2,mm (five finite numbers)");
                    calibrate = numbers.Select(n => n!.Value).ToArray();
                    break;
                case "--project-id":
                    // The empty GUID is no project id (the library refuses it), so it is refused here before any I/O.
                    if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw new Refusal("cad_option_value_invalid", invalid + "a non-empty GUID");
                    projectId = id;
                    break;
                default:
                    var forms = value.Split(',');
                    if (forms.Length > MaxFormNumbers) throw new Refusal("cad_option_value_invalid", invalid + "at most " + I(MaxFormNumbers) + " object numbers");
                    var parsed = new int[forms.Length];
                    for (var k = 0; k < forms.Length; k++)
                        if (!int.TryParse(forms[k], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[k]) || parsed[k] < 1)
                            throw new Refusal("cad_option_value_invalid", invalid + "comma-separated positive object numbers");
                    if (name == "--pdf-door-forms") doors = parsed; else windows = parsed;
                    break;
            }
        }
        return new Options(page, metres, calibrate, projectId, doors, windows);
    }

    // A finite invariant-culture number (sign, decimal point, exponent; no thousands separators), or null.
    static double? Number(string text) =>
        double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    static string Snake(string code) => code.Replace('-', '_');

    static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
