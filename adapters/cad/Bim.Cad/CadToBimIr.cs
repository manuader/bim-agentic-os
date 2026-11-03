using System.Globalization;
using BimElement = Bim.Core.Element;
using BimProvenance = Bim.Core.Provenance;
using RoomCompiler = Bim.Core.RoomCompiler;
namespace Bim.Cad;

// P6-04: a calibrated DXF drawing or PDF page to a draft of BIM-IR wall and door/window proposals (see CadDraft).
// Nothing is persisted and the BIM-IR schema is unchanged; the result is a proposal with an uncertainty report.
//
// 1. Candidates. Straight segments (LINE, straight LWPOLYLINE segments, PDF subpaths) in millimetres, after exploding
//    INSERTs, filtered like WallPairDetector (WallLayers, paper space, invisible, hidden layers). Geometry of door and
//    window blocks/forms is set aside for openings; geometry of other blocks/forms is reported (unmatched-block) and
//    left out unless WallsFromUnmatchedBlocks.
// 2. Pairs. WallPairDetector proposes pairs (confidence descending, then drawing order). A pair is accepted unless the
//    stretch it uses on one of its two lines overlaps (by more than JoinTolerance) a stretch already used by an accepted
//    pair; a refused pair is reported against the accepted one as ambiguous-thickness (other thickness) or
//    ambiguous-pairing (same thickness, other partner). Each segment keeps only its accepted stretches, so a line is
//    scanned against its own few stretches, not against every pair.
// 3. Openings. A door/window is hosted on the accepted wall whose band (axis +- thickness/2 + JoinTolerance) holds most
//    of its points (line ends, arc ends, DXF insertion point); a tie with a non-collinear wall is ambiguous-host. The
//    host is merged with every collinear piece of the same thickness touching the opening's stretch, so wall lines
//    broken at the opening become one wall (merged-across-opening). Collinear touching pieces merge too
//    (merged-split-lines).
// 4. Joins. Each wall end within (t1+t2)/2 + JoinTolerance of a non-parallel wall is snapped to the axis intersection
//    (corner or tee); an end within MaxGap beyond that reach (or of a collinear wall) is a gap; otherwise unclosed.
//    Each end that is not joined multiplies the wall confidence by 0.85; an ambiguous wall's confidence is halved.
// 5. Report. Lines in no accepted pair are unpaired-line unless they are caps (no longer than MaxThickness and centred
//    on a wall end, such as door jambs).
// Neighbour queries go through a uniform grid with cell = MaxThickness + JoinTolerance + MaxGap, so each query looks at
// a 5x5 block of cells; every grid insertion and visited entry counts against MaxWork, which bounds dense inputs.
// Proposals are numbered in drawing order (cad:wall:n by first face line, cad:opening:n by INSERT/Do order); element ids
// are RoomCompiler.Id(ProjectId, key) and the level key is cad:level.
public static class CadToBimIr
{
    private const double EndPenalty = 0.85;

    public static CadDraft FromDxf(DxfDrawing drawing, CadToBimIrOptions options)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        Validate(options);
        var scale = options.MillimetresPerUnit ?? (drawing.MetresPerUnit is { } metres ? metres * 1000 : throw new CadDraftException("uncalibrated", "the DXF has no length units; set MillimetresPerUnit"));
        if (!(scale > 0) || !double.IsFinite(scale)) throw new CadDraftException("uncalibrated", "the DXF length scale is not a positive finite number");
        var work = new Budget(options.MaxWork);
        var input = new Input(drawing.SourceFile, "dxf", scale);
        HashSet<string>? hidden = options.SkipHiddenLayers
            ? new HashSet<string>(drawing.Layers.Where(l => l.Frozen || l.Off).Select(l => l.Name), StringComparer.OrdinalIgnoreCase)
            : null;
        bool Visible(DxfProvenance p) =>
            !(p.PaperSpace && !options.IncludePaperSpace) && !(p.Invisible && !options.IncludeInvisible) && !(hidden is not null && hidden.Contains(p.Layer));

        // Top-level INSERTs: openings, unmatched blocks or skipped (not visible).
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);
        const int Skipped = -1, Unmatched = -2;
        foreach (var entity in drawing.Entities)
        {
            if (entity is not DxfInsert insert) continue;
            work.Spend();
            var key = insert.Provenance.Handle ?? "@" + I(insert.Provenance.Line);
            if (roles.ContainsKey(key)) continue;
            if (!Visible(insert.Provenance)) { roles[key] = Skipped; continue; }
            var source = DxfSource(insert.Provenance, null);
            var kind = Matches(insert.BlockName, options.DoorBlocks) ? "door" : Matches(insert.BlockName, options.WindowBlocks) ? "window" : null;
            if (kind is null)
            {
                roles[key] = Unmatched;
                input.Report.Add(new Pending("unmatched-block", "block " + insert.BlockName + " is not a door or window block", P(insert.Insertion * scale), null, new[] { source }));
                continue;
            }
            if (input.Openings.Count >= options.MaxOpenings) throw Limit("more than " + I(options.MaxOpenings) + " openings");
            roles[key] = input.Openings.Count;
            var opening = new OpeningInput(kind, insert.BlockName, source);
            opening.Points.Add(insert.Insertion * scale);
            input.Openings.Add(opening);
        }

        DxfExplodedDrawing exploded;
        try { exploded = drawing.Explode(); }
        catch (DxfFormatException error) when (error.Code == "explode-limit") { throw Limit(error.Message); }
        var layers = options.WallLayers is null ? null : new HashSet<string>(options.WallLayers, StringComparer.OrdinalIgnoreCase);
        foreach (var entity in exploded.Entities)
        {
            work.Spend();
            var provenance = entity.Provenance;
            if (provenance.InsertPath.Count > 0 && roles.TryGetValue(provenance.InsertPath[0], out var role))
            {
                if (role == Skipped) continue;
                if (role >= 0)
                {
                    AddPrimitives(input, input.Openings[role], entity, scale, options);
                    continue;
                }
                if (role == Unmatched && !options.WallsFromUnmatchedBlocks) continue;
            }
            if (!Visible(provenance) || (layers is not null && !layers.Contains(provenance.Layer))) continue;
            input.AddCandidate(entity, scale, 0, null);
        }
        return Draft(input, options, work);
    }

    public static CadDraft FromPdf(PdfDocument document, int pageNumber, PdfCalibration calibration, CadToBimIrOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(options);
        if (calibration is null) throw new CadDraftException("uncalibrated", "a PDF page needs a calibration");
        var page = document.Pages.FirstOrDefault(p => p.Number == pageNumber) ?? throw new CadDraftException("page-missing", "the PDF has no page " + I(pageNumber));
        var work = new Budget(options.MaxWork);
        var input = new Input(document.SourceFile, "pdf", calibration.MillimetresPerUnit);
        var layers = options.WallLayers is null ? null : new HashSet<string>(options.WallLayers, StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        const int Unmatched = -2;
        foreach (var element in page.Elements)
        {
            work.Spend();
            var provenance = element.Provenance;
            var path = provenance.XObjectPath;
            int role = int.MinValue;
            if (path.Count > 0)
            {
                if (!groups.TryGetValue(path[0], out role))
                {
                    var (caller, op) = Invocation(path[0]);
                    var form = path.Count > 1 ? Invocation(path[1]).Object : provenance.ObjectNumber;
                    var name = "form:o" + I(form);
                    var source = new CadDraftSource(document.SourceFile, "pdf", "p" + I(page.Number) + ":o" + I(caller) + ":i" + I(op), "PDF-P" + I(page.Number), "PDF-DO", null, page.Number, caller, op, null, 0, Array.Empty<string>());
                    var kind = options.PdfDoorForms.Contains(form) ? "door" : options.PdfWindowForms.Contains(form) ? "window" : null;
                    if (kind is null)
                    {
                        role = Unmatched;
                        input.Report.Add(new Pending("unmatched-block", "form " + name + " is not a listed door or window form", null, null, new[] { source }));
                    }
                    else
                    {
                        if (input.Openings.Count >= options.MaxOpenings) throw Limit("more than " + I(options.MaxOpenings) + " openings");
                        role = input.Openings.Count;
                        input.Openings.Add(new OpeningInput(kind, name, source));
                    }
                    groups[path[0]] = role;
                }
            }
            if (element is not PdfPath) continue;
            var converted = PdfToDxf.Convert(page with { Elements = new[] { element } }, calibration, options.CurveSegments);
            for (var part = 0; part < converted.Count; part++)
            {
                work.Spend();
                var entity = converted[part];
                if (role >= 0) { AddPrimitives(input, input.Openings[role], entity, 1, options); continue; }
                if (role == Unmatched && !options.WallsFromUnmatchedBlocks) continue;
                if (layers is not null && !layers.Contains(entity.Provenance.Layer)) continue;
                input.AddCandidate(entity, 1, part, provenance);
            }
        }
        return Draft(input, options, work);
    }

    // ---------------- pipeline ----------------

    private static CadDraft Draft(Input input, CadToBimIrOptions options, Budget work)
    {
        var tol = options.JoinToleranceMillimetres;
        var detect = new WallPairOptions(options.MinThicknessMillimetres, options.MaxThicknessMillimetres, options.MinOverlapMillimetres, options.AngleToleranceDegrees)
        {
            IncludePaperSpace = true,
            IncludeInvisible = true,
            MaxSegments = options.MaxSegments,
            MaxPairs = options.MaxCandidatePairs,
            MaxComparisons = options.MaxComparisons,
        };
        IReadOnlyList<WallPair> pairs;
        try { pairs = WallPairDetector.Detect(input.Candidates, detect); }
        catch (DxfFormatException error) when (error.Code == "detect-limit") { throw Limit(error.Message); }

        // 2. Accept pairs.
        var raw = new List<RawWall>();
        foreach (var pair in pairs)
        {
            work.Spend();
            var a = input.Segment(pair.A);
            var b = input.Segment(pair.B);
            var ia = a.Interval(pair.CenterlineStart, pair.CenterlineEnd);
            var ib = b.Interval(pair.CenterlineStart, pair.CenterlineEnd);
            var conflict = a.Conflict(ia, tol, work) ?? b.Conflict(ib, tol, work);
            if (conflict is not null)
            {
                var sources = new[] { input.Source(pair.A), input.Source(pair.B) };
                var mid = P((pair.CenterlineStart + pair.CenterlineEnd) * 0.5);
                var differs = Math.Abs(conflict.Thickness - pair.Thickness) > tol;
                conflict.Alternatives.Add(new Pending(differs ? "ambiguous-thickness" : "ambiguous-pairing",
                    (differs ? "another pairing gives thickness " : "another partner line gives the same thickness ") + F(pair.Thickness) + " mm", mid, pair.Thickness, sources));
                continue;
            }
            if (raw.Count >= options.MaxWalls) throw Limit("more than " + I(options.MaxWalls) + " walls");
            var wall = new RawWall(pair, a, b, input.Source(pair.A), input.Source(pair.B));
            a.Accepted.Add((ia.Low, ia.High, wall));
            b.Accepted.Add((ib.Low, ib.High, wall));
            raw.Add(wall);
        }
        raw.Sort((x, y) => x.SortKey.CompareTo(y.SortKey));
        for (var i = 0; i < raw.Count; i++) raw[i].Index = i;

        var cell = options.MaxThicknessMillimetres + tol + options.MaxGapMillimetres;
        var rawGrid = new Grid(cell, raw.Count, work);
        foreach (var wall in raw) rawGrid.Add(wall.Index, wall.Start, wall.End);
        var sets = new UnionFind(raw.Count);
        var mergedByOpening = new bool[raw.Count];
        var mergedBySplit = new bool[raw.Count];

        // 3. Host openings on raw walls and merge across them.
        var near = new List<int>();
        var hosted = new List<(OpeningInput Opening, int Host, bool Ambiguous)>();
        foreach (var opening in input.Openings)
        {
            work.Spend();
            if (opening.Primitives.Count == 0)
            {
                input.Report.Add(new Pending("unhosted-opening", opening.Kind + " block " + opening.BlockName + " has no geometry", null, null, new[] { opening.Source }));
                continue;
            }
            rawGrid.QueryAll(opening.Points, near);
            int best = -1, bestScore = 0;
            var ambiguous = false;
            foreach (var index in near)
            {
                var wall = raw[index];
                var score = 0;
                foreach (var point in opening.Points)
                {
                    work.Spend();
                    if (wall.InBand(point, tol)) score++;
                }
                if (score == 0) continue;
                if (score > bestScore) { best = index; bestScore = score; ambiguous = false; }
                else if (score == bestScore && !Collinear(raw[best], wall, tol, options.AngleToleranceDegrees, false)) ambiguous = true;
            }
            if (best < 0)
            {
                var anchor = opening.Points.Count > 0 ? P(opening.Points[0]) : (CadDraftPoint?)null;
                input.Report.Add(new Pending("unhosted-opening", opening.Kind + " block " + opening.BlockName + " is not on any drafted wall", anchor, null, new[] { opening.Source }));
                continue;
            }
            var host = raw[best];
            var (s0, s1) = Extent(opening.Primitives, host.Start, host.Direction, work);
            near.Clear();
            rawGrid.QueryAll(new[] { host.At(s0), host.At(s1) }, near);
            foreach (var index in near)
            {
                work.Spend();
                var other = raw[index];
                if (index == best || !Collinear(host, other, tol, options.AngleToleranceDegrees, true)) continue;
                var (lo, hi) = host.Project(other.Start, other.End);
                if (lo <= s1 + tol && hi >= s0 - tol && sets.Union(best, index)) { mergedByOpening[best] = true; mergedByOpening[index] = true; }
            }
            hosted.Add((opening, best, ambiguous));
        }

        // Touching collinear pieces of one wall merge.
        foreach (var wall in raw)
        {
            near.Clear();
            rawGrid.QueryAll(new[] { wall.Start, wall.End }, near);
            foreach (var index in near)
            {
                work.Spend();
                if (index == wall.Index) continue;
                var other = raw[index];
                if (!Collinear(wall, other, tol, options.AngleToleranceDegrees, true)) continue;
                var (lo, hi) = wall.Project(other.Start, other.End);
                if (lo <= wall.Length + tol && hi >= -tol && sets.Union(wall.Index, index)) { mergedBySplit[wall.Index] = true; mergedBySplit[index] = true; }
            }
        }

        // Final walls: one per merged set, in drawing order of their first piece.
        var finals = new List<FinalWall>();
        var finalOf = new int[raw.Count];
        var byRoot = new Dictionary<int, FinalWall>();
        foreach (var wall in raw)
        {
            var root = sets.Find(wall.Index);
            if (!byRoot.TryGetValue(root, out var final))
            {
                final = new FinalWall(finals.Count, wall);
                byRoot[root] = final;
                finals.Add(final);
            }
            final.Members.Add(wall);
            finalOf[wall.Index] = final.Index;
            if (mergedByOpening[wall.Index]) final.MergedByOpening = true;
            if (mergedBySplit[wall.Index]) final.MergedBySplit = true;
        }
        foreach (var final in finals) final.Close(work);

        // 4. Joins.
        var finalGrid = new Grid(cell, finals.Count, work);
        foreach (var final in finals) finalGrid.Add(final.Index, final.Start, final.End);
        var ends = new EndState[finals.Count, 2];
        var snaps = new double?[finals.Count, 2];
        foreach (var final in finals)
        {
            for (var side = 0; side < 2; side++)
            {
                var end = side == 0 ? final.Start : final.End;
                near.Clear();
                finalGrid.QueryAll(new[] { end }, near);
                double joinDistance = double.PositiveInfinity, gapDistance = double.PositiveInfinity;
                double? snap = null;
                var joined = false;
                foreach (var index in near)
                {
                    work.Spend();
                    if (index == final.Index) continue;
                    var other = finals[index];
                    var distance = DistanceToSegment(end, other.Start, other.End);
                    if (Parallel(final.Direction, other.Direction, options.AngleToleranceDegrees))
                    {
                        if (Math.Abs(Cross(final.Direction, other.Start - final.Start)) > tol || Math.Abs(Cross(final.Direction, other.End - final.Start)) > tol) continue;
                        if (distance <= tol) { if (distance < joinDistance) { joinDistance = distance; joined = true; snap = null; } }
                        else if (distance <= options.MaxGapMillimetres && distance < gapDistance) gapDistance = distance;
                        continue;
                    }
                    var reach = (final.Thickness + other.Thickness) / 2 + tol;
                    if (distance <= reach)
                    {
                        var along = Cross(other.Start - end, other.Direction) / Cross(final.Direction, other.Direction);
                        if (double.IsFinite(along) && Math.Abs(along) <= reach)
                        {
                            if (distance < joinDistance) { joinDistance = distance; joined = true; snap = Dot(end + final.Direction * along - final.Start, final.Direction); }
                            continue;
                        }
                    }
                    if (distance <= reach + options.MaxGapMillimetres && distance < gapDistance) gapDistance = distance;
                }
                if (joined) { ends[final.Index, side] = new EndState("joined", 0); snaps[final.Index, side] = snap; }
                else if (double.IsFinite(gapDistance)) ends[final.Index, side] = new EndState("gap", gapDistance);
                else ends[final.Index, side] = new EndState("unclosed", 0);
            }
        }
        foreach (var final in finals)
        {
            var lo = snaps[final.Index, 0] ?? 0;
            var hi = snaps[final.Index, 1] ?? final.Length;
            if (hi - lo > tol) final.Reshape(lo, hi);
        }

        // Keys and elements.
        var level = new BimElement(RoomCompiler.Id(options.ProjectId, "cad:level"), "level", new[] { M(options.LevelElevationMillimetres) }, Array.Empty<Guid>(), new BimProvenance("cad:" + input.SourceFile, options.Approval));
        var provenance = level.Provenance;
        var report = new List<(string Code, CadUncertainty Entry)>();
        void Report(Pending p, string? key) => report.Add((p.Code, new CadUncertainty(p.Code, p.Message, key, p.At, p.Value, p.Sources)));
        var walls = new List<CadWallProposal>(finals.Count);
        foreach (var final in finals)
        {
            var key = "cad:wall:" + I(final.Index + 1);
            var notes = new List<string>();
            if (final.MergedByOpening) notes.Add("merged-across-opening");
            if (final.MergedBySplit) notes.Add("merged-split-lines");
            var alternatives = final.Members.SelectMany(m => m.Alternatives).ToList();
            if (alternatives.Any(a => a.Code == "ambiguous-thickness")) notes.Add("ambiguous-thickness");
            if (alternatives.Any(a => a.Code == "ambiguous-pairing")) notes.Add("ambiguous-pairing");
            notes.Add("height-assumed");
            var confidence = final.Members.Min(m => m.Pair.Confidence) * (alternatives.Count > 0 ? 0.5 : 1);
            for (var side = 0; side < 2; side++)
            {
                var state = ends[final.Index, side];
                notes.Add((side == 0 ? "start:" : "end:") + state.Kind);
                if (state.Kind == "joined") continue;
                confidence *= EndPenalty;
                var at = P(side == 0 ? final.Start : final.End);
                var sources = side == 0 ? final.Members[0].Sources : final.Members[^1].Sources;
                if (state.Kind == "gap") Report(new Pending("gap", "wall " + (side == 0 ? "start" : "end") + " is " + F(state.Distance) + " mm from another wall without a join", at, state.Distance, sources), key);
                else Report(new Pending("unclosed-join", "wall " + (side == 0 ? "start" : "end") + " does not meet another wall", at, null, sources), key);
            }
            foreach (var alternative in alternatives) Report(alternative, key);
            var start = P(final.Start);
            var endPoint = P(final.End);
            var element = new BimElement(RoomCompiler.Id(options.ProjectId, key), "wall",
                new[] { M(start.X), M(start.Y), M(endPoint.X), M(endPoint.Y), M(options.WallHeightMillimetres), M(final.Thickness) }, new[] { level.Id }, provenance);
            walls.Add(new CadWallProposal(key, element, start, endPoint, final.Thickness, options.WallHeightMillimetres, confidence,
                final.Members.SelectMany(m => m.Sources).ToArray(), notes.ToArray()));
        }

        var openings = new List<CadOpeningProposal>(hosted.Count);
        foreach (var (opening, host, ambiguous) in hosted)
        {
            work.Spend();
            var final = finals[finalOf[host]];
            var wall = walls[final.Index];
            var (s0, s1) = Extent(opening.Primitives, final.Start, final.Direction, work);
            var beyond = s0 < -tol || s1 > final.Length + tol;
            var c0 = Math.Clamp(s0, 0, final.Length);
            var c1 = Math.Clamp(s1, 0, final.Length);
            if (!(c1 - c0 > tol))
            {
                input.Report.Add(new Pending("unhosted-opening", opening.Kind + " block " + opening.BlockName + " has no width on its host wall", P(final.At(s0)), null, new[] { opening.Source }));
                continue;
            }
            var key = "cad:opening:" + I(openings.Count + 1);
            var notes = new List<string>();
            var confidence = 1.0;
            if (ambiguous)
            {
                notes.Add("ambiguous-host");
                confidence *= 0.5;
                Report(new Pending("ambiguous-host", opening.Kind + " block " + opening.BlockName + " fits more than one wall equally", P(final.At((c0 + c1) / 2)), null, new[] { opening.Source }), key);
            }
            if (beyond)
            {
                notes.Add("beyond-host");
                confidence *= 0.6;
                Report(new Pending("opening-beyond-host", opening.Kind + " block " + opening.BlockName + " extends past its host wall and was clamped", P(final.At((c0 + c1) / 2)), null, new[] { opening.Source }), key);
            }
            notes.Add("sill-assumed");
            notes.Add("height-assumed");
            var door = opening.Kind == "door";
            var sill = door ? options.DoorSillMillimetres : options.WindowSillMillimetres;
            var height = door ? options.DoorHeightMillimetres : options.WindowHeightMillimetres;
            var a = P(final.At(c0));
            var b = P(final.At(c1));
            var element = new BimElement(RoomCompiler.Id(options.ProjectId, key), opening.Kind, new[] { M(a.X), M(a.Y), M(b.X), M(b.Y), M(sill), M(height) },
                new[] { level.Id, wall.Element.Id }, provenance);
            openings.Add(new CadOpeningProposal(key, opening.Kind, element, wall.Key, a, b, c1 - c0, sill, height, confidence, opening.BlockName, opening.Source, notes.ToArray()));
        }

        // 5. Unpaired lines (caps excluded).
        foreach (var segment in input.Segments)
        {
            work.Spend();
            if (segment.Accepted.Count > 0) continue;
            var mid = (segment.Start + segment.End) * 0.5;
            if (segment.Length <= options.MaxThicknessMillimetres + tol && raw.Count > 0)
            {
                near.Clear();
                rawGrid.QueryAll(new[] { mid }, near);
                var cap = false;
                foreach (var index in near)
                {
                    work.Spend();
                    if ((raw[index].Start - mid).Length <= tol || (raw[index].End - mid).Length <= tol) { cap = true; break; }
                }
                if (cap) continue;
            }
            Report(new Pending("unpaired-line", "line is not one side of any drafted wall", P(mid), null, new[] { segment.Source }), null);
        }
        foreach (var pending in input.Report) Report(pending, null);

        var ordered = report.Select((r, i) => (r.Code, r.Entry, i)).OrderBy(r => r.Code, StringComparer.Ordinal).ThenBy(r => r.i).Select(r => r.Entry).ToList();
        if (ordered.Count > options.MaxUncertainties)
        {
            var total = ordered.Count;
            ordered = ordered.Take(options.MaxUncertainties).ToList();
            ordered.Add(new CadUncertainty("uncertainty-limit", "report capped at " + I(options.MaxUncertainties) + " of " + I(total) + " entries", null, null, null, Array.Empty<CadDraftSource>()));
        }
        return new CadDraft(input.SourceFile, input.Format, input.Scale, options.ProjectId, level, walls, openings, ordered);
    }

    // ---------------- inputs ----------------

    private static void AddPrimitives(Input input, OpeningInput opening, DxfEntity entity, double scale, CadToBimIrOptions options)
    {
        void Add(Primitive primitive)
        {
            if (++input.PrimitiveCount > options.MaxOpeningPrimitives) throw Limit("more than " + I(options.MaxOpeningPrimitives) + " opening primitives");
            opening.Primitives.Add(primitive);
            opening.Points.Add(primitive.A);
            opening.Points.Add(primitive.B);
        }
        switch (entity)
        {
            case DxfLine line:
                if (Finite(line.Start) && Finite(line.End)) Add(Primitive.Line(line.Start * scale, line.End * scale));
                break;
            case DxfPolyline polyline:
                foreach (var segment in polyline.Segments())
                {
                    if (!Finite(segment.Start) || !Finite(segment.End)) continue;
                    if (segment.Center is { } center) Add(Primitive.Arc(center * scale, segment.Radius * scale, segment.StartAngleDegrees, DxfGeometry.Span(segment.StartAngleDegrees, segment.EndAngleDegrees)));
                    else Add(Primitive.Line(segment.Start * scale, segment.End * scale));
                }
                break;
            case DxfArc arc:
                if (Finite(arc.Center) && double.IsFinite(arc.Radius) && arc.Radius > 0) Add(Primitive.Arc(arc.Center * scale, arc.Radius * scale, arc.StartAngleDegrees, DxfGeometry.Span(arc.StartAngleDegrees, arc.EndAngleDegrees)));
                break;
            case DxfCircle circle:
                if (Finite(circle.Center) && double.IsFinite(circle.Radius) && circle.Radius > 0) Add(Primitive.Arc(circle.Center * scale, circle.Radius * scale, 0, 360));
                break;
        }
    }

    private static (int Object, int Operator) Invocation(string step)
    {
        // "o{object}:i{operator index}" as written by the PDF reader.
        var colon = step.IndexOf(':', StringComparison.Ordinal);
        if (colon > 1 && step[0] == 'o' && colon + 2 <= step.Length && step[colon + 1] == 'i'
            && int.TryParse(step.AsSpan(1, colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var obj)
            && int.TryParse(step.AsSpan(colon + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var op))
            return (obj, op);
        return (-1, -1);
    }

    private static bool Matches(string name, IReadOnlyCollection<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            if (pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static CadDraftSource DxfSource(DxfProvenance p, int? segment) =>
        new(p.SourceFile, "dxf", p.Handle, p.Layer, p.EntityType, p.Line, null, null, null, segment, 0, p.InsertPath);

    private static void Validate(CadToBimIrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        static void Require(bool ok, string message) { if (!ok) throw new ArgumentException(message, nameof(options)); }
        static bool NonNegative(double v) => double.IsFinite(v) && v >= 0;
        static bool Positive(double v) => double.IsFinite(v) && v > 0;
        Require(options.ProjectId != Guid.Empty, "ProjectId must not be empty");
        Require(NonNegative(options.MinThicknessMillimetres), "MinThicknessMillimetres must be a finite non-negative length");
        Require(Positive(options.MaxThicknessMillimetres) && options.MaxThicknessMillimetres >= options.MinThicknessMillimetres, "MaxThicknessMillimetres must be finite, positive and at least MinThicknessMillimetres");
        Require(NonNegative(options.MinOverlapMillimetres), "MinOverlapMillimetres must be a finite non-negative length");
        Require(options.AngleToleranceDegrees > 0 && options.AngleToleranceDegrees < 90, "AngleToleranceDegrees must be in (0, 90)");
        Require(NonNegative(options.JoinToleranceMillimetres), "JoinToleranceMillimetres must be a finite non-negative length");
        Require(NonNegative(options.MaxGapMillimetres), "MaxGapMillimetres must be a finite non-negative length");
        Require(options.MillimetresPerUnit is null || Positive(options.MillimetresPerUnit.Value), "MillimetresPerUnit must be positive and finite");
        Require(double.IsFinite(options.LevelElevationMillimetres), "LevelElevationMillimetres must be finite");
        Require(Positive(options.WallHeightMillimetres) && Positive(options.DoorHeightMillimetres) && Positive(options.WindowHeightMillimetres), "heights must be positive and finite");
        Require(NonNegative(options.DoorSillMillimetres) && NonNegative(options.WindowSillMillimetres), "sills must be finite and non-negative");
        Require(!string.IsNullOrWhiteSpace(options.Approval), "Approval must not be blank");
        Require(options.DoorBlocks is not null && options.WindowBlocks is not null && options.PdfDoorForms is not null && options.PdfWindowForms is not null, "block and form lists must not be null");
        Require(options.CurveSegments >= 1 && options.CurveSegments <= 1024, "CurveSegments must be in [1, 1024]");
        Require(options.MaxSegments > 0 && options.MaxCandidatePairs > 0 && options.MaxComparisons > 0 && options.MaxWalls > 0 && options.MaxOpenings > 0
            && options.MaxOpeningPrimitives > 0 && options.MaxWork > 0 && options.MaxUncertainties > 0, "Max* bounds must be positive");
    }

    // ---------------- geometry ----------------

    private static (double Low, double High) Extent(List<Primitive> primitives, Point2 origin, Point2 direction, Budget work)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var primitive in primitives)
        {
            work.Spend();
            var (a, b) = primitive.Extent(origin, direction);
            lo = Math.Min(lo, a);
            hi = Math.Max(hi, b);
        }
        return (lo, hi);
    }

    private static bool Parallel(Point2 u, Point2 v, double toleranceDegrees) => Math.Abs(Cross(u, v)) <= Math.Sin(toleranceDegrees * Math.PI / 180) + 1e-12;

    // Same line (within tol), optionally same thickness.
    private static bool Collinear(RawWall a, RawWall b, double tol, double toleranceDegrees, bool sameThickness)
    {
        if (!Parallel(a.Direction, b.Direction, toleranceDegrees)) return false;
        if (Math.Abs(Cross(a.Direction, b.Start - a.Start)) > tol || Math.Abs(Cross(a.Direction, b.End - a.Start)) > tol) return false;
        return !sameThickness || Math.Abs(a.Thickness - b.Thickness) <= tol;
    }

    private static double DistanceToSegment(Point2 p, Point2 a, Point2 b)
    {
        var ab = b - a;
        var length2 = Dot(ab, ab);
        var t = length2 > 0 ? Math.Clamp(Dot(p - a, ab) / length2, 0, 1) : 0;
        return (a + ab * t - p).Length;
    }

    internal static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;

    internal static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;

    private static bool Finite(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    // -0 and 0 are one quantity, so hashes of equal drafts are equal.
    private static CadDraftPoint P(Point2 p) => new(p.X == 0 ? 0.0 : p.X, p.Y == 0 ? 0.0 : p.Y);

    private static double M(double millimetres)
    {
        var metres = Bim.Core.Length.FromMillimetres(millimetres).Metres;
        return metres == 0 ? 0.0 : metres;
    }

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static CadDraftException Limit(string what) => new("draft-limit", "CAD draft stopped: " + what);

    // ---------------- working types ----------------

    private sealed record Pending(string Code, string Message, CadDraftPoint? At, double? Value, IReadOnlyList<CadDraftSource> Sources);

    private readonly record struct EndState(string Kind, double Distance);

    private sealed class Budget(long max)
    {
        private long spent;

        public void Spend()
        {
            if (++spent > max) throw Limit("more than " + I(max) + " work units");
        }
    }

    private sealed class Input(string sourceFile, string format, double scale)
    {
        private readonly Dictionary<DxfProvenance, (int Order, CadDraftSource Source)> sources = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(int, int), SegmentState> segments = new();

        public string SourceFile { get; } = sourceFile;
        public string Format { get; } = format;
        public double Scale { get; } = scale;
        public List<DxfEntity> Candidates { get; } = new();
        public List<SegmentState> Segments { get; } = new();
        public List<OpeningInput> Openings { get; } = new();
        public List<Pending> Report { get; } = new();
        public int PrimitiveCount { get; set; }

        // Scales to millimetres, gives the entity its own provenance object (so detector sides map back to it) and
        // registers its straight segments the way WallPairDetector enumerates them.
        public void AddCandidate(DxfEntity entity, double factor, int part, PdfProvenance? pdf)
        {
            DxfEntity? scaled = entity switch
            {
                DxfLine line => new DxfLine(entity.Provenance with { }, line.Start * factor, line.End * factor),
                DxfPolyline polyline => new DxfPolyline(entity.Provenance with { }, polyline.Vertices.Select(v => new DxfVertex(v.Point * factor, v.Bulge)).ToArray(), polyline.Closed),
                _ => null,
            };
            if (scaled is null) return;
            var order = Candidates.Count;
            var p = scaled.Provenance;
            var source = pdf is null
                ? DxfSource(p, null)
                : new CadDraftSource(p.SourceFile, "pdf", p.Handle, p.Layer, p.EntityType, null, pdf.Page, pdf.ObjectNumber, pdf.OperatorIndex, null, part, pdf.XObjectPath);
            sources[p] = (order, source);
            Candidates.Add(scaled);
            if (scaled is DxfLine l) Register(order, source, l.Start, l.End);
            else if (scaled is DxfPolyline poly)
                foreach (var segment in poly.Segments())
                    if (!segment.IsArc) Register(order, segment.Index, source with { SegmentIndex = segment.Index }, segment.Start, segment.End);
        }

        private void Register(int order, int index, CadDraftSource source, Point2 start, Point2 end)
        {
            var length = (end - start).Length;
            if (!(length > 0) || !double.IsFinite(length) || !Finite(start)) return;
            var state = new SegmentState(order, index, source, start, end, length);
            segments[(order, index)] = state;
            Segments.Add(state);
        }

        public SegmentState Segment(WallSide side) => segments[(sources[side.Source].Order, side.SegmentIndex ?? -1)];

        public CadDraftSource Source(WallSide side) => Segment(side).Source;
    }

    private sealed class SegmentState(int order, int index, CadDraftSource source, Point2 start, Point2 end, double length)
    {
        public int Order { get; } = order;
        public int Index { get; } = index;
        public CadDraftSource Source { get; } = source;
        public Point2 Start { get; } = start;
        public Point2 End { get; } = end;
        public double Length { get; } = length;
        public Point2 Direction { get; } = (end - start) * (1 / length);
        public List<(double Low, double High, RawWall Wall)> Accepted { get; } = new();

        public (double Low, double High) Interval(Point2 a, Point2 b)
        {
            var ta = Dot(a - Start, Direction);
            var tb = Dot(b - Start, Direction);
            return (Math.Min(ta, tb), Math.Max(ta, tb));
        }

        public RawWall? Conflict((double Low, double High) interval, double tol, Budget work)
        {
            foreach (var (low, high, wall) in Accepted)
            {
                work.Spend();
                if (Math.Min(high, interval.High) - Math.Max(low, interval.Low) > tol) return wall;
            }
            return null;
        }
    }

    private sealed class OpeningInput(string kind, string blockName, CadDraftSource source)
    {
        public string Kind { get; } = kind;
        public string BlockName { get; } = blockName;
        public CadDraftSource Source { get; } = source;
        public List<Primitive> Primitives { get; } = new();
        public List<Point2> Points { get; } = new();
    }

    // A line (A to B) or a counter-clockwise arc (A and B its end points).
    private sealed record Primitive(Point2 A, Point2 B, Point2 Center, double Radius, double StartDegrees, double SpanDegrees, bool IsArc)
    {
        public static Primitive Line(Point2 a, Point2 b) => new(a, b, default, 0, 0, 0, false);

        public static Primitive Arc(Point2 center, double radius, double startDegrees, double spanDegrees) =>
            new(DxfGeometry.PointAt(center, radius, startDegrees), DxfGeometry.PointAt(center, radius, startDegrees + spanDegrees), center, radius, startDegrees, spanDegrees, true);

        // Range of the projection onto direction (unit) measured from origin; an arc adds its extreme points along the
        // direction when they lie within its span.
        public (double Low, double High) Extent(Point2 origin, Point2 direction)
        {
            var a = Dot(A - origin, direction);
            var b = Dot(B - origin, direction);
            double lo = Math.Min(a, b), hi = Math.Max(a, b);
            if (!IsArc) return (lo, hi);
            var c = Dot(Center - origin, direction);
            var phi = DxfGeometry.Degrees(direction);
            if (Within(phi)) hi = Math.Max(hi, c + Radius);
            if (Within(phi + 180)) lo = Math.Min(lo, c - Radius);
            return (lo, hi);
        }

        private bool Within(double degrees)
        {
            if (SpanDegrees >= 360) return true;
            var offset = (degrees - StartDegrees) % 360;
            if (offset < 0) offset += 360;
            return offset <= SpanDegrees;
        }
    }

    private sealed class RawWall
    {
        public RawWall(WallPair pair, SegmentState a, SegmentState b, CadDraftSource sourceA, CadDraftSource sourceB)
        {
            Pair = pair;
            Start = pair.CenterlineStart;
            End = pair.CenterlineEnd;
            Length = (End - Start).Length;
            Direction = Length > 0 ? (End - Start) * (1 / Length) : a.Direction;
            Thickness = pair.Thickness;
            Sources = new[] { sourceA, sourceB };
            SortKey = (a.Order, a.Index, b.Order, b.Index);
        }

        public WallPair Pair { get; }
        public Point2 Start { get; }
        public Point2 End { get; }
        public double Length { get; }
        public Point2 Direction { get; }
        public double Thickness { get; }
        public IReadOnlyList<CadDraftSource> Sources { get; }
        public (int, int, int, int) SortKey { get; }
        public int Index { get; set; }
        public List<Pending> Alternatives { get; } = new();

        public Point2 At(double s) => Start + Direction * s;

        public (double Low, double High) Project(Point2 a, Point2 b)
        {
            var ta = Dot(a - Start, Direction);
            var tb = Dot(b - Start, Direction);
            return (Math.Min(ta, tb), Math.Max(ta, tb));
        }

        public bool InBand(Point2 p, double tol)
        {
            var along = Dot(p - Start, Direction);
            return along >= -tol && along <= Length + tol && Math.Abs(Cross(Direction, p - Start)) <= Thickness / 2 + tol;
        }
    }

    private sealed class FinalWall(int index, RawWall first)
    {
        private Point2 origin = first.Start;

        public int Index { get; } = index;
        public List<RawWall> Members { get; } = new();
        public Point2 Direction { get; } = first.Direction;
        public double Thickness { get; } = first.Thickness;
        public bool MergedByOpening { get; set; }
        public bool MergedBySplit { get; set; }
        public Point2 Start { get; private set; } = first.Start;
        public Point2 End { get; private set; } = first.End;
        public double Length { get; private set; } = first.Length;

        // Axis on the first piece's line, spanning every member's projection.
        public void Close(Budget work)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var member in Members)
            {
                work.Spend();
                foreach (var p in new[] { member.Start, member.End })
                {
                    var s = Dot(p - origin, Direction);
                    lo = Math.Min(lo, s);
                    hi = Math.Max(hi, s);
                }
            }
            Reshape(lo, hi);
        }

        public void Reshape(double lo, double hi)
        {
            var start = origin + Direction * lo;
            var end = origin + Direction * hi;
            origin = start;
            Start = start;
            End = end;
            Length = hi - lo;
        }

        public Point2 At(double s) => Start + Direction * s;
    }

    private sealed class UnionFind
    {
        private readonly int[] parent;

        public UnionFind(int count)
        {
            parent = new int[count];
            for (var i = 0; i < count; i++) parent[i] = i;
        }

        public int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        // Joins the sets; the smaller index stays the root. False when already joined.
        public bool Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return false;
            if (ra < rb) parent[rb] = ra;
            else parent[ra] = rb;
            return true;
        }
    }

    // Uniform grid of segment ids. A segment is registered in the cells of points sampled every half cell along it, so a
    // point within one cell size of the segment is always within two cells of a sample.
    private sealed class Grid
    {
        private const double MaxCellIndex = 1e15;
        private readonly double cell;
        private readonly Dictionary<(long, long), List<int>> cells = new();
        private readonly int[] stamp;
        private readonly Budget work;
        private int mark;

        public Grid(double cell, int count, Budget work)
        {
            this.cell = cell;
            stamp = new int[count];
            this.work = work;
        }

        private (long, long) Key(Point2 p) => ((long)Math.Clamp(Math.Floor(p.X / cell), -MaxCellIndex, MaxCellIndex), (long)Math.Clamp(Math.Floor(p.Y / cell), -MaxCellIndex, MaxCellIndex));

        public void Add(int id, Point2 a, Point2 b)
        {
            var length = (b - a).Length;
            var steps = (long)Math.Ceiling(length / (cell / 2));
            if (!(steps >= 1)) steps = 1;
            for (long k = 0; k <= steps; k++)
            {
                work.Spend();
                var key = Key(a + (b - a) * ((double)k / steps));
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                if (list.Count == 0 || list[^1] != id) list.Add(id);
            }
        }

        // Ids registered near any of the points, ascending, each once.
        public void QueryAll(IEnumerable<Point2> points, List<int> output)
        {
            output.Clear();
            if (++mark == int.MaxValue) { Array.Clear(stamp); mark = 1; }
            foreach (var p in points)
            {
                var (cx, cy) = Key(p);
                for (var dx = -2; dx <= 2; dx++)
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        work.Spend();
                        if (!cells.TryGetValue((cx + dx, cy + dy), out var list)) continue;
                        foreach (var id in list)
                        {
                            work.Spend();
                            if (stamp[id] == mark) continue;
                            stamp[id] = mark;
                            output.Add(id);
                        }
                    }
            }
            output.Sort();
        }
    }
}
