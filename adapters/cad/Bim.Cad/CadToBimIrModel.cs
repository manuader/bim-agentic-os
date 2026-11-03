using BimElement = Bim.Core.Element;
using BimProject = Bim.Core.Project;
namespace Bim.Cad;

// Draft of BIM-IR elements proposed from a calibrated CAD drawing (see CadToBimIr). A draft is a proposal only: it is
// never persisted (no store or filesystem writes), and nothing here changes the BIM-IR schema. Each proposal carries an
// existing Bim.Core.Element (persisted layout, metres: level [elevation]; wall [ax,ay,bx,by,height,thickness] on the
// axis; door/window [ax,ay,bx,by,sill,height] = the opening's stretch of the host axis, depending on [level, host]) plus
// the same geometry in millimetres, a confidence in (0, 1], where it came from and notes on what was assumed.

// A plan point in millimetres (drawing coordinates times the calibration scale).
public readonly record struct CadDraftPoint(double X, double Y);

// Where a proposal came from. Format is "dxf" or "pdf". DXF: Handle, Layer (effective), EntityType, Line (1-based DXF
// line of the entity), InsertPath (enclosing INSERT handles, outermost first) and SegmentIndex (LWPOLYLINE segment;
// null for a LINE). PDF: Page, ObjectNumber (content stream or Form XObject holding the operator), OperatorIndex (the
// painting operator of the path, or the Do operator for a form placement), Handle "p{page}:o{object}:i{operator}",
// Layer "PDF-P{page}", EntityType PDF-PATH or PDF-DO, InsertPath = the Do invocation chain, SegmentIndex the polyline
// segment, and Part the 0-based ordinal of the drafted entity among those converted from that one painted path (its
// subpaths); Line is null. Part is 0 for DXF.
public sealed record CadDraftSource(string SourceFile, string Format, string? Handle, string Layer, string EntityType, int? Line, int? Page, int? ObjectNumber, int? OperatorIndex, int? SegmentIndex, int Part, IReadOnlyList<string> InsertPath);

// A proposed wall. Start/End is the axis (midway between the paired face lines) after merging collinear pieces and
// closing joins; Sources lists the face lines of every merged piece (A side then B side, in drawing order). Notes:
// merged-across-opening, merged-split-lines, merged-shared-face, ambiguous-thickness, ambiguous-pairing,
// height-assumed, and for each end "start:"/"end:" + joined | gap | unclosed.
public sealed record CadWallProposal(string Key, BimElement Element, CadDraftPoint Start, CadDraftPoint End, double ThicknessMillimetres, double HeightMillimetres, double Confidence, IReadOnlyList<CadDraftSource> Sources, IReadOnlyList<string> Notes);

// A proposed door or window hosted on a proposed wall. BimIR has door/window elements (IntentCompiler layout), so the
// proposal carries one. Start/End is its stretch on the host axis in the host's direction; sill and height are the
// option defaults (a plan does not show them; noted as sill-assumed / height-assumed). Source is the DXF INSERT or the
// PDF Do operator; BlockName is the DXF block name or "form:o{object}" for a PDF Form XObject.
public sealed record CadOpeningProposal(string Key, string Kind, BimElement Element, string HostKey, CadDraftPoint Start, CadDraftPoint End, double WidthMillimetres, double SillMillimetres, double HeightMillimetres, double Confidence, string BlockName, CadDraftSource Source, IReadOnlyList<string> Notes);

// One entry of the uncertainty report. Codes: ambiguous-host, ambiguous-pairing, ambiguous-thickness, gap,
// opening-beyond-host, unclosed-join, unhosted-opening, unmatched-block, unpaired-line, and uncertainty-limit (last,
// when the report was capped). ProposalKey names the affected proposal, At the location in millimetres and
// ValueMillimetres the alternative thickness (ambiguous-*), the gap distance (gap) or the overlap length
// (opening-overlap) when applicable.
public sealed record CadUncertainty(string Code, string Message, string? ProposalKey, CadDraftPoint? At, double? ValueMillimetres, IReadOnlyList<CadDraftSource> Sources);

// The draft. MillimetresPerUnit is the drawing-unit scale used. Uncertainties are sorted by code (ordinal), then in
// discovery order. ToProject assembles a BIM-IR Project in memory only (level, walls, openings); it is not persisted.
public sealed record CadDraft(string SourceFile, string Format, double MillimetresPerUnit, Guid ProjectId, BimElement Level, IReadOnlyList<CadWallProposal> Walls, IReadOnlyList<CadOpeningProposal> Openings, IReadOnlyList<CadUncertainty> Uncertainties)
{
    public BimProject ToProject(int revision = 0)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision), "revision must be non-negative");
        var elements = new List<BimElement>(1 + Walls.Count + Openings.Count) { Level };
        elements.AddRange(Walls.Select(w => w.Element));
        elements.AddRange(Openings.Select(o => o.Element));
        return new BimProject(ProjectId, revision, elements.ToArray());
    }
}

// Options. Lengths are millimetres. Walls are WallPairDetector pairs with thickness in [MinThickness, MaxThickness] and
// overlap at least MinOverlap; JoinTolerance is the slack for collinearity, touching pieces, joins and opening bands;
// a wall end that does not join may report a gap when another wall is within MaxGap beyond the join reach.
// DoorBlocks/WindowBlocks are DXF block-name patterns (case-insensitive; a trailing * matches a prefix); PdfDoorForms
// and PdfWindowForms list Form XObject object numbers. Both are matched on the outermost enclosing INSERT / form, so a
// door nested inside a plan block is still a door. MinThickness and MinOverlap must exceed JoinTolerance, a door's sill
// must be 0 and a sill plus its height must stay within WallHeight, as the core opening rules require. Geometry of other blocks/forms is reported as unmatched-block
// and only used for walls when WallsFromUnmatchedBlocks. MillimetresPerUnit overrides the DXF $INSUNITS scale (it is
// required when the DXF has none). Max* bound the work; exceeding one raises CadDraftException "draft-limit".
public sealed record CadToBimIrOptions(Guid ProjectId)
{
    public double MinThicknessMillimetres { get; init; } = 50;
    public double MaxThicknessMillimetres { get; init; } = 500;
    public double MinOverlapMillimetres { get; init; } = 300;
    public double AngleToleranceDegrees { get; init; } = 1.0;
    public double JoinToleranceMillimetres { get; init; } = 20;
    public double MaxGapMillimetres { get; init; } = 1200;
    public double? MillimetresPerUnit { get; init; }
    public IReadOnlyCollection<string>? WallLayers { get; init; }
    public IReadOnlyCollection<string> DoorBlocks { get; init; } = new[] { "DOOR*", "PUERTA*" };
    public IReadOnlyCollection<string> WindowBlocks { get; init; } = new[] { "WINDOW*", "VENTANA*" };
    public IReadOnlyCollection<int> PdfDoorForms { get; init; } = Array.Empty<int>();
    public IReadOnlyCollection<int> PdfWindowForms { get; init; } = Array.Empty<int>();
    public bool WallsFromUnmatchedBlocks { get; init; }
    public bool IncludePaperSpace { get; init; }
    public bool IncludeInvisible { get; init; }
    public bool SkipHiddenLayers { get; init; }
    public double LevelElevationMillimetres { get; init; }
    public double WallHeightMillimetres { get; init; } = 2700;
    public double DoorHeightMillimetres { get; init; } = 2100;
    public double DoorSillMillimetres { get; init; }
    public double WindowHeightMillimetres { get; init; } = 1200;
    public double WindowSillMillimetres { get; init; } = 900;
    public string Approval { get; init; } = "draft-unapproved";
    public int CurveSegments { get; init; } = 16;
    public int MaxSegments { get; init; } = 200_000;
    public int MaxCandidatePairs { get; init; } = 100_000;
    public long MaxComparisons { get; init; } = 50_000_000;
    public int MaxWalls { get; init; } = 20_000;
    public int MaxOpenings { get; init; } = 5_000;
    public int MaxOpeningPrimitives { get; init; } = 200_000;
    public long MaxWork { get; init; } = 20_000_000;
    public int MaxUncertainties { get; init; } = 10_000;
}

// Refusals: uncalibrated (no drawing scale), page-missing, draft-limit (a Max* bound, including the wall-pair detector's
// and the exploder's limits).
public sealed class CadDraftException : Exception
{
    public CadDraftException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
