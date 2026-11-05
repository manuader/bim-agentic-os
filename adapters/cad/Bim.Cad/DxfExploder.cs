using System.Globalization;
namespace Bim.Cad;

// Replaces every INSERT, recursively, by its block's entities in world coordinates:
// world = parent ∘ translate(insertion) ∘ rotate(rotation) ∘ scale(sx, sy) ∘ translate(-block base point).
// Under a similarity (uniform scale, rotation, mirror) arcs and circles stay arcs and circles (a mirror keeps the
// counter-clockwise sense by swapping the end angles, and negates polyline bulges); under a non-uniform scale they
// and bulged polyline segments are tessellated into straight polylines with an arc-tessellated warning. Missing
// blocks and block cycles are skipped with warnings; nesting deeper than MaxDepth or more than MaxEntities output
// entities raise explode-limit. Every visited entity or INSERT and every warning also counts against MaxWork, so
// exponential nesting raises explode-limit quickly even when its leaves are empty, unsupported or missing blocks.
// Paper-space and invisible flags are inherited from enclosing INSERTs. A MINSERT array places the block once per cell
// (row-major), each cell's insert-path element written "key[row,column]"; every cell counts against MaxWork, its
// entities against MaxEntities.
public static class DxfExploder
{
    public static DxfExplodedDrawing Explode(DxfDrawing drawing, DxfExplodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxDepth < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be at least 1");
        if (options.MaxEntities < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxEntities must be at least 1");
        if (options.MaxWork < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxWork must be at least 1");
        if (options.ArcTessellationSegments < 4 || options.ArcTessellationSegments > 4096) throw new ArgumentOutOfRangeException(nameof(options), "ArcTessellationSegments must be in 4..4096");
        var context = new Context(drawing, options);
        foreach (var entity in drawing.Entities) context.Emit(entity, Affine.Identity, Array.Empty<string>(), null, false, false, 0);
        return new DxfExplodedDrawing(context.Output.ToArray(), context.Warnings.ToArray());
    }

    private sealed class Context
    {
        private readonly Dictionary<string, DxfBlock> blocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> stack = new();
        private readonly DxfExplodeOptions options;
        private long work;

        public Context(DxfDrawing drawing, DxfExplodeOptions options)
        {
            this.options = options;
            foreach (var block in drawing.Blocks) blocks.TryAdd(block.Name, block);
        }

        public List<DxfEntity> Output { get; } = new();
        public List<DxfWarning> Warnings { get; } = new();

        private void Spend(int line)
        {
            if (++work > options.MaxWork)
                throw new DxfFormatException("explode-limit", line, "exploding inserts needs more than " + options.MaxWork.ToString(CultureInfo.InvariantCulture) + " work units (entities, inserts and warnings)");
        }

        private void Warn(string code, int line, string message)
        {
            Spend(line);
            Warnings.Add(new DxfWarning(code, line, message));
        }

        public void Emit(DxfEntity entity, Affine transform, IReadOnlyList<string> path, string? inheritedLayer, bool inheritedPaper, bool inheritedInvisible, int depth)
        {
            var source = entity.Provenance;
            Spend(source.Line);
            var layer = inheritedLayer is not null && source.Layer == "0" ? inheritedLayer : source.Layer;
            var paper = source.PaperSpace || inheritedPaper;
            var invisible = source.Invisible || inheritedInvisible;
            var provenance = path.Count == 0 && layer == source.Layer && paper == source.PaperSpace && invisible == source.Invisible
                ? source
                : source with { Layer = layer, InsertPath = path, PaperSpace = paper, Invisible = invisible };
            if (entity is DxfInsert insert)
            {
                if (!blocks.TryGetValue(insert.BlockName, out var block))
                {
                    Warn("block-missing", source.Line, "INSERT references undefined block " + insert.BlockName);
                    return;
                }
                if (stack.Contains(block.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Warn("block-cycle", source.Line, "INSERT of block " + block.Name + " inside itself skipped");
                    return;
                }
                if (depth + 1 > options.MaxDepth)
                    throw new DxfFormatException("explode-limit", source.Line, "block nesting deeper than " + options.MaxDepth.ToString(CultureInfo.InvariantCulture));
                var key = source.Handle ?? "@" + source.Line.ToString(CultureInfo.InvariantCulture);
                if (!insert.IsArray)
                {
                    var local = Affine.Translate(insert.Insertion)
                        .Compose(Affine.Rotate(insert.RotationDegrees))
                        .Compose(Affine.Scale(insert.ScaleX, insert.ScaleY))
                        .Compose(Affine.Translate(new Point2(-block.BasePoint.X, -block.BasePoint.Y)));
                    EmitBlock(insert, block, key, transform, transform.Compose(local), path.Append(key).ToArray(), layer, paper, invisible, depth);
                    return;
                }
                // MINSERT: one placement per cell, row by row; every cell after the first also counts as visited work.
                for (var row = 0; row < insert.Rows; row++)
                {
                    for (var column = 0; column < insert.Columns; column++)
                    {
                        if (row > 0 || column > 0) Spend(source.Line);
                        var offset = new Point2(column * insert.ColumnSpacing, row * insert.RowSpacing);
                        var rotation = Affine.Rotate(insert.RotationDegrees);
                        var local = Affine.Translate(insert.Insertion)
                            .Compose(rotation)
                            .Compose(Affine.Translate(offset))
                            .Compose(Affine.Scale(insert.ScaleX, insert.ScaleY))
                            .Compose(Affine.Translate(new Point2(-block.BasePoint.X, -block.BasePoint.Y)));
                        // ATTRIBs are placed for cell (0,0); other cells shift them by the rotated cell offset.
                        var attributes = transform.Compose(Affine.Translate(rotation.ApplyVector(offset)));
                        EmitBlock(insert, block, key, attributes, transform.Compose(local), path.Append(DxfInsertPath.Cell(key, row, column)).ToArray(), layer, paper, invisible, depth);
                    }
                }
                return;
            }
            Add(transform.IsIdentity ? Relabel(entity, provenance) : Transform(entity, transform, provenance));
        }

        // Emits one placement of a block: its entities through world, then the INSERT's ATTRIBs (already in the
        // coordinates the INSERT lives in, moved by attributeTransform) and the block's constant ATTDEFs.
        // InsertHandle of the emitted attributes is the INSERT's bare key, also for MINSERT cells.
        private void EmitBlock(DxfInsert insert, DxfBlock block, string key, Affine attributeTransform, Affine world, string[] childPath, string layer, bool paper, bool invisible, int depth)
        {
            stack.Add(block.Name);
            foreach (var child in block.Entities)
            {
                if (child is DxfAttribute { IsDefinition: true }) continue;
                Emit(child, world, childPath, layer, paper, invisible, depth + 1);
            }
            stack.RemoveAt(stack.Count - 1);
            // ATTRIBs are already placed in the coordinates the INSERT itself lives in. Of the block's ATTDEFs only
            // constant ones are drawn (placed through the insert); AutoCAD never draws a non-constant ATTDEF.
            HashSet<string>? tags = insert.Attributes.Count > 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            foreach (var attribute in insert.Attributes)
            {
                tags!.Add(attribute.Tag);
                EmitAttribute(attribute with { InsertHandle = key, BlockName = block.Name }, attributeTransform, childPath, layer, paper, invisible);
            }
            foreach (var child in block.Entities)
            {
                if (child is not DxfAttribute { IsDefinition: true, IsConstant: true } definition || (tags is not null && tags.Contains(definition.Tag))) continue;
                EmitAttribute(definition with { InsertHandle = key, BlockName = block.Name }, world, childPath, layer, paper, invisible);
            }
        }

        private void EmitAttribute(DxfAttribute attribute, Affine transform, IReadOnlyList<string> path, string layer, bool paper, bool invisible)
        {
            var source = attribute.Provenance;
            Spend(source.Line);
            var provenance = source with
            {
                Layer = source.Layer == "0" ? layer : source.Layer,
                InsertPath = path,
                PaperSpace = source.PaperSpace || paper,
                Invisible = source.Invisible || invisible,
            };
            Add(Transform(attribute, transform, provenance));
        }

        private void Add(DxfEntity? entity)
        {
            if (entity is null) return;
            if (Output.Count >= options.MaxEntities)
                throw new DxfFormatException("explode-limit", entity.Provenance.Line, "exploding inserts produces more than " + options.MaxEntities.ToString(CultureInfo.InvariantCulture) + " entities");
            Output.Add(entity);
        }

        private static DxfEntity Relabel(DxfEntity entity, DxfProvenance provenance) => ReferenceEquals(entity.Provenance, provenance) ? entity : entity with { Provenance = provenance };

        private DxfEntity? Transform(DxfEntity entity, Affine m, DxfProvenance p)
        {
            var mirrored = m.Determinant < 0;
            switch (entity)
            {
                case DxfLine line:
                    return new DxfLine(p, m.Apply(line.Start), m.Apply(line.End));
                case DxfCircle circle:
                    if (m.IsConformal) return new DxfCircle(p, m.Apply(circle.Center), circle.Radius * m.UniformScale);
                    return Tessellate(p, m, circle.Center, circle.Radius, 0, 360, true);
                case DxfArc arc:
                    if (m.IsConformal)
                    {
                        var center = m.Apply(arc.Center);
                        var a0 = DxfGeometry.Degrees(m.Apply(DxfGeometry.PointAt(arc.Center, arc.Radius, arc.StartAngleDegrees)) - center);
                        var a1 = DxfGeometry.Degrees(m.Apply(DxfGeometry.PointAt(arc.Center, arc.Radius, arc.EndAngleDegrees)) - center);
                        return mirrored ? new DxfArc(p, center, arc.Radius * m.UniformScale, a1, a0) : new DxfArc(p, center, arc.Radius * m.UniformScale, a0, a1);
                    }
                    return Tessellate(p, m, arc.Center, arc.Radius, arc.StartAngleDegrees, DxfGeometry.Span(arc.StartAngleDegrees, arc.EndAngleDegrees), false);
                case DxfPolyline polyline:
                    if (m.IsConformal || polyline.Vertices.All(v => v.Bulge == 0))
                        return new DxfPolyline(p, polyline.Vertices.Select(v => new DxfVertex(m.Apply(v.Point), mirrored ? -v.Bulge : v.Bulge)).ToArray(), polyline.Closed);
                    return TessellatePolyline(p, m, polyline);
                case DxfText text:
                    {
                        var (insertion, height, rotation) = Place(m, text.Insertion, text.Height, text.RotationDegrees);
                        return text with { Provenance = p, Insertion = insertion, Height = height, RotationDegrees = rotation };
                    }
                case DxfAttribute attribute:
                    {
                        var (insertion, height, rotation) = Place(m, attribute.Insertion, attribute.Height, attribute.RotationDegrees);
                        return attribute with { Provenance = p, Insertion = insertion, Height = height, RotationDegrees = rotation };
                    }
                default:
                    return entity with { Provenance = p };
            }
        }

        private static (Point2 Insertion, double Height, double RotationDegrees) Place(Affine m, Point2 insertion, double height, double rotationDegrees)
        {
            if (m.IsIdentity) return (insertion, height, rotationDegrees);
            var direction = m.ApplyVector(new Point2(Math.Cos(rotationDegrees * Math.PI / 180), Math.Sin(rotationDegrees * Math.PI / 180)));
            var up = m.ApplyVector(new Point2(-Math.Sin(rotationDegrees * Math.PI / 180), Math.Cos(rotationDegrees * Math.PI / 180)));
            return (m.Apply(insertion), height * up.Length, DxfGeometry.Degrees(direction));
        }

        private DxfPolyline Tessellate(DxfProvenance p, Affine m, Point2 center, double radius, double startDegrees, double spanDegrees, bool closed)
        {
            Warn("arc-tessellated", p.Line, p.EntityType + " under a non-uniform insert scale tessellated into a polyline");
            var points = ArcPoints(center, radius, startDegrees, spanDegrees, closed);
            return new DxfPolyline(p, points.Select(point => new DxfVertex(m.Apply(point), 0)).ToArray(), closed);
        }

        private DxfPolyline TessellatePolyline(DxfProvenance p, Affine m, DxfPolyline polyline)
        {
            Warn("arc-tessellated", p.Line, "LWPOLYLINE bulges under a non-uniform insert scale tessellated");
            var points = new List<Point2>();
            var segments = polyline.Segments();
            foreach (var segment in segments)
            {
                if (points.Count == 0) points.Add(segment.Start);
                if (segment.Center is { } center)
                {
                    var arc = segment.Bulge > 0
                        ? ArcPoints(center, segment.Radius, segment.StartAngleDegrees, DxfGeometry.Span(segment.StartAngleDegrees, segment.EndAngleDegrees), false)
                        : ArcPoints(center, segment.Radius, segment.StartAngleDegrees, DxfGeometry.Span(segment.StartAngleDegrees, segment.EndAngleDegrees), false).AsEnumerable().Reverse().ToList();
                    for (var k = 1; k < arc.Count - 1; k++) points.Add(arc[k]);
                }
                points.Add(segment.End);
            }
            // A closed polyline's last segment returns to the first vertex, which is already the first point.
            if (polyline.Closed && points.Count > 1) points.RemoveAt(points.Count - 1);
            return new DxfPolyline(p, points.Select(point => new DxfVertex(m.Apply(point), 0)).ToArray(), polyline.Closed);
        }

        private List<Point2> ArcPoints(Point2 center, double radius, double startDegrees, double spanDegrees, bool closed)
        {
            var steps = Math.Max(4, (int)Math.Ceiling(options.ArcTessellationSegments * spanDegrees / 360));
            var points = new List<Point2>(steps + 1);
            var count = closed ? steps : steps + 1;
            for (var k = 0; k < count; k++) points.Add(DxfGeometry.PointAt(center, radius, startDegrees + spanDegrees * k / steps));
            return points;
        }
    }
}
