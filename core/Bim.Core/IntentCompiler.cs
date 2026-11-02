namespace Bim.Core;

// P3-01 stub: public surface only; behaviour lands after the red CI run.
public static class IntentCompiler
{
    public const double Tolerance = 1e-6;

    public static Guid Id(Guid projectId, string intentKey) => RoomCompiler.Id(projectId, intentKey);
    public static string LevelKey(string level) => "level:" + level;
    public static string SpaceKey(string room) => "space:" + room;
    public static string OpeningKey(string opening) => "opening:" + opening;
    public static string WallKey(string room, IntentSide side, int segment = 0) => "wall:" + room + ":" + side + ":" + segment.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string SharedWallKey(string roomA, string roomB) => "wall:" + roomA + "|" + roomB;

    public static IntentIssue[] Validate(LayoutIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Array.Empty<IntentIssue>();
    }

    public static Project Compile(Guid projectId, int revision, LayoutIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new Project(projectId, revision, Array.Empty<Element>());
    }
}
