#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bim.Cad;
using Bim.Core;
using Bim.Gltf;

namespace Bim.Cli;

// P6-10 `cad-plan-brief draft.json output.json [--min-confidence x] [--confirm id]... [--confirm-reason code]...`:
// a P6-07 `cad-brief` document (the P6-05 brief draft) becomes the P6-08 planner brief (BriefPlanner.ToPlannerBrief),
// written to a new JSON file. Nothing is persisted to a project store ("persisted": false).
//
// Options: --min-confidence x at most once (a finite number in [0, 1], default BriefPlannerOptions.DefaultMinConfidence);
// --confirm id and --confirm-reason code repeatable (ids must be brief room ids, BriefParser.IsRoomId; codes must be
// BriefPlannerCodes.All, case-sensitive). Repeats and order do not matter. A confirmed id naming no draft room is
// reported in unknown_confirmed_rooms, not refused.
//
// Input (bounded, BoundedInput.ReadBytes at BoundedInput.MaxBytes): a JSON object with "kind":"cad-brief", "version":1, "brief" (P6-03, read
// with BriefParser), "rooms" (BriefRoomProposal[]) and "uncertainties" (BriefExtractUncertainty[]) as cad-brief writes
// them. Uncertainty sources that equal a proposal label are re-bound to that label instance, so the planner's
// duplicate-label grouping sees the same identities as on the in-process draft.
//
// The output path follows GltfExportCommand.OutputProblem with extension .json and is checked before the input is read;
// options are checked before either. Every refusal prints one {"status":"failed","code":...[,"detail"]} envelope, exits
// 1 and writes nothing. Codes: usage, cad_option_unknown, cad_option_duplicate, cad_option_value_missing,
// cad_option_value_invalid, output_* (OutputProblem, output_exists, output_io_error), input_* (BoundedInput),
// cad_plan_brief_input_invalid (not a cad-brief document), brief_<code> (BriefParser refusal of the draft's brief).
//
// Output document (UTF-8, no BOM, compact, deterministic): {"kind":"cad-plan-brief","version":1,"persisted":false,
// "source":{"file","bytes","sha256","drawing_file","drawing_sha256"},"options":{"min_confidence","confirmed_rooms",
// "confirmed_reasons"} (distinct, ordinal order),"valid","brief" (BriefSerializer canonical text, verbatim),"kept",
// "dropped","unknown_confirmed_rooms","unknown_confirmed_reasons","issues"}. An invalid planner brief (for example every
// room dropped: rooms_empty) is still written with "valid": false and exit 0; the issues say why.
internal static class CadPlanBriefCommand
{
    public const string Usage = "usage: cad-plan-brief draft.json output.json [--min-confidence x] [--confirm id]... [--confirm-reason code]...";
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
        var options = ParseOptions(args);
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
        var result = BriefPlanner.ToPlannerBrief(draft, options);
        var briefJson = BriefSerializer.Serialize(result.Brief);

        byte[] written;
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

    // The cad-brief document as a BriefDraft. Structure errors are cad_plan_brief_input_invalid with a detail; the
    // brief itself goes through the strict P6-03 parser (brief_<code>).
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
            for (var i = 0; i < rooms.Length; i++)
            {
                var room = rooms[i];
                if (room is null || room.Id is null || room.Name is null || room.Notes is null || !double.IsFinite(room.Confidence))
                    throw new Refusal(InputInvalid, "rooms[" + I(i) + "] must be a proposal with Id, Name, Confidence and Notes");
            }
            var labels = new Dictionary<string, BriefTextSource>(StringComparer.Ordinal);
            foreach (var room in rooms)
                if (room!.Label is { } label) labels.TryAdd(Key(label), label);
            var bound = new BriefExtractUncertainty[uncertainties.Length];
            for (var i = 0; i < uncertainties.Length; i++)
            {
                var uncertainty = uncertainties[i];
                if (uncertainty is null || uncertainty.Code is null || uncertainty.Sources is null || uncertainty.Sources.Contains(null!))
                    throw new Refusal(InputInvalid, "uncertainties[" + I(i) + "] must be an uncertainty with Code and non-null Sources");
                var sources = uncertainty.Sources.Select(item => labels.TryGetValue(Key(item), out var known) ? known : item).ToArray();
                bound[i] = uncertainty with { Sources = sources };
            }
            var draft = new BriefDraft(sourceFile ?? "", format ?? "", metresPerUnit, brief, rooms.Select(r => r!).ToArray(), bound, BriefValidator.Validate(brief));
            return (draft, sourceFile, drawingSha);
        }
    }

    static string Key(BriefTextSource source) => JsonSerializer.Serialize(source);

    static JsonElement Member(JsonElement root, string name, JsonValueKind kind)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new Refusal(InputInvalid, "\"" + name + "\" must be a JSON " + (kind == JsonValueKind.Object ? "object" : "array"));
        return value;
    }

    static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static BriefPlannerOptions ParseOptions(string[] args)
    {
        double? threshold = null;
        var rooms = new SortedSet<string>(StringComparer.Ordinal);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i += 2)
        {
            var name = args[i];
            if (name is not ("--min-confidence" or "--confirm" or "--confirm-reason")) throw new Refusal("cad_option_unknown", name);
            if (name == "--min-confidence" && threshold is not null) throw new Refusal("cad_option_duplicate", name);
            if (i + 1 >= args.Length) throw new Refusal("cad_option_value_missing", name);
            var value = args[i + 1];
            switch (name)
            {
                case "--min-confidence":
                    if (!double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number)
                        || !double.IsFinite(number) || number < 0 || number > 1)
                        throw new Refusal("cad_option_value_invalid", name + ": a finite number in [0, 1]");
                    threshold = number == 0 ? 0.0 : number;
                    break;
                case "--confirm":
                    if (!BriefParser.IsRoomId(value)) throw new Refusal("cad_option_value_invalid", name + ": a brief room id ([a-z0-9][a-z0-9_-]{0,63})");
                    rooms.Add(value);
                    break;
                default:
                    if (!BriefPlannerCodes.All.Contains(value, StringComparer.Ordinal))
                        throw new Refusal("cad_option_value_invalid", name + ": one of " + string.Join(", ", BriefPlannerCodes.All));
                    reasons.Add(value);
                    break;
            }
        }
        return new BriefPlannerOptions
        {
            MinConfidence = threshold ?? BriefPlannerOptions.DefaultMinConfidence,
            ConfirmedRooms = rooms.ToArray(),
            ConfirmedReasons = reasons.ToArray(),
        };
    }

    static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
