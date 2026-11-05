using Bim.Core;
namespace Bim.Revit;

// P1-10 element.rotate / element.mirror executor rules (red stub: signatures only, every answer wrong).
public sealed record PlanRotation(double CenterX, double CenterY, double AngleRad);
public sealed record PlanMirror(double StartX, double StartY, double EndX, double EndY);
public sealed record MirrorResult(string SourceUid, string UniqueId, bool IsNew);
public sealed record MirrorMapping(IReadOnlyList<MirrorResult> Results, IReadOnlyList<string> AlsoCreated, string? Mismatch);

public static class CommandRulesRotateMirror
{
    public static void CheckRotateTarget(TransformTargetFacts facts, IReadOnlyCollection<string> targetUids, string field) { }
    public static void CheckMirrorTarget(TransformTargetFacts facts, bool mirrorable, IReadOnlyCollection<string> targetUids, string field) { }
    public static PlanRotation RotationOf(ElementRotateArgs args) => new(0, 0, 0);
    public static PlanMirror MirrorOf(ElementMirrorArgs args) => new(0, 0, 0, 0);
    public static double[] RotatePoint(double[] point, PlanRotation rotation) => point;
    public static double[] MirrorPoint(double[] point, PlanMirror mirror) => point;
    public static TransformLocation Rotate(TransformLocation location, PlanRotation rotation) => location;
    public static TransformLocation Mirror(TransformLocation location, PlanMirror mirror) => location;
    public static string? CompareMirrored(TransformLocation expected, TransformLocation? observed, string what) => null;
    public static MirrorMapping MapMirrored(IReadOnlyList<TransformCandidate> sources, IReadOnlyList<TransformCandidate?> sourcesAfter, IReadOnlyList<TransformCandidate> created, PlanMirror mirror, bool keepOriginal) =>
        new(Array.Empty<MirrorResult>(), Array.Empty<string>(), null);
    public static IReadOnlyList<string> MirrorCreated(MirrorMapping mapping) => Array.Empty<string>();
    public static IReadOnlyList<string> MirrorModified(MirrorMapping mapping) => Array.Empty<string>();
}

public static class CommandReadbackRotateMirror
{
    public static string? Rotated(IReadOnlyList<string> uids, IReadOnlyList<TransformLocation> before, IReadOnlyList<TransformLocation?> after, PlanRotation rotation) => null;
    public static string RotateJson(IReadOnlyList<string> uids, IReadOnlyList<TransformLocation> before, IReadOnlyList<TransformLocation?> after, PlanRotation rotation) => "{}";
    public static string MirrorJson(MirrorMapping mapping, IReadOnlyList<TransformCandidate> sources, IReadOnlyList<TransformCandidate?> sourcesAfter, IReadOnlyList<TransformCandidate> created, PlanMirror mirror, bool keepOriginal) => "{}";
}
