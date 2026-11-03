using Bim.Core;

namespace Bim.Gltf;

public sealed class GltfExportException(string code, string detail) : Exception(code + ": " + detail)
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
}

// TDD stub: the public surface only; the mapping is not implemented yet.
public static class GltfExporter
{
    public const string Schema = "bim-agentic-os.gltf/1";
    public const string Generator = "bim-agentic-os Bim.Gltf";
    public const int MaxElements = 10_000;
    public static byte[] ExportGlb(Project project) => throw new NotImplementedException();
    public static string ExportGltf(Project project) => throw new NotImplementedException();
    public static void WriteGlb(Project project, string path) => throw new NotImplementedException();
}
