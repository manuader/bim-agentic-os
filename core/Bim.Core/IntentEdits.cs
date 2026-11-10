namespace Bim.Core;

// P3-09 (stub): intent edits to element edits. Implementation follows the red test commit.
public sealed record IntentEditSet(Project Before, Project After, ElementEdit[] Edits);

public static class IntentEdits
{
    public const string UnknownOpening = "unknown_opening";

    public static IntentEditSet From(Guid projectId, int revision, LayoutIntent before, LayoutIntent after, int maxEdits = IncrementalDiff.MaxEdits) =>
        throw new NotImplementedException();

    public static LayoutIntent ResizeRoom(LayoutIntent intent, string room, double xMetres, double yMetres, double widthMetres, double depthMetres) =>
        throw new NotImplementedException();

    public static LayoutIntent MoveOpening(LayoutIntent intent, string opening, double offsetMetres, IntentHost? host = null) =>
        throw new NotImplementedException();

    public static LayoutIntent ChangeWallType(LayoutIntent intent, string room, string wallType) =>
        throw new NotImplementedException();
}
