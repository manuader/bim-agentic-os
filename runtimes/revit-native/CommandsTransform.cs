using Bim.Core;
namespace Bim.Revit;

// element.move / element.copy executor decisions, readback checks and readback JSON, in millimetres. Autodesk-free on
// purpose: tests/revit-commands source-links this file. CommandsExecutorTransform.cs reads the Revit facts and calls
// the Revit API. RED STUB (P1-09): the contract tests run against these placeholders first.

public static class TransformLocationKinds
{
    public const string Point = "point";
    public const string Curve = "curve";
    public const string Box = "box";
}

public sealed record TransformLocation(string Kind, double[] A, double[]? B, double? RotationRad = null);
public sealed record TransformTargetFacts(string Uid, bool Pinned, bool ViewSpecific, bool Grouped, bool HostedPlacement, bool HostPresent, TransformLocation? Location);
public sealed record TransformCandidate(string Uid, string Key, TransformLocation? Location);
public sealed record CopyPair(string SourceUid, string CopyUid);
public sealed record CopyMapping(IReadOnlyList<CopyPair> Pairs, IReadOnlyList<string> AlsoCreated, string? Mismatch);

public static class CommandRulesTransform
{
    public const double ToleranceMm = 1.0;

    public static void CheckTarget(TransformTargetFacts facts, string field) { }

    public static double[] Vector(TranslationVector vector) => new[] { vector.Dx.Millimetres, vector.Dy.Millimetres, vector.Dz.Millimetres };

    public static TransformLocation Shift(TransformLocation location, double[] vector) => location;

    public static CopyMapping MapCopies(IReadOnlyList<TransformCandidate> sources, IReadOnlyList<TransformCandidate> copies, double[] vector) =>
        new(Array.Empty<CopyPair>(), Array.Empty<string>(), null);

    public static IReadOnlyList<string> Created(CopyMapping mapping) => Array.Empty<string>();
}

public static class CommandReadbackTransform
{
    public static string? Moved(IReadOnlyList<string> uids, IReadOnlyList<TransformLocation> before, IReadOnlyList<TransformLocation?> after, double[] vector) => null;

    public static string? Copied(IReadOnlyList<TransformCandidate> sources, IReadOnlyList<TransformLocation?> sourcesAfter, IReadOnlyList<TransformCandidate> copies, double[] vector) => null;

    public static string MoveJson(IReadOnlyList<string> uids, IReadOnlyList<TransformLocation> before, IReadOnlyList<TransformLocation?> after, double[] vector) => "{}";

    public static string CopyJson(CopyMapping mapping, IReadOnlyList<TransformCandidate> copies, double[] vector) => "{}";
}
