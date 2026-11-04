using Bim.Core;
namespace Bim.Cad;

// Draft of a project brief (P6-03 schema, Bim.Core.ProjectBrief) proposed from the room labels and area texts of a DXF
// drawing or PDF page (see BriefExtract). A draft is a proposal only: nothing is persisted and neither the BIM-IR nor
// the brief schema changes. Each proposed room carries a confidence in (0, 1], where its label, its stated area and its
// closed region came from, and notes on what was assumed; the uncertainty report lists what could not be resolved.

// Where a fact came from. Format is "dxf" or "pdf". DXF: Handle (null when the file has none), effective Layer,
// EntityType (TEXT, MTEXT, LWPOLYLINE), Line = the 1-based DXF line of the entity's "0" group, InsertPath = enclosing
// INSERT handles (outermost first). PDF: Page, Handle "p{page}:o{object}:i{operator}" (see PdfPage.ToDxfEntities),
// Layer "PDF-P{page}", EntityType PDF-TEXT or PDF-PATH, Line = operator index + 1. At is the text insertion point, or
// the centre of a region's vertex bounding box, in drawing units (PDF: calibrated millimetres when a calibration was
// given). Text is the plain text (whitespace collapsed, at most MaxTextLength) for text sources and null for regions.
public sealed record BriefTextSource(string SourceFile, string Format, string? Handle, string Layer, string EntityType, int Line, int? Page, IReadOnlyList<string> InsertPath, Point2 At, string? Text);

// One proposed room. Id and Name are the brief room's; StatedAreaSquareMetres is the area written on the drawing (null
// when none was found); RegionAreaSquareMetres the area of the closed region that holds the label and no other label
// (null without such a region or without a drawing scale). Notes: area-same-text, area-nearby, area-from-region,
// area-missing, area-mismatch, no-region, shared-region, label-outside-vocabulary, duplicate-label.
public sealed record BriefRoomProposal(string Id, string Name, double? StatedAreaSquareMetres, double? RegionAreaSquareMetres, double Confidence,
    BriefTextSource Label, BriefTextSource? Area, BriefTextSource? Region, IReadOnlyList<string> Notes);

// One entry of the uncertainty report. Codes: area-from-region, area-invalid, area-mismatch, area-missing,
// duplicate-label, duplicate-text, orphan-area, region-limit, shared-region, text-limit, text-too-long,
// unlabelled-region, work-limit, and uncertainty-limit (last, when the report was capped). RoomId names the affected
// proposal; ValueSquareMetres is the region area (area-mismatch, area-from-region, unlabelled-region, shared-region) or
// the rejected value (area-invalid, orphan-area) when applicable.
public sealed record BriefExtractUncertainty(string Code, string Message, string? RoomId, double? ValueSquareMetres, IReadOnlyList<BriefTextSource> Sources);

// The draft. MetresPerUnit is the drawing scale used for region areas (null: uncalibrated, so region areas are unknown).
// Brief is a P6-03 ProjectBrief (rooms in label drawing order, no adjacencies) and Issues is BriefValidator.Validate of
// it. Uncertainties are sorted by code (ordinal), then in discovery order.
public sealed record BriefDraft(string SourceFile, string Format, double? MetresPerUnit, ProjectBrief Brief, IReadOnlyList<BriefRoomProposal> Rooms,
    IReadOnlyList<BriefExtractUncertainty> Uncertainties, IReadOnlyList<BriefIssue> Issues);

// What one text says: Label (letters remaining once the area and its prefix are removed; null when none, or when its
// only letters are units) and the area in square metres (null when the text has no area token; negative when the token
// has a leading minus sign). AreaToken is the matched token. AreaAmbiguous is true when the number has a single
// separator followed by exactly three digits ("1.234", "1,234"): read as a decimal, but it may be a thousands group.
public sealed record BriefTextReading(string? Label, double? AreaSquareMetres, string? AreaToken, bool AreaAmbiguous = false);

// Options. MetresPerUnit overrides the drawing scale (DXF $INSUNITS; for PDF the raw page unit when no calibration is
// given). ExplodeOptions bound the DXF block explosion. Closed polylines drawn through a block insert or Form XObject
// (furniture, symbols) are regions only when IncludeBlockRegions; block texts are always read. Name defaults to "Brief draft from {source file}". TextLayers/RegionLayers restrict the layers read
// (case-insensitive; null = all). A label is at most MaxLabelLength characters and MaxLabelWords words. An area text
// pairs with a label when both lie in the same smallest closed region, or, both outside every region, when they are at
// most PairDistanceTextHeights times the larger text height apart. A region area differing from the stated area by more
// than AreaMismatchTolerance (relative to the region) is area-mismatch. Regions smaller than MinRegionAreaSquareMetres
// are ignored when the scale is known. Stated areas outside (0, MaxAreaSquareMetres] are area-invalid. RoomWords adds
// vocabulary words (matched like the built-in es/en list, case- and accent-insensitive). Max* bound the work; a bound
// stops that part of the extraction and is reported (text-limit, region-limit, work-limit, uncertainty-limit).
public sealed record BriefExtractOptions
{
    public string? Name { get; init; }
    public IReadOnlyCollection<string>? TextLayers { get; init; }
    public IReadOnlyCollection<string>? RegionLayers { get; init; }
    public IReadOnlyCollection<string> RoomWords { get; init; } = Array.Empty<string>();
    public bool IncludePaperSpace { get; init; }
    public bool IncludeInvisible { get; init; }
    public bool IncludeBlockRegions { get; init; }
    public double? MetresPerUnit { get; init; }
    public DxfExplodeOptions? ExplodeOptions { get; init; }
    public double PairDistanceTextHeights { get; init; } = 4;
    public double AreaMismatchTolerance { get; init; } = 0.10;
    public double MinRegionAreaSquareMetres { get; init; } = 1.0;
    public double MaxAreaSquareMetres { get; init; } = 100_000;
    public int MaxTexts { get; init; } = 10_000;
    public int MaxTextLength { get; init; } = 256;
    public int MaxLabelLength { get; init; } = 64;
    public int MaxLabelWords { get; init; } = 6;
    public int MaxRegions { get; init; } = 5_000;
    public int MaxRegionVertices { get; init; } = 200_000;
    public int MaxPairCandidates { get; init; } = 100_000;
    public long MaxWork { get; init; } = 20_000_000;
    public int MaxUncertainties { get; init; } = 1_000;
}
