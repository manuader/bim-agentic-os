using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bim.Core;
namespace Bim.Cad;

// P6-05: room labels and area texts of a DXF drawing or PDF page to a draft project brief (see BriefDraft). Nothing is
// persisted and neither the BIM-IR nor the brief schema changes; the result is a proposal with an uncertainty report.
//
// 1. Texts. TEXT/MTEXT plain text (DXF, after exploding INSERTs; explosion warnings are explode-warning, a failed
//    explosion is explode-limit and falls back to top-level entities) or shown PDF strings (PdfPage.ToDxfEntities),
//    filtered by TextLayers, paper space and visibility, in drawing order. A text longer than MaxTextLength is
//    text-too-long; a text equal (after whitespace collapsing) to an earlier one of the same height whose insertion is
//    within 1 % of that height is duplicate-text and dropped (grid lookup, linear time).
//    ReadText splits each text into an area token (a minus sign touching the number at the start of the text or after
//    ':' / '=' makes it negative; a spaced dash as in "Cocina - 12 m2" is a separator; number with decimal comma or point,
//    optional thousands separators "1.234,5" / "1,234.5", unit m², m2, m^2, mt2, mts2, sq m, sqm; a single separator is
//    always decimal, and "1.234" / "1,234" (non-zero integer part, exactly three fraction digits) is area-ambiguous) and
//    a label (the rest,
//    minus an area prefix Sup., Superficie, Surface, Área/Area or S:, that has a letter other than units, at most
//    MaxLabelLength characters and MaxLabelWords words). Areas outside (0, MaxAreaSquareMetres] are area-invalid.
// 2. Regions. Closed LWPOLYLINEs (DXF) or closed subpaths (PDF), filtered by RegionLayers, paper space and visibility,
//    and, unless IncludeBlockRegions, not drawn through a block insert or Form XObject; bulges tessellated for
//    containment and integrated exactly for area (gross: holes are not subtracted). Without a scale (MetresPerUnit
//    option, DXF units or PDF calibration) the report starts with uncalibrated and region areas are unknown; with one,
//    regions smaller than MinRegionAreaSquareMetres are ignored. A text belongs to the smallest region holding its
//    insertion point. A region that also holds a label whose smallest region is another one encloses a labelled region.
// 3. Labels. A label is kept when it has a room word (built-in es/en vocabulary plus RoomWords, case- and
//    accent-insensitive, plural -s/-es accepted, multi-word RoomWords matched as phrases, common es abbreviations dorm,
//    hab, coc, lav, dep, est, circ, liv, com), or when it lies in a region that holds no room-word label and encloses no
//    labelled region (label-outside-vocabulary; frames and outlines are thus never rooms). Dropped labels free their
//    area token, if any. An outer region is one enclosing the smallest region of a kept label (a plain unlabelled
//    polyline such as furniture inside a room does not make the room outer).
// 4. Areas. A kept label with its own area token uses it (area-same-text). The other labels take free area texts
//    greedily by distance (then label and area order): candidates share the smallest region (an outer region also
//    needs the distance cut), or both lie outside every region, within PairDistanceTextHeights x the larger text height
//    (area-nearby). At most MaxPairCandidates candidates are considered (pair-limit). Unused area texts are orphan-area.
// 5. Rooms. A region holding exactly one kept label (as smallest region) that is not an outer region is that room's
//    region; otherwise shared-region or outer-region (no region area). Without a stated area the room takes its region
//    area as an inferred target (area-from-region) or has none (area-missing). A stated area off the region area by more
//    than AreaMismatchTolerance is area-mismatch; the stated area stays the target. Labels whose slug repeats an earlier
//    one are duplicate-label (both kept; the later gets the first free -2, -3 suffix that is not another label's own
//    slug). Regions holding no kept label are unlabelled-region.
// Confidence starts at 1 and is multiplied by: label-outside-vocabulary 0.7, area-nearby 0.95, area-ambiguous 0.7,
// no-region 0.9, shared-region 0.9, outer-region 0.9, area-from-region 0.8, area-missing 0.6, area-mismatch 0.6,
// duplicate-label 0.8. Ids are slugs of the label (lowercase, accents removed, other characters to '-'). Every text,
// region vertex, containment edge test and pairing candidate counts against MaxWork. Limit entries (explode-limit,
// pair-limit, region-limit, text-limit, work-limit) and uncalibrated are always reported, outside the
// MaxUncertainties cap. Malformed text never throws.
public static class BriefExtract
{
    private const double OutsideVocabulary = 0.7, Nearby = 0.95, Ambiguous = 0.7, NoRegion = 0.9, Shared = 0.9, Outer = 0.9, FromRegion = 0.8, Missing = 0.6, Mismatch = 0.6, Duplicate = 0.8;
    private const int MaxReadLength = 4096;
    private const int ArcSteps = 16;
    private const int MaxSlugLength = 56;

    private static readonly Regex AreaPattern = new(
        @"(?<sign>(?<=(?:\A|[:=])\s*)[-\u2212])?(?<!\d)(?<!\d[.,])(?<num>\d{1,3}(?:\.\d{3})+,\d{1,4}|\d{1,3}(?:,\d{3})+\.\d{1,4}|\d{1,7}(?:[.,]\d{1,4})?)\s*(?:m²|m2|m\^2|mts?\.?\s?2|mts?²|sq\.?\s?m|sqm)(?![\p{L}\d])\.?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
    private static readonly Regex PrefixPattern = new(
        @"(?<!\p{L})(?:sup(?:erficie)?\.?|surface|[aá]rea|s(?=\s*[:=]))\s*[:=]?\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
    private static readonly Regex AmbiguousPattern = new(@"\A[1-9]\d{0,2}[.,]\d{3}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
    private static readonly HashSet<string> UnitWords = new(StringComparer.Ordinal)
    {
        "m", "mm", "cm", "dm", "km", "m2", "mt", "mts", "mt2", "mts2", "sq", "sqm", "ft", "sqft", "in", "yd", "x", "ha", "metro", "metros", "cuadrado", "cuadrados", "square", "feet", "foot",
    };
    private static readonly HashSet<string> AlwaysReported = new(StringComparer.Ordinal) { "explode-limit", "pair-limit", "region-limit", "text-limit", "uncalibrated", "work-limit" };
    private static readonly char[] LabelTrim = { ' ', '-', '–', ':', ';', ',', '.', '=', '(', ')', '[', ']', '/', '*', '+', '_' };

    private static readonly string[] Vocabulary =
    {
        // es
        "dormitorio", "habitacion", "cuarto", "suite", "cocina", "kitchenette", "bano", "toilette", "aseo", "living", "estar", "comedor", "sala",
        "lavadero", "lavanderia", "garaje", "cochera", "hall", "recibidor", "vestibulo", "pasillo", "circulacion", "estudio", "oficina",
        "escritorio", "deposito", "baulera", "vestidor", "placard", "balcon", "terraza", "patio", "galeria", "quincho", "dependencia", "palier",
        "despensa", "biblioteca", "escalera",
        // common plan abbreviations (es)
        "dorm", "hab", "coc", "lav", "dep", "est", "circ", "liv", "com",
        // en
        "bedroom", "bed", "kitchen", "bathroom", "bath", "toilet", "wc", "lavatory", "lounge", "dining", "family", "laundry", "utility", "garage",
        "hallway", "corridor", "entrance", "entry", "foyer", "study", "office", "den", "storage", "store", "closet", "wardrobe", "pantry", "balcony",
        "terrace", "porch", "room", "library", "stair", "stairs", "playroom",
    };

    public static BriefDraft FromDxf(DxfDrawing drawing, BriefExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        options ??= new BriefExtractOptions();
        Validate(options);
        var report = new Report(options.MaxUncertainties);
        IReadOnlyList<DxfEntity> entities;
        try
        {
            var exploded = drawing.Explode(options.ExplodeOptions);
            entities = exploded.Entities;
            foreach (var warning in exploded.Warnings) report.Add("explode-warning", warning.Code + " at line " + I(warning.Line) + ": " + warning.Message, null, null);
        }
        catch (DxfFormatException error)
        {
            report.Add("explode-limit", "block explosion stopped (" + error.Code + ": " + error.Message + "); only top-level entities were read", null, null);
            entities = drawing.Entities;
        }
        return Extract(entities, drawing.SourceFile, "dxf", options.MetresPerUnit ?? drawing.MetresPerUnit, null, options, report);
    }

    // Calibrated pages are read in millimetres (MetresPerUnit 0.001); without a calibration region areas are unknown.
    public static BriefDraft FromPdf(PdfDocument document, int pageNumber, PdfCalibration? calibration = null, BriefExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new BriefExtractOptions();
        Validate(options);
        var page = document.Pages.FirstOrDefault(p => p.Number == pageNumber) ?? throw new ArgumentOutOfRangeException(nameof(pageNumber), "the document has no page " + I(pageNumber));
        var entities = page.ToDxfEntities(calibration);
        return Extract(entities, document.SourceFile, "pdf", calibration is null ? options.MetresPerUnit : 0.001, pageNumber, options, new Report(options.MaxUncertainties));
    }

    // Splits one text into a label and an area (see the class comment). Null, blank or over-long (more than 4096 UTF-16
    // code units) input reads as nothing; lone surrogates become U+FFFD and control characters spaces.
    public static BriefTextReading ReadText(string? text, int maxLabelLength = 64, int maxLabelWords = 6)
    {
        if (text is null || text.Length > MaxReadLength || maxLabelLength < 1 || maxLabelWords < 1) return new BriefTextReading(null, null, null);
        var ambiguous = false;
        var clean = Sanitize(text);
        var rest = clean;
        double? area = null;
        string? token = null;
        Match? match = null;
        try { match = AreaPattern.Match(clean); }
        catch (RegexMatchTimeoutException) { match = null; }
        if (match is { Success: true } && TryNumber(match.Groups["num"].Value, out var value))
        {
            var number = match.Groups["num"].Value;
            area = match.Groups["sign"].Success ? -value : value;
            token = match.Value.Trim();
            try { ambiguous = AmbiguousPattern.IsMatch(number); }
            catch (RegexMatchTimeoutException) { ambiguous = true; }
            var before = clean[..match.Index];
            try { before = PrefixPattern.Replace(before, ""); }
            catch (RegexMatchTimeoutException) { }
            rest = before + " " + clean[(match.Index + match.Length)..];
        }
        return new BriefTextReading(Label(rest, maxLabelLength, maxLabelWords), area, token, ambiguous);
    }

    private static BriefDraft Extract(IReadOnlyList<DxfEntity> entities, string sourceFile, string format, double? metresPerUnit, int? page, BriefExtractOptions options, Report report)
    {
        var budget = new Budget(options.MaxWork, report);
        var file = BriefParser.IsText(sourceFile) ? sourceFile : "drawing";
        var scale = metresPerUnit is { } mpu && mpu > 0 && double.IsFinite(mpu) ? mpu : (double?)null;
        if (scale is null) report.Add("uncalibrated", "the drawing has no length scale (no DXF units, no PDF calibration, no MetresPerUnit); region areas are unknown", null, null);
        HashSet<string>? textLayers = options.TextLayers is null ? null : new HashSet<string>(options.TextLayers.Where(l => l is not null), StringComparer.OrdinalIgnoreCase);
        HashSet<string>? regionLayers = options.RegionLayers is null ? null : new HashSet<string>(options.RegionLayers.Where(l => l is not null), StringComparer.OrdinalIgnoreCase);
        var words = new HashSet<string>(Vocabulary, StringComparer.Ordinal);
        var phrases = new List<string>();
        foreach (var word in options.RoomWords)
            if (word is not null && Fold(word) is { Length: > 0 } folded)
            {
                if (folded.Contains(' ', StringComparison.Ordinal)) phrases.Add(folded);
                else words.Add(folded);
            }
        bool Visible(DxfProvenance p) => !(p.PaperSpace && !options.IncludePaperSpace) && !(p.Invisible && !options.IncludeInvisible);
        BriefTextSource Source(DxfProvenance p, Point2 at, string? text) =>
            new(file, format, p.Handle, p.Layer, p.EntityType, p.Line, page, p.InsertPath, at, text);

        // 1. Texts.
        var texts = new List<TextItem>();
        var seen = new Dictionary<(string Text, long Cell, long X, long Y), TextItem>();
        var textCount = 0;
        foreach (var entity in entities)
        {
            if (entity is not DxfText text || !Visible(text.Provenance) || (textLayers is not null && !textLayers.Contains(text.Provenance.Layer))) continue;
            if (!budget.Spend()) break;
            if (++textCount > options.MaxTexts)
            {
                report.Add("text-limit", "more than " + I(options.MaxTexts) + " texts; the rest were not read", null, null);
                break;
            }
            if (!Finite(text.Insertion)) continue;
            var plain = text.PlainText ?? "";
            if (plain.Length > options.MaxTextLength)
            {
                report.Add("text-too-long", "text of " + I(plain.Length) + " characters exceeds MaxTextLength", null, null, Source(text.Provenance, text.Insertion, Display(plain[..options.MaxTextLength])));
                continue;
            }
            var display = Display(plain);
            if (display.Length == 0) continue;
            var height = double.IsFinite(text.Height) ? Math.Abs(text.Height) : 0;
            var source = Source(text.Provenance, text.Insertion, display);
            // Same text and height, insertion within 1 % of the height: looked up in the 3x3 neighbouring grid cells.
            var cell = Math.Max(0.01 * height, 1e-6);
            var cellBits = BitConverter.DoubleToInt64Bits(cell);
            var qx = Math.Floor(text.Insertion.X / cell);
            var qy = Math.Floor(text.Insertion.Y / cell);
            var quantised = Math.Abs(qx) < 4e18 && Math.Abs(qy) < 4e18;
            TextItem? copy = null;
            if (quantised)
                for (var dx = -1L; dx <= 1 && copy is null; dx++)
                    for (var dy = -1L; dy <= 1 && copy is null; dy++)
                        if (seen.TryGetValue((display, cellBits, (long)qx + dx, (long)qy + dy), out var earlier)
                            && Math.Abs(earlier.Source.At.X - text.Insertion.X) <= cell && Math.Abs(earlier.Source.At.Y - text.Insertion.Y) <= cell) copy = earlier;
            if (copy is not null)
            {
                report.Add("duplicate-text", "text '" + display + "' repeats an earlier text at the same point; the copy is ignored", null, null, source, copy.Source);
                continue;
            }
            var reading = ReadText(plain, options.MaxLabelLength, options.MaxLabelWords);
            var area = reading.AreaSquareMetres;
            if (area is { } stated && !(stated > 0 && stated <= options.MaxAreaSquareMetres))
            {
                report.Add("area-invalid", "area " + F(stated) + " m2 is outside (0, " + F(options.MaxAreaSquareMetres) + "]", null, stated, source);
                area = null;
            }
            var item = new TextItem(texts.Count, source, height, reading.Label, area, area is not null && reading.AreaAmbiguous);
            if (quantised) seen.TryAdd((display, cellBits, (long)qx, (long)qy), item);
            if (item.Label is not null || item.Area is not null) texts.Add(item);
        }

        // 2. Regions.
        var regions = new List<Region>();
        var vertexCount = 0;
        foreach (var entity in entities)
        {
            if (budget.Exhausted) break;
            if (entity is not DxfPolyline { Closed: true } polyline || !Visible(polyline.Provenance) || (regionLayers is not null && !regionLayers.Contains(polyline.Provenance.Layer))) continue;
            if (polyline.Provenance.InsertPath.Count > 0 && !options.IncludeBlockRegions) continue;
            if (polyline.Vertices.Count < 2) continue;
            if (regions.Count >= options.MaxRegions || vertexCount + polyline.Vertices.Count > options.MaxRegionVertices)
            {
                report.Add("region-limit", "more than " + I(options.MaxRegions) + " regions or " + I(options.MaxRegionVertices) + " region vertices; the rest were not read", null, null);
                break;
            }
            vertexCount += polyline.Vertices.Count;
            if (BuildRegion(polyline, regions.Count, budget) is not { } built) continue;
            var (points, areaUnits) = built;
            double? squareMetres = scale is { } s ? areaUnits * s * s : null;
            if (squareMetres is { } m2 && (!double.IsFinite(m2) || m2 < options.MinRegionAreaSquareMetres)) continue;
            double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            regions.Add(new Region(regions.Count, Source(polyline.Provenance, new Point2((minX + maxX) / 2, (minY + maxY) / 2), null), points, minX, minY, maxX, maxY, areaUnits, squareMetres));
        }

        // Containment of every text: the smallest region holding its insertion point.
        foreach (var item in texts)
        {
            if (budget.Exhausted) break;
            foreach (var region in regions)
            {
                if (!Contains(region, item.Source.At, budget)) continue;
                item.Containing.Add(region);
                if (item.Smallest is null || region.AreaUnits < item.Smallest.AreaUnits) item.Smallest = region;
            }
        }

        // 3. Labels.
        foreach (var item in texts) item.InVocabulary = item.Label is not null && HasRoomWord(item.Label, words, phrases);
        // A label without a room word is kept only in a region that holds no room-word label and encloses no region
        // holding a label (a frame or building outline around labelled rooms).
        static IEnumerable<int> Around(TextItem t) => t.Containing.Where(r => !ReferenceEquals(r, t.Smallest)).Select(r => r.Index);
        var vocabularyRegions = new HashSet<int>(texts.Where(t => t.InVocabulary).SelectMany(t => t.Containing.Select(r => r.Index)));
        var framing = new HashSet<int>(texts.Where(t => t.Label is not null).SelectMany(Around));
        foreach (var item in texts)
            item.Kept = item.Label is not null && (item.InVocabulary || (item.Smallest is { } region && !vocabularyRegions.Contains(region.Index) && !framing.Contains(region.Index)));
        // Outer regions enclose the smallest region of a kept label.
        var outer = new HashSet<int>(texts.Where(t => t.Kept).SelectMany(Around));
        var labels = texts.Where(t => t.Kept).ToList();

        // 4. Areas: own token first, then greedy nearest free area text.
        var free = texts.Where(t => t.Area is not null && !(t.Kept)).ToList();
        var candidates = new List<(double Distance, TextItem Label, TextItem Area)>();
        var pairLimit = false;
        foreach (var label in labels)
        {
            if (label.Area is not null || budget.Exhausted || pairLimit) continue;
            foreach (var area in free)
            {
                if (!budget.Spend()) break;
                var distance = (label.Source.At - area.Source.At).Length;
                var reach = options.PairDistanceTextHeights * Math.Max(label.Height, area.Height);
                var near = reach > 0 && distance <= reach;
                // In a room region any distance pairs; in an outer region or outside every region the distance cut applies.
                var paired = label.Smallest is not null
                    ? ReferenceEquals(label.Smallest, area.Smallest) && (!outer.Contains(label.Smallest.Index) || near)
                    : area.Smallest is null && near;
                if (!paired || !double.IsFinite(distance)) continue;
                if (candidates.Count >= options.MaxPairCandidates)
                {
                    report.Add("pair-limit", "more than " + I(options.MaxPairCandidates) + " label/area pairing candidates; pairing used the first ones only", null, null);
                    pairLimit = true;
                    break;
                }
                candidates.Add((distance, label, area));
            }
        }
        foreach (var (_, label, area) in candidates.OrderBy(c => c.Distance).ThenBy(c => c.Label.Order).ThenBy(c => c.Area.Order))
        {
            if (label.PairedArea is not null || area.PairedTo is not null) continue;
            label.PairedArea = area;
            area.PairedTo = label;
        }

        // 5. Rooms.
        var perRegion = labels.Where(l => l.Smallest is not null).GroupBy(l => l.Smallest!.Index).ToDictionary(g => g.Key, g => g.Count());
        var labelled = new HashSet<int>(labels.SelectMany(l => l.Containing.Select(r => r.Index)));
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        // Natural slugs are reserved first, so a later "Baño 2" keeps bano-2 even after two "Baño" labels.
        var natural = new HashSet<string>(labels.Select(l => Slug(Truncate(l.Label!, 2048))), StringComparer.Ordinal);
        var firstBySlug = new Dictionary<string, Draft>(StringComparer.Ordinal);
        var reportedShared = new HashSet<int>();
        var drafts = new List<Draft>();
        foreach (var label in labels)
        {
            var name = Truncate(label.Label!, 2048);
            var slug = Slug(name);
            var id = slug;
            if (usedIds.Contains(id))
            {
                var n = 2;
                do id = slug + "-" + I(n++); while (usedIds.Contains(id) || natural.Contains(id));
            }
            usedIds.Add(id);
            var draft = new Draft(id, name, label);
            drafts.Add(draft);

            if (!label.InVocabulary) { draft.Confidence *= OutsideVocabulary; draft.Notes.Add("label-outside-vocabulary"); }
            if (label.Area is { } own) { draft.Stated = own; draft.AreaSource = label.Source; draft.Notes.Add("area-same-text"); }
            else if (label.PairedArea is { } near) { draft.Stated = near.Area; draft.AreaSource = near.Source; draft.Confidence *= Nearby; draft.Notes.Add("area-nearby"); }
            if ((label.Area is not null ? label.Ambiguous : label.PairedArea?.Ambiguous == true) && draft.Stated is { } doubtful)
            {
                draft.Confidence *= Ambiguous;
                draft.Notes.Add("area-ambiguous");
                report.Add("area-ambiguous", "area " + F(doubtful) + " m2 was read with a decimal separator but may be a thousands group", id, doubtful, draft.AreaSource!);
            }

            Region? region = null;
            if (label.Smallest is not { } smallest) { draft.Confidence *= NoRegion; draft.Notes.Add("no-region"); }
            else if (perRegion[smallest.Index] > 1)
            {
                draft.Confidence *= Shared;
                draft.Notes.Add("shared-region");
                if (reportedShared.Add(smallest.Index))
                {
                    var sources = new List<BriefTextSource> { smallest.Source };
                    sources.AddRange(labels.Where(l => ReferenceEquals(l.Smallest, smallest)).Select(l => l.Source));
                    report.Add("shared-region", "region holds " + I(perRegion[smallest.Index]) + " labels; its area is not attributed to any of them", null, smallest.SquareMetres, sources.ToArray());
                }
            }
            else if (outer.Contains(smallest.Index)) { draft.Confidence *= Outer; draft.Notes.Add("outer-region"); }
            else region = smallest;
            draft.Region = region;
            draft.RegionArea = region?.SquareMetres;

            if (draft.Stated is null)
            {
                if (draft.RegionArea is { } inferred)
                {
                    draft.Confidence *= FromRegion;
                    draft.Notes.Add("area-from-region");
                    report.Add("area-from-region", "no area text for '" + name + "'; the region area " + F(inferred) + " m2 is the inferred target", id, inferred, label.Source, region!.Source);
                }
                else
                {
                    draft.Confidence *= Missing;
                    draft.Notes.Add("area-missing");
                    report.Add("area-missing", "no area text and no attributable region area for '" + name + "'", id, null, label.Source);
                }
            }
            else if (draft.RegionArea is { } regionArea && Math.Abs(draft.Stated.Value - regionArea) > options.AreaMismatchTolerance * regionArea)
            {
                draft.Confidence *= Mismatch;
                draft.Notes.Add("area-mismatch");
                report.Add("area-mismatch", "stated " + F(draft.Stated.Value) + " m2 differs from the region area " + F(regionArea) + " m2 by more than " + F(options.AreaMismatchTolerance * 100) + " %", id, regionArea,
                    label.Source, draft.AreaSource!, region!.Source);
            }

            if (firstBySlug.TryGetValue(slug, out var first))
            {
                if (!first.Duplicated) { first.Duplicated = true; first.Confidence *= Duplicate; first.Notes.Add("duplicate-label"); }
                draft.Duplicated = true;
                draft.Confidence *= Duplicate;
                draft.Notes.Add("duplicate-label");
                report.Add("duplicate-label", "label '" + name + "' repeats '" + first.Name + "' (" + first.Id + ")", id, null, first.Label.Source, label.Source);
            }
            else firstBySlug[slug] = draft;
        }

        foreach (var area in free.Where(a => a.PairedTo is null))
            report.Add("orphan-area", "area text " + F(area.Area!.Value) + " m2 has no label", null, area.Area, area.Source);
        foreach (var region in regions.Where(r => !labelled.Contains(r.Index)))
            report.Add("unlabelled-region", "closed region holds no room label", null, region.SquareMetres, region.Source);

        var proposals = drafts.Select(d => new BriefRoomProposal(d.Id, d.Name, d.Stated, d.RegionArea, d.Confidence, d.Label.Source, d.AreaSource, d.Region?.Source, d.Notes.ToArray())).ToArray();
        var rooms = drafts.Select(d =>
        {
            var provenance = new BriefProvenance(BriefSource.Document, d.Confidence, Reference(d.Label.Source), page);
            BriefValue<double>? target = d.Stated is { } stated
                ? new BriefValue<double>(stated, new BriefProvenance(BriefSource.Document, d.Confidence, Reference(d.AreaSource!), page))
                : d.RegionArea is { } inferred ? new BriefValue<double>(inferred, new BriefProvenance(BriefSource.Inferred, d.Confidence, Reference(d.Region!.Source), page)) : null;
            return new BriefRoom(d.Id, d.Name, TargetAreaSquareMetres: target, Provenance: provenance);
        }).ToArray();
        var briefName = options.Name is { } given && BriefParser.IsText(given) ? given : Truncate("Brief draft from " + file, 2048);
        var brief = new ProjectBrief(ProjectBrief.CurrentVersion, briefName, new BriefConstraints(), rooms, Array.Empty<BriefAdjacency>());
        return new BriefDraft(file, format, scale, brief, proposals, report.Sorted(), BriefValidator.Validate(brief));
    }

    private static (List<Point2> Points, double AreaUnits)? BuildRegion(DxfPolyline polyline, int index, Budget budget)
    {
        var points = new List<Point2>();
        double shoelace = 0, segments = 0;
        foreach (var segment in polyline.Segments())
        {
            if (!Finite(segment.Start) || !Finite(segment.End)) return null;
            if (!budget.Spend()) return null;
            points.Add(segment.Start);
            shoelace += segment.Start.X * segment.End.Y - segment.End.X * segment.Start.Y;
            if (segment.Center is { } center && segment.Radius > 0 && double.IsFinite(segment.Radius))
            {
                var sweep = 4 * Math.Atan(segment.Bulge);
                var theta = Math.Abs(sweep);
                segments += Math.Sign(segment.Bulge) * segment.Radius * segment.Radius / 2 * (theta - Math.Sin(theta));
                var startAngle = Math.Atan2(segment.Start.Y - center.Y, segment.Start.X - center.X);
                for (var k = 1; k < ArcSteps; k++)
                {
                    if (!budget.Spend()) return null;
                    var angle = startAngle + sweep * k / ArcSteps;
                    points.Add(new Point2(center.X + segment.Radius * Math.Cos(angle), center.Y + segment.Radius * Math.Sin(angle)));
                }
            }
        }
        var area = Math.Abs(shoelace / 2 + segments);
        if (points.Count < 3 || !(area > 0) || !double.IsFinite(area) || points.Any(p => !Finite(p))) return null;
        return (points, area);
    }

    // Even-odd ray casting against the tessellated outline.
    private static bool Contains(Region region, Point2 point, Budget budget)
    {
        if (point.X < region.MinX || point.X > region.MaxX || point.Y < region.MinY || point.Y > region.MaxY) return false;
        var inside = false;
        var points = region.Points;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            if (!budget.Spend()) return false;
            var a = points[i];
            var b = points[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static bool HasRoomWord(string label, HashSet<string> words, List<string> phrases)
    {
        var folded = Fold(label);
        foreach (var phrase in phrases)
            if ((" " + folded + " ").Contains(" " + phrase + " ", StringComparison.Ordinal)) return true;
        foreach (var token in folded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (words.Contains(token)) return true;
            if (token.Length > 3 && token.EndsWith('s') && words.Contains(token[..^1])) return true;
            if (token.Length > 4 && token.EndsWith("es", StringComparison.Ordinal) && words.Contains(token[..^2])) return true;
        }
        return false;
    }

    // Lowercase, accents removed, every non-letter-or-digit run a single space.
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Slug(string name)
    {
        var slug = Fold(name).Replace(' ', '-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].TrimEnd('-');
        return slug.Length == 0 ? "room" : slug;
    }

    private static string? Label(string rest, int maxLength, int maxWords)
    {
        var label = Collapse(rest).Trim(LabelTrim);
        if (label.Length == 0 || label.Length > maxLength || !label.Any(char.IsLetter)) return null;
        if (label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > maxWords) return null;
        // Letters that are only units ("120 sq ft", "3 x 4 m") do not make a label.
        if (Fold(label).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => UnitWords.Contains(t) || t.All(char.IsAsciiDigit))) return null;
        return label;
    }

    // "1.234,5" and "1,234.5": the last separator is decimal and the others group thousands; a lone separator is decimal.
    private static bool TryNumber(string token, out double value)
    {
        var comma = token.LastIndexOf(',');
        var dot = token.LastIndexOf('.');
        string normalized;
        if (comma >= 0 && dot >= 0) normalized = comma > dot ? token.Replace(".", "", StringComparison.Ordinal).Replace(',', '.') : token.Replace(",", "", StringComparison.Ordinal);
        else normalized = token.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    // Lone surrogates to U+FFFD, carriage returns to newlines, other control characters to spaces.
    private static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { builder.Append(ch).Append(text[++i]); continue; }
            if (char.IsSurrogate(ch)) builder.Append('�');
            else if (ch == '\r' || ch == '\n') builder.Append('\n');
            else if (char.IsControl(ch)) builder.Append(' ');
            else builder.Append(ch);
        }
        return builder.ToString();
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Display(string text) => Collapse(Sanitize(text));

    private static string Truncate(string text, int length)
    {
        if (text.Length <= length) return text;
        var cut = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..cut];
    }

    private static string Reference(BriefTextSource source)
    {
        var builder = new StringBuilder(source.SourceFile);
        if (source.Page is { } page) builder.Append(" page ").Append(I(page));
        else builder.Append(" line ").Append(I(source.Line));
        if (source.Handle is { } handle) builder.Append(" handle ").Append(handle);
        builder.Append(' ').Append(source.EntityType);
        if (source.Text is { } text) builder.Append(" \"").Append(text).Append('"');
        return Truncate(builder.ToString(), BriefParser.MaxStringChars);
    }

    private static void Validate(BriefExtractOptions options)
    {
        static void Require(bool condition, string name, string rule) { if (!condition) throw new ArgumentOutOfRangeException(name, name + " must be " + rule); }
        Require(double.IsFinite(options.PairDistanceTextHeights) && options.PairDistanceTextHeights >= 0, nameof(options.PairDistanceTextHeights), "finite and non-negative");
        Require(double.IsFinite(options.AreaMismatchTolerance) && options.AreaMismatchTolerance >= 0, nameof(options.AreaMismatchTolerance), "finite and non-negative");
        Require(double.IsFinite(options.MinRegionAreaSquareMetres) && options.MinRegionAreaSquareMetres >= 0, nameof(options.MinRegionAreaSquareMetres), "finite and non-negative");
        Require(double.IsFinite(options.MaxAreaSquareMetres) && options.MaxAreaSquareMetres > 0, nameof(options.MaxAreaSquareMetres), "finite and positive");
        Require(options.MaxTexts >= 1, nameof(options.MaxTexts), "at least 1");
        Require(options.MaxTextLength is >= 1 and <= MaxReadLength, nameof(options.MaxTextLength), "within 1..4096");
        Require(options.MaxLabelLength is >= 1 and <= BriefParser.MaxStringChars, nameof(options.MaxLabelLength), "within 1..2048");
        Require(options.MaxLabelWords >= 1, nameof(options.MaxLabelWords), "at least 1");
        Require(options.MaxRegions >= 0, nameof(options.MaxRegions), "non-negative");
        Require(options.MaxRegionVertices >= 0, nameof(options.MaxRegionVertices), "non-negative");
        Require(options.MaxWork >= 1, nameof(options.MaxWork), "at least 1");
        Require(options.MaxUncertainties >= 1, nameof(options.MaxUncertainties), "at least 1");
        Require(options.MaxPairCandidates >= 0, nameof(options.MaxPairCandidates), "non-negative");
        Require(options.MetresPerUnit is null || (double.IsFinite(options.MetresPerUnit.Value) && options.MetresPerUnit.Value > 0), nameof(options.MetresPerUnit), "null or finite and positive");
        ArgumentNullException.ThrowIfNull(options.RoomWords);
    }

    private static bool Finite(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed class TextItem
    {
        public TextItem(int order, BriefTextSource source, double height, string? label, double? area, bool ambiguous)
        {
            Ambiguous = ambiguous;
            Order = order;
            Source = source;
            Height = height;
            Label = label;
            Area = area;
        }

        public int Order { get; }
        public BriefTextSource Source { get; }
        public double Height { get; }
        public string? Label { get; }
        public double? Area { get; }
        public bool Ambiguous { get; }
        public List<Region> Containing { get; } = new();
        public Region? Smallest { get; set; }
        public bool InVocabulary { get; set; }
        public bool Kept { get; set; }
        public TextItem? PairedArea { get; set; }
        public TextItem? PairedTo { get; set; }
    }

    private sealed record Region(int Index, BriefTextSource Source, IReadOnlyList<Point2> Points, double MinX, double MinY, double MaxX, double MaxY, double AreaUnits, double? SquareMetres);

    private sealed class Draft
    {
        public Draft(string id, string name, TextItem label)
        {
            Id = id;
            Name = name;
            Label = label;
        }

        public string Id { get; }
        public string Name { get; }
        public TextItem Label { get; }
        public double Confidence { get; set; } = 1;
        public double? Stated { get; set; }
        public BriefTextSource? AreaSource { get; set; }
        public Region? Region { get; set; }
        public double? RegionArea { get; set; }
        public bool Duplicated { get; set; }
        public List<string> Notes { get; } = new();
    }

    private sealed class Budget
    {
        private long _left;
        private readonly Report _report;

        public Budget(long limit, Report report)
        {
            _left = limit;
            _report = report;
        }

        public bool Exhausted { get; private set; }

        public bool Spend()
        {
            if (Exhausted) return false;
            if (--_left >= 0) return true;
            Exhausted = true;
            _report.Add("work-limit", "MaxWork reached; the extraction stopped early and is incomplete", null, null);
            return false;
        }
    }

    private sealed class Report
    {
        private readonly int _max;
        private readonly List<BriefExtractUncertainty> _items = new();
        private bool _capped;
        private int _counted;

        public Report(int max) => _max = max;

        public void Add(string code, string message, string? roomId, double? value, params BriefTextSource[] sources)
        {
            // Limit and scale entries are always kept and do not count against the cap.
            var always = AlwaysReported.Contains(code);
            if (!always && _counted >= _max) { _capped = true; return; }
            if (!always) _counted++;
            _items.Add(new BriefExtractUncertainty(code, message, roomId, value, sources));
        }

        public IReadOnlyList<BriefExtractUncertainty> Sorted()
        {
            var sorted = _items.OrderBy(u => u.Code, StringComparer.Ordinal).ToList();
            if (_capped) sorted.Add(new BriefExtractUncertainty("uncertainty-limit", "more than " + _max.ToString(CultureInfo.InvariantCulture) + " uncertainties; the report is truncated", null, null, Array.Empty<BriefTextSource>()));
            return sorted;
        }
    }
}
