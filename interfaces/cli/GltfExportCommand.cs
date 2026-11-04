#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Gltf;

namespace Bim.Cli;

// P8-07 `export-gltf project.json output.glb|output.gltf`: BIM-IR (bounded input, BoundedInput) to a new glTF 2.0
// file. The output path is bounded too: extension .glb or .gltf, an existing parent directory that is not a link, no
// link at the path itself, no Windows device namespace, no ':' after a drive designator (NTFS alternate data streams),
// and never an existing file or directory (the exporter creates
// it exclusively). Every refusal prints one JSON envelope and writes nothing. Contract: adapters/gltf/README.md.
internal static class GltfExportCommand
{
    public const string Usage = "usage: export-gltf project.json output.glb|output.gltf";

    public static void Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException(Usage);
        // Output is checked before the input is read, so a bad path costs no parse.
        if (OutputProblem(args[2], out var format) is string refused) { Fail(refused, null); return; }
        var text = BoundedInput.ReadText(args[1]);
        Project? project;
        try { project = JsonSerializer.Deserialize<Project>(text); }
        catch (JsonException) { Fail("bim_ir_json_invalid", null); return; }
        byte[] written;
        try { written = format == "glb" ? GltfExporter.ExportGlb(project!) : new UTF8Encoding(false).GetBytes(GltfExporter.ExportGltf(project!)); }
        catch (GltfExportException e) { Fail(e.Code, e.Detail); return; }
        try { GltfExporter.WriteNew(written, args[2]); }
        catch (GltfExportException e) { Fail(e.Code, null); return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail("output_io_error", e.GetType().Name); return; }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            format,
            output = args[2],
            bytes = written.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(written)),
            elements = project!.Elements.Length,
            openings = GltfExporter.OpeningCount(project),
        }));
    }

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }

    // Stable refusal code for an output path, or null; format is "glb" or "gltf" when accepted.
    internal static string? OutputProblem(string path, out string format) => OutputProblem(path, [".glb", ".gltf"], out format);

    // The same bounded output rules for any verb that writes one new file; extensions are lower-case with the dot, and
    // format is the accepted extension without it.
    internal static string? OutputProblem(string path, IReadOnlyCollection<string> extensions, out string format)
    {
        format = "";
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0) return "output_path_invalid";
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "output_path_invalid"; }
        if (OperatingSystem.IsWindows() && (DeviceNamespace(path) || DeviceNamespace(full))) return "output_not_regular_file";
        // No ':' after a drive designator, on every OS: on Windows "host.txt:model.glb" names an NTFS alternate data
        // stream of host.txt, not a new file, so it is never a valid output.
        if (HasStreamColon(path) || HasStreamColon(full)) return "output_path_invalid";
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!extensions.Contains(extension)) return "output_extension_unsupported";
        format = extension[1..];
        try
        {
            // A link at the path (even dangling) is never followed or replaced.
            if (new FileInfo(full).LinkTarget is not null) return "output_link_rejected";
            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return "output_directory_not_found";
            if (new DirectoryInfo(parent).LinkTarget is not null) return "output_link_rejected";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "output_path_invalid"; }
        if (File.Exists(full) || Directory.Exists(full)) return "output_exists";
        return null;
    }

    // True when path holds a ':' other than a Windows drive designator ("C:" at the start, Windows only).
    internal static bool HasStreamColon(string path)
    {
        var start = OperatingSystem.IsWindows() && path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? 2 : 0;
        return path.IndexOf(':', start) >= 0;
    }

    static bool DeviceNamespace(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.StartsWith(@"\\.\", StringComparison.Ordinal) || normalized.StartsWith(@"\\?\", StringComparison.Ordinal);
    }
}
