namespace Bim.Core;

// Stub: canonical project brief v1 JSON (not yet implemented).
public static class BriefSerializer
{
    public static string Serialize(ProjectBrief brief) => brief is null ? "" : "{}";

    public static string Source(BriefSource source) => source.ToString();

    public static string Kind(BriefAdjacencyKind kind) => kind.ToString();
}
