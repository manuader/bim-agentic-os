using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// P4-01 view commands executor decisions, expected section frame, readback checks and readback JSON, in millimetres.
// Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorViews.cs reads the Revit
// facts (lengths converted with UnitUtils) and calls the Revit API. Names of Revit enums travel as their ToString()
// text (ViewFamily "FloorPlan", ViewType "ThreeD", ...).

// Directions are unit vectors; positions are millimetres in project coordinates.
public sealed record Vec3(double X, double Y, double Z);
public sealed record ViewFamilyTypeRow(string Name, string UniqueId, string ViewFamily, bool IsDefault);
public sealed record ViewTemplateRow(string Name, string UniqueId, string ViewType);
public sealed record ViewTypeCandidate(string UniqueId, string Name, string ViewFamily);
// What Revit reports for a created view. Scale null: unreadable. LevelUid: ViewPlan.GenLevel, null for other views.
public sealed record ObservedView(string UniqueId, string ViewType, string TypeUid, string Name, int? Scale, string? TemplateUid, bool IsTemplate, string? LevelUid);
// The section box handed to ViewSection.CreateSection: transform origin (mm) and basis, then Min/Max in that frame (mm).
public sealed record SectionFrame(Vec3 Origin, Vec3 BasisX, Vec3 BasisY, Vec3 BasisZ, Vec3 Min, Vec3 Max);
// A created section, read from the view itself: RightDirection, UpDirection, ViewDirection (toward the viewer),
// Origin, the crop box corners transformed to project coordinates, the Far Clip Offset and whether the crop is active.
public sealed record ObservedSection(Vec3 Right, Vec3 Up, Vec3 ViewDirection, Vec3 Origin, Vec3 CropMin, Vec3 CropMax, double? FarClipMm, bool CropActive);
// A created 3D view: View3D.IsPerspective, IsSectionBoxActive, the GetSectionBox transform basis and its Min/Max
// corners in project coordinates (null when no box can be read), and the view orientation.
// A 3D view orientation for View3D.SetOrientation: eye position (mm), up and forward unit directions.
public sealed record ViewFrame3D(Vec3 Eye, Vec3 Up, Vec3 Forward);
public sealed record Observed3D(bool IsPerspective, bool SectionBoxActive, Vec3? BoxBasisX, Vec3? BoxBasisY, Vec3? BoxBasisZ, Vec3? BoxMin, Vec3? BoxMax, Vec3 Forward, Vec3 Up);

public static class CommandRulesViews
{
    // Command family -> Revit ViewFamily name.
    public static string ViewFamily(string family) => family switch
    {
        "floor_plan" => "FloorPlan",
        "ceiling_plan" => "CeilingPlan",
        "section" => "Section",
        "three_d" => "ThreeDimensional",
        _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.family", "unknown view family " + family),
    };

    // Command family -> Revit ViewType name of the views it creates.
    public static string ViewType(string family) => family switch
    {
        "floor_plan" => "FloorPlan",
        "ceiling_plan" => "CeilingPlan",
        "section" => "Section",
        "three_d" => "ThreeD",
        _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.family", "unknown view family " + family),
    };

    // The ViewFamilyType to create with. A reference resolves among every view family type (name or UniqueId rules of
    // CommandReferences) and must be of the family; without one, the document default of the family, else the only
    // type of the family.
    public static int PickType(IReadOnlyList<ViewTypeCandidate> types, string? reference, string? defaultUid, string viewFamily, string field)
    {
        if (reference is not null)
        {
            var index = CommandReferences.Resolve(reference, types.Select(type => new CommandCandidate(type.UniqueId, type.Name)).ToArray(), field, "view family type");
            if (types[index].ViewFamily != viewFamily)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "view family type '" + types[index].Name + "' is a " + types[index].ViewFamily + " type; pick a " + viewFamily + " type from revit_query_view_family_types");
            return index;
        }
        var family = Enumerable.Range(0, types.Count).Where(i => types[i].ViewFamily == viewFamily).ToArray();
        if (defaultUid is not null)
            foreach (var i in family)
                if (string.Equals(types[i].UniqueId, defaultUid, StringComparison.OrdinalIgnoreCase)) return i;
        if (family.Length == 1) return family[0];
        if (family.Length == 0) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, field, "the document has no " + viewFamily + " view family type");
        throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, field, family.Length + " " + viewFamily + " view family types and no default; pass args.type from revit_query_view_family_types");
    }

    public static void CheckTemplate(string templateViewType, string viewType)
    {
        if (templateViewType != viewType) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_template", "the view template is for " + templateViewType + " views, not " + viewType);
    }

    // Names of existing views of the same ViewType and of every view template. Revit refuses a duplicate; the check is
    // case-insensitive (stricter than any Revit rule) so a collision fails closed here instead of as a Revit throw.
    public static void CheckNameFree(string? name, IEnumerable<string> existing)
    {
        if (name is not null && existing.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "a view or view template named '" + name + "' (ignoring case) already exists");
    }

    // A view family type with a default template (ViewFamilyType.DefaultTemplateId) applies it to new views, and the
    // template may control the scale, so an explicit scale is refused like scale with view_template.
    public static void CheckScaleAllowed(bool hasDefaultTemplate, int? scale)
    {
        if (hasDefaultTemplate && scale is not null) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.scale", "this view family type applies a default view template that may control the scale; omit scale or pick a type without a default template");
    }

    // Unit vector along the section line and the line length.
    static (double X, double Y, double Length) Direction(ViewSectionCreateArgs args)
    {
        double dx = args.End.X.Millimetres - args.Start.X.Millimetres, dy = args.End.Y.Millimetres - args.Start.Y.Millimetres;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return (dx / length, dy / length, length);
    }

    // Where the view looks: the left of start -> end (the line direction turned +90 degrees about Z).
    public static Vec3 LookingDirection(ViewSectionCreateArgs args)
    {
        var (x, y, _) = Direction(args);
        return new Vec3(-y, x, 0);
    }

    // The view's right direction: start -> end (what CommandReadbackViews.Section expects from View.RightDirection).
    public static Vec3 RightDirection(ViewSectionCreateArgs args)
    {
        var (x, y, _) = Direction(args);
        return new Vec3(x, y, 0);
    }

    // The box handed to ViewSection.CreateSection. Revit 2025 API reference: "The view direction of the resulting section
    // will be sectionBox.Transform.BasisZ and the up direction will be sectionBox.Transform.BasisY. The right hand
    // direction will be computed so that (right, up, view direction) form a left handed coordinate system", i.e. Revit
    // takes right = BasisZ x BasisY and ignores BasisX; View.ViewDirection then points toward the viewer (-BasisZ). So
    // BasisZ is the looking direction (the left of start -> end) and the view's right is start -> end; BasisX = BasisY x
    // BasisZ keeps the transform orthonormal and right-handed (it points end -> start). Origin at the line midpoint (z 0,
    // so frame Y is the project elevation); the box spans the line, bottom to top elevation, from the cut plane (z 0) to
    // the far clip (z depth, ahead of the cut plane). The previous frame (BasisZ = X cross Y toward the viewer) made every
    // section look to the right of the line: the owner's Revit 2025.2 reported RightDirection (-0.707, -0.707, 0) for a
    // line along (0.707, 0.707) on 2026-11-04 (axis-aligned lines had never run in Revit).
    public static SectionFrame Section(ViewSectionCreateArgs args)
    {
        var (x, y, length) = Direction(args);
        var origin = new Vec3((args.Start.X.Millimetres + args.End.X.Millimetres) / 2, (args.Start.Y.Millimetres + args.End.Y.Millimetres) / 2, 0);
        return new SectionFrame(origin, new Vec3(-x, -y, 0), new Vec3(0, 0, 1), new Vec3(-y, x, 0),
            new Vec3(-length / 2, args.Bottom.Millimetres, 0), new Vec3(length / 2, args.Top.Millimetres, args.Depth.Millimetres));
    }

    // Distance from the target to the eye when the view has no model extents to measure (an empty document).
    public const double MinEyeDistanceMm = 1000;

    // View3D.SetOrientation frame for an eye direction (unit, from the target toward the eye): forward = -eye; up = the
    // project vertical made perpendicular to forward (tilted toward the view), or project north (+Y) when the eye is
    // straight above or below; the eye position on the eye direction, distanceMm (at least MinEyeDistanceMm) from the
    // target. An isometric view does not depend on the distance; the target centres the view.
    public static ViewFrame3D Orientation(ViewDirection3 eye, Vec3 target, double distanceMm)
    {
        var length = Math.Sqrt(eye.X * eye.X + eye.Y * eye.Y + eye.Z * eye.Z);
        var e = new Vec3(eye.X / length, eye.Y / length, eye.Z / length);
        var forward = new Vec3(-e.X, -e.Y, -e.Z);
        Vec3 up;
        if (Math.Sqrt(e.X * e.X + e.Y * e.Y) < 1e-9) up = new Vec3(0, 1, 0);
        else
        {
            // Z minus its component along the eye direction.
            var ux = -e.Z * e.X; var uy = -e.Z * e.Y; var uz = 1 - e.Z * e.Z;
            var n = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            up = new Vec3(ux / n, uy / n, uz / n);
        }
        var distance = double.IsFinite(distanceMm) && distanceMm > MinEyeDistanceMm ? distanceMm : MinEyeDistanceMm;
        return new ViewFrame3D(new Vec3(target.X + e.X * distance, target.Y + e.Y * distance, target.Z + e.Z * distance), up, forward);
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackViews
{
    // Unit-vector components agree within this (well above double/feet round-trip noise).
    public const double DirectionTolerance = 1e-6;

    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string F(Vec3 v) => "(" + F(v.X) + ", " + F(v.Y) + ", " + F(v.Z) + ")";
    static bool SameDirection(Vec3 a, Vec3 b) => Math.Abs(a.X - b.X) <= DirectionTolerance && Math.Abs(a.Y - b.Y) <= DirectionTolerance && Math.Abs(a.Z - b.Z) <= DirectionTolerance;
    static bool Near(double a, double b) => CommandGeometry.Near(a, b);
    static bool Near(Vec3 a, Vec3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
    static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static Vec3 Minus(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    // templateUid: the requested template after resolution (null when none was requested; a template the view family
    // type applies to new views is then accepted). levelUid: the plan's level, null for views without one.
    public static string? View(string viewType, string typeUid, ViewOptions options, string? templateUid, string? levelUid, ObservedView observed)
    {
        if (observed.ViewType != viewType) return "view type is " + observed.ViewType + ", expected " + viewType;
        if (observed.IsTemplate) return "the created view is a view template";
        if (!string.Equals(observed.TypeUid, typeUid, StringComparison.OrdinalIgnoreCase)) return "view family type is " + observed.TypeUid + ", expected " + typeUid;
        if (levelUid is not null && !string.Equals(observed.LevelUid, levelUid, StringComparison.OrdinalIgnoreCase)) return "plan level is " + (observed.LevelUid ?? "unreadable") + ", expected " + levelUid;
        if (options.Name is { } name && !string.Equals(observed.Name, name, StringComparison.Ordinal)) return "view name is '" + observed.Name + "', expected '" + name + "'";
        if (options.Scale is { } scale && observed.Scale != scale) return "view scale is 1:" + (observed.Scale?.ToString(CultureInfo.InvariantCulture) ?? "unreadable") + ", expected 1:" + scale.ToString(CultureInfo.InvariantCulture);
        if (templateUid is not null && !string.Equals(observed.TemplateUid, templateUid, StringComparison.OrdinalIgnoreCase)) return "view template is " + (observed.TemplateUid ?? "none") + ", expected " + templateUid;
        return null;
    }

    public static string? Section(ViewSectionCreateArgs args, ObservedSection observed)
    {
        // Independent of the box frame: right runs start -> end, up is vertical, the view looks to the left of the line
        // (View.ViewDirection points toward the viewer, the opposite way).
        var frame = CommandRulesViews.Section(args);
        var right = CommandRulesViews.RightDirection(args);
        var looking = CommandRulesViews.LookingDirection(args);
        var toward = new Vec3(-looking.X, -looking.Y, -looking.Z);
        if (!SameDirection(observed.Right, right)) return "section right direction is " + F(observed.Right) + ", expected " + F(right);
        if (!SameDirection(observed.Up, new Vec3(0, 0, 1))) return "section up direction is " + F(observed.Up) + ", expected " + F(new Vec3(0, 0, 1));
        if (!SameDirection(observed.ViewDirection, toward)) return "section view direction is " + F(observed.ViewDirection) + ", expected " + F(toward);
        var start = new Vec3(args.Start.X.Millimetres, args.Start.Y.Millimetres, 0);
        var offset = Dot(Minus(observed.Origin, start), looking);
        if (!Near(offset, 0)) return "section cut plane is " + F(offset) + " mm off the requested line";
        if (!observed.CropActive) return "section crop box is not active";
        var length = frame.Max.X - frame.Min.X;
        double a = Dot(Minus(observed.CropMin, start), right), b = Dot(Minus(observed.CropMax, start), right);
        if (!Near(Math.Min(a, b), 0) || !Near(Math.Max(a, b), length)) return "section crop spans " + F(Math.Min(a, b)) + " to " + F(Math.Max(a, b)) + " mm along the line, expected 0 to " + F(length);
        double low = Math.Min(observed.CropMin.Z, observed.CropMax.Z), high = Math.Max(observed.CropMin.Z, observed.CropMax.Z);
        if (!Near(low, args.Bottom.Millimetres) || !Near(high, args.Top.Millimetres)) return "section crop spans elevation " + F(low) + " to " + F(high) + " mm, expected " + F(args.Bottom.Millimetres) + " to " + F(args.Top.Millimetres);
        if (observed.FarClipMm is not { } far || !Near(far, args.Depth.Millimetres)) return "section far clip offset is " + (observed.FarClipMm is { } f ? F(f) : "unreadable") + " mm, expected " + F(args.Depth.Millimetres);
        return null;
    }

    // eye: the requested eye direction (null: Revit's default orientation, not checked).
    public static string? ThreeD(ViewBox? box, Observed3D observed, ViewDirection3? eye = null)
    {
        if (observed.IsPerspective) return "the 3D view is a perspective view, expected isometric";
        if (eye is not null)
        {
            var expected = CommandRulesViews.Orientation(eye, new Vec3(0, 0, 0), CommandRulesViews.MinEyeDistanceMm);
            if (!SameDirection(observed.Forward, expected.Forward)) return "the 3D view forward direction is " + F(observed.Forward) + ", expected " + F(expected.Forward);
            if (!SameDirection(observed.Up, expected.Up)) return "the 3D view up direction is " + F(observed.Up) + ", expected " + F(expected.Up);
        }
        if (box is null) return observed.SectionBoxActive ? "the 3D view has an active section box; none was requested" : null;
        if (!observed.SectionBoxActive) return "the 3D view section box is not active";
        if (observed is not { BoxBasisX: { } x, BoxBasisY: { } y, BoxBasisZ: { } z, BoxMin: { } min, BoxMax: { } max }) return "the 3D view section box is unreadable";
        if (!SameDirection(x, new Vec3(1, 0, 0)) || !SameDirection(y, new Vec3(0, 1, 0)) || !SameDirection(z, new Vec3(0, 0, 1))) return "the 3D view section box is rotated";
        var expectedMin = new Vec3(box.MinX.Millimetres, box.MinY.Millimetres, box.MinZ.Millimetres);
        var expectedMax = new Vec3(box.MaxX.Millimetres, box.MaxY.Millimetres, box.MaxZ.Millimetres);
        if (!Near(min, expectedMin) || !Near(max, expectedMax)) return "the 3D view section box is " + F(min) + " to " + F(max) + ", expected " + F(expectedMin) + " to " + F(expectedMax);
        return null;
    }

    // ---- Readback JSON (snake_case, millimetres, rows in a stable order) ----

    static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    static void Vector(Utf8JsonWriter writer, string name, Vec3? value)
    {
        if (value is null) { writer.WriteNull(name); return; }
        writer.WriteStartArray(name);
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }

    public static string ViewFamilyTypesJson(IEnumerable<ViewFamilyTypeRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("view_family_types");
        foreach (var row in rows.OrderBy(row => row.ViewFamily, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            writer.WriteString("view_family", row.ViewFamily);
            writer.WriteBoolean("is_default", row.IsDefault);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string ViewTemplatesJson(IEnumerable<ViewTemplateRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("view_templates");
        foreach (var row in rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            writer.WriteString("view_type", row.ViewType);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    static void ViewMembers(Utf8JsonWriter writer, ObservedView view)
    {
        writer.WriteString("unique_id", view.UniqueId);
        writer.WriteString("name", view.Name);
        writer.WriteString("view_type", view.ViewType);
        writer.WriteString("type_unique_id", view.TypeUid);
        if (view.Scale is { } scale) writer.WriteNumber("scale", scale);
        else writer.WriteNull("scale");
        Text(writer, "view_template_unique_id", view.TemplateUid);
        Text(writer, "level_unique_id", view.LevelUid);
    }

    static string Wrapped(ObservedView view, Action<Utf8JsonWriter>? extra) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("view");
        ViewMembers(writer, view);
        extra?.Invoke(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string ViewJson(ObservedView view) => Wrapped(view, null);

    public static string SectionJson(ObservedView view, ObservedSection section) => Wrapped(view, writer =>
    {
        writer.WriteStartObject("section");
        Vector(writer, "right_direction", section.Right);
        Vector(writer, "up_direction", section.Up);
        Vector(writer, "view_direction", section.ViewDirection);
        Vector(writer, "origin", section.Origin);
        Vector(writer, "crop_min", section.CropMin);
        Vector(writer, "crop_max", section.CropMax);
        if (section.FarClipMm is { } far && double.IsFinite(far)) writer.WriteNumber("far_clip_mm", far);
        else writer.WriteNull("far_clip_mm");
        writer.WriteBoolean("crop_box_active", section.CropActive);
        writer.WriteEndObject();
    });

    public static string ThreeDJson(ObservedView view, Observed3D threeD) => Wrapped(view, writer =>
    {
        writer.WriteStartObject("three_d");
        writer.WriteBoolean("perspective", threeD.IsPerspective);
        writer.WriteBoolean("section_box_active", threeD.SectionBoxActive);
        // GetSectionBox returns the model extents even when the box is off: report a box only when it is active.
        if (threeD.SectionBoxActive && threeD.BoxMin is { } min && threeD.BoxMax is { } max)
        {
            writer.WriteStartObject("section_box");
            Vector(writer, "min", min);
            Vector(writer, "max", max);
            writer.WriteEndObject();
        }
        else writer.WriteNull("section_box");
        Vector(writer, "forward_direction", threeD.Forward);
        Vector(writer, "up_direction", threeD.Up);
        // ForwardDirection points from the eye toward the target (Revit 2025 API), so the eye sits at -forward:
        // the same convention as the view_from / eye_direction arguments.
        Vector(writer, "eye_direction", new Vec3(-threeD.Forward.X, -threeD.Forward.Y, -threeD.Forward.Z));
        writer.WriteEndObject();
    });
}
