#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bim.Cad;
using Bim.Core;
using Bim.Gltf;

namespace Bim.Cli;

// P6-10 `cad-plan-brief draft.json output.json [--min-confidence x] [--confirm id]... [--confirm-reason code]...
//   [--site-width w --site-depth d] [--name text] [--brief-only]`:
// a P6-07 `cad-brief` document (the P6-05 brief draft) becomes the P6-08 planner brief (BriefPlanner.ToPlannerBrief),
// written to a new JSON file. Nothing is persisted to a project store ("persisted": false).
//
// Options: --min-confidence x at most once (a finite number in [0, 1], default BriefPlannerOptions.DefaultMinConfidence);
// --confirm id and --confirm-reason code repeatable (ids must be brief room ids, BriefParser.IsRoomId; codes must be
// BriefPlannerCodes.All, case-sensitive). Repeats and order do not matter. A confirmed id naming no draft room is
// reported in unknown_confirmed_rooms, not refused.
// P6-12: --site-width w and --site-depth d, each at most once and only together (cad_option_pair_missing otherwise),
// are metres with the --min-confidence number grammar, finite and within Bim.Generative.PrototypeGenerator.MinLotSideMetres..
// MaxLotSideMetres inclusive (the PrototypeLot bounds). They set BriefPlannerOptions.Constraints to the draft brief's
// constraints with only the site replaced (jurisdiction, levels and budget are kept), so the written brief can be planned
// by the P7-01 SpacePlanner without a PlannerOptions.Footprint. --name text at most once must pass BriefParser.IsText
// (non-blank, at most 2048 UTF-16 code units, no lone surrogate) and replaces the brief name. Without these options the
// output is byte-for-byte what P6-10 wrote; with them "options" also carries "site_width_m","site_depth_m" (when a site
// is given) and "name" (when given), after the P6-10 members.
//
// Input (bounded, BoundedInput.ReadBytes at BoundedInput.MaxBytes): a JSON object with "kind":"cad-brief", "version":1, "brief" (P6-03, read
// with BriefParser), "rooms" (BriefRoomProposal[]) and "uncertainties" (BriefExtractUncertainty[]) as cad-brief writes
// them: every member of a proposal, label/area/region source and uncertainty is required with its exact name, unknown
// and duplicate members are refused, Label must not be null and Confidence must be in [0, 1]. Room and uncertainty
// counts are bounded by the BriefExtractOptions defaults (see ReadDraft). Value-identical labels and sources are
// re-bound to one instance (see ReadDraft). Without a "source" block, drawing_file and drawing_sha256 are null.
//
// The output path follows GltfExportCommand.OutputProblem with extension .json and is checked before the input is read;
// options are checked before either. Every refusal prints one {"status":"failed","code":...[,"detail"]} envelope, exits
// 1 and writes nothing. Codes: "usage: cad-plan-brief ..." (fewer than two arguments; printed with the usage text as
// the code by the CLI's top-level handler, no detail), cad_option_unknown, cad_option_duplicate, cad_option_value_missing,
// cad_option_value_invalid, cad_option_pair_missing, output_* (OutputProblem, output_exists, output_io_error), input_* (BoundedInput),
// cad_plan_brief_input_invalid (not a cad-brief document), brief_<code> (BriefParser refusal of the draft's brief).
//
// Output document (UTF-8, no BOM, compact, deterministic): {"kind":"cad-plan-brief","version":1,"persisted":false,
// "source":{"file","bytes","sha256","drawing_file","drawing_sha256"},"options":{"min_confidence","confirmed_rooms",
// "confirmed_reasons" (distinct, ordinal order)[,"site_width_m","site_depth_m" (only with a site)][,"name" (only with
// --name)]},"valid","brief" (BriefSerializer canonical text, verbatim),"kept",
// "dropped","unknown_confirmed_rooms","unknown_confirmed_reasons","issues"}. An invalid planner brief (for example every
// room dropped: rooms_empty) is a result, not a refusal: it is still written with "valid": false, the stdout envelope
// says "valid":false, and the exit code is 0; the issues say why. unknown_confirmed_reasons is always [] because
// unknown reason codes are refused as cad_option_value_invalid; it is kept to mirror BriefPlannerResult.
//
// P6-14 --brief-only (a flag, no value, at most once: cad_option_duplicate; anywhere among the options): the file holds
// only the bare P6-03 planner brief, exactly the bytes of the full document's "brief" member (the UTF-8 encoding of
// BriefSerializer.Serialize(result.Brief): canonical, compact, no BOM, no trailing newline), which plan-layouts reads
// directly. Option, output and input checks, refusals, never-overwrite and the valid:false-is-written rule are the same;
// the stdout envelope is the same plus a final "brief_only":true member. Without the flag file and envelope bytes are
// unchanged (no brief_only member).
internal static class CadPlanBriefCommand
{
    public const string Usage = "usage: cad-plan-brief draft.json output.json [--min-confidence x] [--confirm id]... [--confirm-reason code]... [--site-width w --site-depth d] [--name text] [--brief-only]";
    public const string InputInvalid = "cad_plan_brief_input_invalid";

    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    public static void Run(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException(Usage);
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
    }

    static void Execute(string[] args)
    {
        var (options, site, briefOnly) = ParseOptions(args);
        var input = args[1];
        var output = args[2];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string refused) throw new Refusal(refused);

        // One bounded read (BoundedInput.MaxBytes, the JSON input cap): the bytes are hashed for the source block and
        // decoded exactly as BoundedInput.ReadText does (UTF-8, byte order marks detected and stripped).
        byte[] raw;
        try { raw = BoundedInput.ReadBytes(input, BoundedInput.MaxBytes); }
        catch (InputRejectedException e) { throw new Refusal(e.Message); }
        string text;
        using (var reader = new StreamReader(new MemoryStream(raw, writable: false), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true)) text = reader.ReadToEnd();

        var (draft, drawingFile, drawingSha) = ReadDraft(text);
        if (site is { } given) options = options with { Constraints = draft.Brief.Constraints with { Site = given } };
        var result = BriefPlanner.ToPlannerBrief(draft, options);
        var briefJson = BriefSerializer.Serialize(result.Brief);

        byte[] written;
        if (briefOnly) written = new System.Text.UTF8Encoding(false).GetBytes(briefJson);
        else
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "cad-plan-brief");
                writer.WriteNumber("version", 1);
                writer.WriteBoolean("persisted", false);
                writer.WriteStartObject("source");
                writer.WriteString("file", Path.GetFileName(input));
                writer.WriteNumber("bytes", raw.LongLength);
                writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(raw)));
                writer.WriteString("drawing_file", drawingFile);
                writer.WriteString("drawing_sha256", drawingSha);
                writer.WriteEndObject();
                writer.WriteStartObject("options");
                writer.WriteNumber("min_confidence", options.MinConfidence);
                Strings(writer, "confirmed_rooms", options.ConfirmedRooms);
                Strings(writer, "confirmed_reasons", options.ConfirmedReasons);
                if (site is not null)
                {
                    writer.WriteNumber("site_width_m", site.WidthMetres.Value);
                    writer.WriteNumber("site_depth_m", site.DepthMetres.Value);
                }
                if (options.Name is { } name) writer.WriteString("name", name);
                writer.WriteEndObject();
                writer.WriteBoolean("valid", result.IsValid);
                writer.WritePropertyName("brief");
                writer.WriteRawValue(briefJson);
                writer.WritePropertyName("kept");
                JsonSerializer.Serialize(writer, result.Kept, Json);
                writer.WritePropertyName("dropped");
                JsonSerializer.Serialize(writer, result.Dropped, Json);
                writer.WritePropertyName("unknown_confirmed_rooms");
                JsonSerializer.Serialize(writer, result.UnknownConfirmedRooms, Json);
                writer.WritePropertyName("unknown_confirmed_reasons");
                JsonSerializer.Serialize(writer, result.UnknownConfirmedReasons, Json);
                writer.WritePropertyName("issues");
                JsonSerializer.Serialize(writer, result.Issues, Json);
                writer.WriteEndObject();
            }
            written = buffer.ToArray();
        }

        try { GltfExporter.WriteNew(written, output); }
        catch (GltfExportException e) { throw new Refusal(e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", e.GetType().Name); }

        using var envelope = new MemoryStream();
        using (var writer = new Utf8JsonWriter(envelope))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "succeeded");
            writer.WriteString("kind", "cad-plan-brief");
            writer.WriteString("output", output);
            writer.WriteNumber("bytes", written.LongLength);
            writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(written)));
            writer.WriteBoolean("persisted", false);
            writer.WriteBoolean("valid", result.IsValid);
            writer.WriteNumber("kept", result.Kept.Count);
            writer.WriteNumber("dropped", result.Dropped.Count);
            writer.WriteNumber("issues", result.Issues.Count);
            if (briefOnly) writer.WriteBoolean("brief_only", true);
            writer.WriteEndObject();
        }
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(envelope.ToArray()));
    }

    static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    // Shapes of the cad-brief "rooms" and "uncertainties" members as System.Text.Json writes the Bim.Cad records (exact,
    // case-sensitive names). Every listed member is required (it may be null where the record allows it); Point2's
    // computed Length is the only optional member; any other member is refused.
    static readonly string[] ProposalMembers = { "Id", "Name", "StatedAreaSquareMetres", "RegionAreaSquareMetres", "Confidence", "Label", "Area", "Region", "Notes" };
    static readonly string[] SourceMembers = { "SourceFile", "Format", "Handle", "Layer", "EntityType", "Line", "Page", "InsertPath", "At", "Text", "Tag" };
    static readonly string[] PointMembers = { "X", "Y" };
    static readonly string[] PointOptional = { "Length" };
    static readonly string[] UncertaintyMembers = { "Code", "Message", "RoomId", "ValueSquareMetres", "Sources" };
    static readonly string[] None = Array.Empty<string>();
    static readonly BriefExtractOptions Limits = new();
    const string UncertaintyLimit = "uncertainty-limit";
    static readonly HashSet<string> AlwaysReported = new(StringComparer.Ordinal) { "explode-limit", "pair-limit", "region-limit", "text-limit", "uncalibrated", "work-limit" };

    // The cad-brief document as a BriefDraft. Structure errors are cad_plan_brief_input_invalid with a detail; the
    // brief itself goes through the strict P6-03 parser (brief_<code>).
    // Work bound: at most BriefExtractOptions.MaxTexts (default) rooms; at most MaxUncertainties (default) counted
    // uncertainties, at most as many always-reported limit/scale entries, and at most one uncertainty-limit entry, the
    // most an extraction with default options writes (BriefExtract Report).
    // Labels and sources are compared by value (their JSON form): every proposal label and uncertainty source that is
    // value-identical to an earlier proposal label is re-bound to that first label instance, so the planner's reference
    // comparison (BriefPlanner step 2) sees one label record per distinct value, as on an in-process draft whose
    // value-identical labels share one instance.
    static (BriefDraft Draft, string? DrawingFile, string? DrawingSha) ReadDraft(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException e) { throw new Refusal(InputInvalid, "JSON: " + e.Message); }
        catch (ArgumentException e) { throw new Refusal(InputInvalid, "JSON: " + e.Message); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new Refusal(InputInvalid, "the draft must be a JSON object");
            // Duplicate members anywhere outside the brief (the brief parser refuses its own as brief_member_duplicate).
            NoDuplicates(root, "", top: true);
            if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "cad-brief")
                throw new Refusal(InputInvalid, "kind must be \"cad-brief\" (the cad-brief verb output)");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
                throw new Refusal(InputInvalid, "version must be 1");
            var briefElement = Member(root, "brief", JsonValueKind.Object);
            var roomsElement = Member(root, "rooms", JsonValueKind.Array);
            var uncertaintiesElement = Member(root, "uncertainties", JsonValueKind.Array);
            string? sourceFile = null, format = null, drawingSha = null;
            double? metresPerUnit = null;
            if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            {
                sourceFile = OptionalString(source, "file");
                format = OptionalString(source, "format");
                drawingSha = OptionalString(source, "sha256");
                if (source.TryGetProperty("metres_per_unit", out var mpu) && mpu.ValueKind == JsonValueKind.Number && mpu.TryGetDouble(out var m) && double.IsFinite(m)) metresPerUnit = m;
            }

            if (roomsElement.GetArrayLength() > Limits.MaxTexts)
                throw new Refusal(InputInvalid, "rooms: at most " + I(Limits.MaxTexts) + " proposals");
            int counted = 0, always = 0, capped = 0, index = 0;
            foreach (var item in uncertaintiesElement.EnumerateArray())
            {
                var code = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (code == UncertaintyLimit) capped++;
                else if (code is not null && AlwaysReported.Contains(code)) always++;
                else counted++;
                if (counted > Limits.MaxUncertainties || always > Limits.MaxUncertainties || capped > 1)
                    throw new Refusal(InputInvalid, "uncertainties: at most " + I(Limits.MaxUncertainties) + " counted entries, as many limit entries and one uncertainty-limit (entry " + I(index) + ")");
                index++;
            }

            var i = 0;
            foreach (var item in roomsElement.EnumerateArray())
            {
                var path = "rooms[" + I(i++) + "]";
                Shape(item, path, ProposalMembers, None);
                SourceShape(item.GetProperty("Label"), path + ".Label", nullable: false);
                SourceShape(item.GetProperty("Area"), path + ".Area", nullable: true);
                SourceShape(item.GetProperty("Region"), path + ".Region", nullable: true);
            }
            i = 0;
            foreach (var item in uncertaintiesElement.EnumerateArray())
            {
                var path = "uncertainties[" + I(i++) + "]";
                Shape(item, path, UncertaintyMembers, None);
                var sources = item.GetProperty("Sources");
                if (sources.ValueKind != JsonValueKind.Array) throw new Refusal(InputInvalid, path + ".Sources must be an array");
                var k = 0;
                foreach (var entry in sources.EnumerateArray()) SourceShape(entry, path + ".Sources[" + I(k++) + "]", nullable: false);
            }

            ProjectBrief brief;
            try { brief = BriefParser.Parse(briefElement.GetRawText()); }
            catch (BriefException e) { throw new Refusal("brief_" + e.Code, (e.Field ?? "brief") + ": " + (e.Detail ?? e.Code)); }

            BriefRoomProposal?[]? rooms;
            BriefExtractUncertainty?[]? uncertainties;
            try
            {
                rooms = roomsElement.Deserialize<BriefRoomProposal?[]>();
                uncertainties = uncertaintiesElement.Deserialize<BriefExtractUncertainty?[]>();
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
            {
                throw new Refusal(InputInvalid, "rooms/uncertainties: " + e.Message);
            }
            if (rooms is null || uncertainties is null) throw new Refusal(InputInvalid, "rooms and uncertainties must be arrays");
            var labels = new Dictionary<string, BriefTextSource>(StringComparer.Ordinal);
            var proposals = new BriefRoomProposal[rooms.Length];
            for (var r = 0; r < rooms.Length; r++)
            {
                var room = rooms[r];
                if (room is null || room.Id is null || room.Name is null || room.Notes is null || room.Label is null || !(room.Confidence >= 0 && room.Confidence <= 1))
                    throw new Refusal(InputInvalid, "rooms[" + I(r) + "] must be a proposal with Id, Name, Label, Notes and a Confidence in [0, 1]");
                var key = Key(room.Label);
                if (!labels.TryAdd(key, room.Label)) room = room with { Label = labels[key] };
                proposals[r] = room;
            }
            var bound = new BriefExtractUncertainty[uncertainties.Length];
            for (var u = 0; u < uncertainties.Length; u++)
            {
                var uncertainty = uncertainties[u];
                if (uncertainty is null || uncertainty.Code is null || uncertainty.Sources is null || uncertainty.Sources.Contains(null!))
                    throw new Refusal(InputInvalid, "uncertainties[" + I(u) + "] must be an uncertainty with Code and non-null Sources");
                var rebound = uncertainty.Sources.Select(item => labels.TryGetValue(Key(item), out var known) ? known : item).ToArray();
                bound[u] = uncertainty with { Sources = rebound };
            }
            var draft = new BriefDraft(sourceFile ?? "", format ?? "", metresPerUnit, brief, proposals, bound, BriefValidator.Validate(brief));
            return (draft, sourceFile, drawingSha);
        }
    }

    static string Key(BriefTextSource source) => JsonSerializer.Serialize(source);

    // An object with exactly the required members (any optional ones) and no others.
    static void Shape(JsonElement element, string path, string[] required, string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new Refusal(InputInvalid, path + " must be an object");
        foreach (var property in element.EnumerateObject())
            if (!required.Contains(property.Name, StringComparer.Ordinal) && !optional.Contains(property.Name, StringComparer.Ordinal))
                throw new Refusal(InputInvalid, path + ": unknown member " + property.Name);
        foreach (var name in required)
            if (!element.TryGetProperty(name, out _)) throw new Refusal(InputInvalid, path + ": missing member " + name);
    }

    static void SourceShape(JsonElement element, string path, bool nullable)
    {
        if (nullable && element.ValueKind == JsonValueKind.Null) return;
        Shape(element, path, SourceMembers, None);
        Shape(element.GetProperty("At"), path + ".At", PointMembers, PointOptional);
    }

    static void NoDuplicates(JsonElement element, string path, bool top = false)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray()) NoDuplicates(item, path + "[" + I(i++) + "]");
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new Refusal(InputInvalid, (path.Length == 0 ? "" : path + ": ") + "duplicate member " + property.Name);
            if (top && property.Name == "brief") continue;
            NoDuplicates(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name);
        }
    }

    static JsonElement Member(JsonElement root, string name, JsonValueKind kind)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new Refusal(InputInvalid, "\"" + name + "\" must be a JSON " + (kind == JsonValueKind.Object ? "object" : "array"));
        return value;
    }

    static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static (BriefPlannerOptions Options, BriefSite? Site, bool BriefOnly) ParseOptions(string[] args)
    {
        double? threshold = null, width = null, depth = null;
        string? name = null;
        var briefOnly = false;
        var rooms = new SortedSet<string>(StringComparer.Ordinal);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i += 2)
        {
            var option = args[i];
            if (option == "--brief-only")
            {
                if (briefOnly) throw new Refusal("cad_option_duplicate", option);
                briefOnly = true;
                i--; // a flag: the next argument is an option
                continue;
            }
            if (option is not ("--min-confidence" or "--confirm" or "--confirm-reason" or "--site-width" or "--site-depth" or "--name")) throw new Refusal("cad_option_unknown", option);
            if ((option == "--min-confidence" && threshold is not null) || (option == "--site-width" && width is not null)
                || (option == "--site-depth" && depth is not null) || (option == "--name" && name is not null))
                throw new Refusal("cad_option_duplicate", option);
            if (i + 1 >= args.Length) throw new Refusal("cad_option_value_missing", option);
            var value = args[i + 1];
            switch (option)
            {
                case "--min-confidence":
                    if (!(Number(value) is { } number && number >= 0 && number <= 1))
                        throw new Refusal("cad_option_value_invalid", option + ": a finite number in [0, 1]");
                    threshold = number == 0 ? 0.0 : number;
                    break;
                case "--site-width":
                case "--site-depth":
                    if (!(Number(value) is { } side && side >= Bim.Generative.PrototypeGenerator.MinLotSideMetres && side <= Bim.Generative.PrototypeGenerator.MaxLotSideMetres))
                        throw new Refusal("cad_option_value_invalid", option + ": metres, a finite number in [" + D(Bim.Generative.PrototypeGenerator.MinLotSideMetres) + ", " + D(Bim.Generative.PrototypeGenerator.MaxLotSideMetres) + "]");
                    if (option == "--site-width") width = side; else depth = side;
                    break;
                case "--name":
                    if (!BriefParser.IsText(value)) throw new Refusal("cad_option_value_invalid", option + ": non-blank text of at most 2048 UTF-16 code units");
                    name = value;
                    break;
                case "--confirm":
                    if (!BriefParser.IsRoomId(value)) throw new Refusal("cad_option_value_invalid", option + ": a brief room id ([a-z0-9][a-z0-9_-]{0,63})");
                    rooms.Add(value);
                    break;
                default:
                    if (!BriefPlannerCodes.All.Contains(value, StringComparer.Ordinal))
                        throw new Refusal("cad_option_value_invalid", option + ": one of " + string.Join(", ", BriefPlannerCodes.All));
                    reasons.Add(value);
                    break;
            }
        }
        if ((width is null) != (depth is null))
            throw new Refusal("cad_option_pair_missing", width is null ? "--site-depth needs --site-width" : "--site-width needs --site-depth");
        var options = new BriefPlannerOptions
        {
            MinConfidence = threshold ?? BriefPlannerOptions.DefaultMinConfidence,
            ConfirmedRooms = rooms.ToArray(),
            ConfirmedReasons = reasons.ToArray(),
            Name = name,
        };
        var site = width is { } w && depth is { } d ? new BriefSite(new BriefValue<double>(w), new BriefValue<double>(d)) : null;
        return (options, site, briefOnly);
    }

    // The option number grammar: invariant culture, optional sign, decimal point and exponent, no whitespace or
    // thousands separators; null when it does not parse or is not finite.
    static double? Number(string value) =>
        double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : null;

    static string D(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
