namespace Bim.Core;

// P1-10 element.rotate and element.mirror (red stub: contract types only, not in the catalogue yet).
public sealed record ElementRotateArgs(IReadOnlyList<string> ElementUids, Point2 Center, Angle Angle) : RevitCommandArgs;
public sealed record ElementMirrorArgs(IReadOnlyList<string> ElementUids, Point2 AxisStart, Point2 AxisEnd, bool KeepOriginal) : RevitCommandArgs;

public static class RevitRotateMirrorRules
{
    public const string Rotate = "element.rotate";
    public const string Mirror = "element.mirror";
    public const int MinTargets = 1;
    public const int MaxTargets = 100;
    public const double MinAngleDeg = 0.01;
    public const double MaxAngleDeg = 360;
}

public static class RevitRotateMirrorCodes
{
    public const string ElementNotRotatable = "element_not_rotatable";
    public const string ElementNotMirrorable = "element_not_mirrorable";
    public static IReadOnlyList<string> All { get; } = new[] { ElementNotRotatable, ElementNotMirrorable };
}

public static partial class RevitCommandCatalogue
{
    public static IReadOnlyList<RevitCommandSpec> RotateMirror { get; } = Array.Empty<RevitCommandSpec>();
}
