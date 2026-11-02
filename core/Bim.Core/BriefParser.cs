namespace Bim.Core;

// Stub: structural reader for project brief v1 (not yet implemented).
public static class BriefParser
{
    public const int MaxInputChars = 262144;
    public const int MaxDepth = 16;
    public const int MaxStringChars = 2048;

    public static bool IsRoomId(string? value) => value is not null;

    public static ProjectBrief Parse(string json) =>
        new(json.Length, "", new BriefConstraints(), Array.Empty<BriefRoom>(), Array.Empty<BriefAdjacency>());
}
