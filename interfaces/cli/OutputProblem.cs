#nullable enable
namespace Bim.Cli;

// P4-24: the bounded output-path rule of export-gltf, shared by every verb that writes one new file (doc-update,
// doc-realized, generate-typology, generate-prototypes, plan-layouts, mirror-drift, audit-verify, cad, bcf-export, ...).
// It is a part of GltfExportCommand so every caller keeps calling GltfExportCommand.OutputProblem; the verb itself (Run,
// the bounded read, the exporter and the exclusive write) is the other part, GltfExportCommand.cs, which the MCP server
// does not compile.
internal static partial class GltfExportCommand
{
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
