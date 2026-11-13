#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Bim.Core;
using Bim.Gltf;

namespace Bim.Cli;

// P10-20 `bcf-export issues.json output.bcfzip|output.bcf`: the P10-02 BCF 3.0 export (core/Bim.Core/Bcf.cs, BcfZip.cs)
// of a neutral validation-issues file, written to a new archive. Offline: no store, no pipe, no Revit.
// Input (CLI-side format, read strictly through BoundedInput.ReadText, 8 MiB; UTF-8, BOM tolerated):
//   {"kind":"bcf-issues","version":1,
//    "project":{"id","name","author","timestamp"[,"originating_system"]},
//    "issues":[{"id","title"[,"description"],"severity":"error"|"warning"|"info"[,"rule_id"][,"element_ids":[...]]
//               [,"camera":{"kind":"perspective"|"orthogonal","view_point":{"x","y","z"},"direction":{...},
//                           "up_vector":{...},"value","aspect_ratio"}]}]}
//   Members in any order, each at most once, no unknown members, no comments or trailing commas; optional members may be
//   null (same as absent). timestamp is yyyy-MM-ddTHH:mm:ssZ or yyyy-MM-ddTHH:mm:ss+hh:mm (the library normalizes to UTC).
//   Each value maps one-to-one onto BcfExportOptions / BcfIssue / BcfCamera; every value rule beyond JSON types is the
//   library's (BcfExport.Entries), so its refusals pass through with their code and detail.
// Output: exactly BcfZip.Write(BcfExport.Entries(issues, options), options.Timestamp), i.e. BcfExport.WriteZip, byte for
// byte. The path follows GltfExportCommand.OutputProblem (.bcfzip or .bcf; checked before the input is read) and is
// created exclusively (GltfExporter.WriteNew, FileMode.CreateNew): an existing file or directory is never overwritten.
// Success prints {"status":"succeeded","output","issues","entries","bytes","sha256"} (sha256 upper-case hex) and exits 0.
// Refusals print one {"status":"failed","code"[,"detail"]} line, exit 1 and write no file: bcf_export_usage (detail the
// usage text), bcf_export_output_exists, bcf_export_output_invalid (detail the output_* code), bcf_export_input_missing,
// bcf_export_input_too_large, bcf_export_input_unreadable (detail the input_* code), bcf_export_input_invalid (detail
// "json" or the JSON path of the first problem), the library's bcf_* codes (BcfCodes) with their detail,
// bcf_export_output_io_error and bcf_export_internal_error (a defect: type and message on stderr).
// Precedence: usage, output path, input read, JSON shape, library, write.
internal static class BcfExportCommand
{
    public const string Usage = "usage: bcf-export issues.json output.bcfzip|output.bcf (output: a new file, never overwritten)";
    public const string UsageCode = "bcf_export_usage";
    public const string OutputExists = "bcf_export_output_exists";
    public const string OutputInvalid = "bcf_export_output_invalid";
    public const string OutputIoError = "bcf_export_output_io_error";
    public const string InputMissing = "bcf_export_input_missing";
    public const string InputTooLarge = "bcf_export_input_too_large";
    public const string InputUnreadable = "bcf_export_input_unreadable";
    public const string InputInvalid = "bcf_export_input_invalid";
    public const string InternalError = "bcf_export_internal_error";
    public const string Kind = "bcf-issues";
    public const int Version = 1;

    static readonly string[] Extensions = [".bcfzip", ".bcf"];
    static readonly string[] TimestampFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:sszzz"];

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    public static void Run(string[] args)
    {
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
        catch (Exception error)
        {
            Console.Error.WriteLine("bcf-export internal error: " + error.GetType().FullName + ": " + error.Message);
            Fail(InternalError, null);
        }
    }

    static void Execute(string[] args)
    {
        if (args.Length != 3 || args.Skip(1).Any(a => a.Length == 0 || a.StartsWith("--", StringComparison.Ordinal))) throw new Refusal(UsageCode, Usage);
        var output = args[2];
        if (GltfExportCommand.OutputProblem(output, Extensions, out _) is string badOutput)
            throw badOutput == "output_exists" ? new Refusal(OutputExists) : new Refusal(OutputInvalid, badOutput);

        if (args[1].IndexOf('\0') >= 0) throw new Refusal(InputUnreadable, "input_path_invalid");
        string text;
        try { text = BoundedInput.ReadText(args[1]); }
        catch (InputRejectedException e)
        {
            throw e.Message switch
            {
                BoundedInput.NotFound => new Refusal(InputMissing),
                BoundedInput.TooLarge => new Refusal(InputTooLarge),
                _ => new Refusal(InputUnreadable, e.Message),
            };
        }

        var (options, issues) = Parse(text);
        byte[] bytes;
        int entries;
        try
        {
            var list = BcfExport.Entries(issues, options);
            entries = list.Count;
            bytes = BcfZip.Write(list, options.Timestamp);
        }
        catch (BcfExportException error) { throw new Refusal(error.Code, error.Detail); }

        var line = JsonSerializer.Serialize(new
        {
            status = "succeeded",
            output,
            issues = issues.Count,
            entries,
            bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        });
        try { GltfExporter.WriteNew(bytes, output); }
        catch (GltfExportException error) when (error.Code == "output_exists") { throw new Refusal(OutputExists); }
        catch (GltfExportException error) { throw new Refusal(OutputIoError, error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal(OutputIoError, error.GetType().Name); }
        Console.WriteLine(line);
    }

    // ---- strict JSON shape ----

    static (BcfExportOptions Options, IReadOnlyList<BcfIssue> Issues) Parse(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }); }
        catch (JsonException) { throw new Refusal(InputInvalid, "json"); }
        using (document)
        {
            var root = Members(document.RootElement, "$", ["kind", "version", "project", "issues"]);
            if (Str(Required(root, "kind", "$"), "$.kind") != Kind) throw new Refusal(InputInvalid, "$.kind");
            var version = Required(root, "version", "$");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != Version) throw new Refusal(InputInvalid, "$.version");
            var options = Options(Required(root, "project", "$"), "$.project");
            var issuesElement = Required(root, "issues", "$");
            if (issuesElement.ValueKind != JsonValueKind.Array) throw new Refusal(InputInvalid, "$.issues");
            var issues = new List<BcfIssue>(issuesElement.GetArrayLength());
            var index = 0;
            foreach (var item in issuesElement.EnumerateArray()) { issues.Add(Issue(item, "$.issues[" + index.ToString(CultureInfo.InvariantCulture) + "]")); index++; }
            return (options, issues);
        }
    }

    static BcfExportOptions Options(JsonElement element, string path)
    {
        var members = Members(element, path, ["id", "name", "author", "timestamp", "originating_system"]);
        var id = Str(Required(members, "id", path), path + ".id");
        var name = Str(Required(members, "name", path), path + ".name");
        var author = Str(Required(members, "author", path), path + ".author");
        var stampText = Str(Required(members, "timestamp", path), path + ".timestamp");
        if (!DateTimeOffset.TryParseExact(stampText, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            throw new Refusal(InputInvalid, path + ".timestamp");
        var system = OptionalStr(members, "originating_system", path);
        return new BcfExportOptions(id, name, author, timestamp, system);
    }

    static BcfIssue Issue(JsonElement element, string path)
    {
        var members = Members(element, path, ["id", "title", "description", "severity", "rule_id", "element_ids", "camera"]);
        var id = Str(Required(members, "id", path), path + ".id");
        var title = Str(Required(members, "title", path), path + ".title");
        var description = OptionalStr(members, "description", path);
        var severity = Str(Required(members, "severity", path), path + ".severity") switch
        {
            "error" => BcfSeverity.Error,
            "warning" => BcfSeverity.Warning,
            "info" => BcfSeverity.Info,
            _ => throw new Refusal(InputInvalid, path + ".severity"),
        };
        var ruleId = OptionalStr(members, "rule_id", path);
        List<string>? elementIds = null;
        if (members.TryGetValue("element_ids", out var elementsElement) && elementsElement.ValueKind != JsonValueKind.Null)
        {
            if (elementsElement.ValueKind != JsonValueKind.Array) throw new Refusal(InputInvalid, path + ".element_ids");
            elementIds = new List<string>(elementsElement.GetArrayLength());
            var i = 0;
            foreach (var item in elementsElement.EnumerateArray()) { elementIds.Add(Str(item, path + ".element_ids[" + i.ToString(CultureInfo.InvariantCulture) + "]")); i++; }
        }
        BcfCamera? camera = null;
        if (members.TryGetValue("camera", out var cameraElement) && cameraElement.ValueKind != JsonValueKind.Null) camera = Camera(cameraElement, path + ".camera");
        return new BcfIssue(id, title, description, severity, ruleId, elementIds, camera);
    }

    static BcfCamera Camera(JsonElement element, string path)
    {
        var members = Members(element, path, ["kind", "view_point", "direction", "up_vector", "value", "aspect_ratio"]);
        var kind = Str(Required(members, "kind", path), path + ".kind") switch
        {
            "perspective" => BcfCameraKind.Perspective,
            "orthogonal" => BcfCameraKind.Orthogonal,
            _ => throw new Refusal(InputInvalid, path + ".kind"),
        };
        var viewPoint = Vector(Required(members, "view_point", path), path + ".view_point");
        var direction = Vector(Required(members, "direction", path), path + ".direction");
        var upVector = Vector(Required(members, "up_vector", path), path + ".up_vector");
        var value = Number(Required(members, "value", path), path + ".value");
        var aspect = Number(Required(members, "aspect_ratio", path), path + ".aspect_ratio");
        return new BcfCamera(kind, viewPoint, direction, upVector, value, aspect);
    }

    static BcfVector Vector(JsonElement element, string path)
    {
        var members = Members(element, path, ["x", "y", "z"]);
        return new BcfVector(Number(Required(members, "x", path), path + ".x"), Number(Required(members, "y", path), path + ".y"), Number(Required(members, "z", path), path + ".z"));
    }

    // The object's members by name; a non-object, an unknown or a repeated member (or a name that cannot be decoded) is
    // refused at its path, in document order.
    static Dictionary<string, JsonElement> Members(JsonElement element, string path, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new Refusal(InputInvalid, path);
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            string name;
            try { name = property.Name; }
            catch (InvalidOperationException) { throw new Refusal(InputInvalid, path); }
            var at = path + "." + name;
            if (!allowed.Contains(name, StringComparer.Ordinal) || !members.TryAdd(name, property.Value)) throw new Refusal(InputInvalid, at);
        }
        return members;
    }

    static JsonElement Required(Dictionary<string, JsonElement> members, string name, string path) =>
        members.TryGetValue(name, out var value) ? value : throw new Refusal(InputInvalid, path + "." + name);

    static string? OptionalStr(Dictionary<string, JsonElement> members, string name, string path) =>
        members.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null ? Str(value, path + "." + name) : null;

    static string Str(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String) throw new Refusal(InputInvalid, path);
        try { return element.GetString()!; }
        catch (InvalidOperationException) { throw new Refusal(InputInvalid, path); }
    }

    static double Number(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) ? value : throw new Refusal(InputInvalid, path);

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
