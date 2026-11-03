using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// Family commands executor decisions, readback checks and readback JSON, in millimetres and degrees. Autodesk-free on
// purpose: tests/revit-commands source-links this file. CommandsExecutorFamilies.cs reads the Revit facts (lengths
// converted with UnitUtils) and calls the Revit API. Revit enum values travel as their ToString() text
// (FamilyPlacementType "OneLevelBased", "OneLevelBasedHosted", "WorkPlaneBased", ...; BuiltInCategory "OST_...").

public sealed record FamilySymbolRow(string Type, string UniqueId, bool Active);
// CategoryName: Family.FamilyCategory.Name (localized); BuiltInCategory: its BuiltInCategory name; null when unknown.
public sealed record ObservedFamily(string UniqueId, string Name, string? CategoryName, string? BuiltInCategory, string PlacementType, IReadOnlyList<FamilySymbolRow> Symbols);
// LevelUid: the instance's level; HostUid: FamilyInstance.Host (for a level-based instance Revit may report the level);
// Point/ZMm: the LocationPoint in plan and its elevation; OffsetMm: INSTANCE_ELEVATION_PARAM (Elevation from Level);
// RotationDeg: LocationPoint.Rotation in degrees, normalised to [0, 360). Nullable values could not be read.
public sealed record ObservedFamilyInstance(string UniqueId, string SymbolUid, string Family, string Type, string? BuiltInCategory, string? LevelUid, string? HostUid, PlanPoint? Point, double? ZMm, double? OffsetMm, double? RotationDeg);

public static class CommandRulesFamilies
{
    public const string Loaded = "loaded";
    public const string AlreadyLoaded = "already_loaded";
    public const string Reloaded = "reloaded";
    public const string Unchanged = "unchanged";
    // Rotation readback tolerance in degrees.
    public const double RotationToleranceDeg = 1e-3;

    // Document.LoadFamily is called for a family the document does not have, or to reload one when overwrite is set;
    // an existing family is never touched otherwise.
    public static bool CallsLoad(bool existed, bool overwrite) => !existed || overwrite;

    // existed: a family of that name was in the document before; loadReturned: what LoadFamily returned (false when
    // not called); presentAfter: the family is in the document afterwards.
    public static string LoadOutcome(bool existed, bool overwrite, bool loadReturned, bool presentAfter)
    {
        if (!presentAfter) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.path", "Revit did not load the family; the file may not be a family for this Revit version");
        if (!existed) return loadReturned ? Loaded : throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.path", "Revit reported the family was not loaded");
        if (!overwrite) return AlreadyLoaded;
        return loadReturned ? Reloaded : Unchanged;
    }

    // IFamilyLoadOptions.OnFamilyFound: (continue loading, overwrite parameter values).
    public static (bool Continue, bool OverwriteParameterValues) OnFamilyFound(bool overwrite) => (overwrite, overwrite);

    // IFamilyLoadOptions.OnSharedFamilyFound: (continue loading, use the family's version rather than the project's,
    // overwrite parameter values). Loading continues either way; the project's shared family wins unless overwrite.
    public static (bool Continue, bool UseFamilyVersion, bool OverwriteParameterValues) OnSharedFamilyFound(bool overwrite) => (true, overwrite, overwrite);

    // The executor repeats the client's source guard on its own policy (any same-user process can write to the pipe),
    // then checks the file itself: length (null when missing or unreadable) within the cap, and fullPath
    // (Path.GetFullPath of the path, null when it could not be resolved) naming the same file.
    public static void CheckSource(string path, IReadOnlyList<string> roots, RevitFileSystemProbe probe, long? length, string? fullPath)
    {
        if (RevitFamilySourceGuard.Check(path, roots, probe) is { } refused) throw Refused(refused);
        if (fullPath is null || !CommandAdmission.SamePath(fullPath, path)) throw Refused("the path does not resolve to itself");
        if (RevitFamilySourceGuard.SizeProblem(length) is { } size) throw Refused(size);
    }

    static RevitCommandException Refused(string detail) => new(RevitCommandCodes.FamilySourceRefused, "args.path", detail);

    // family_instance.place makes a free instance on a level: only a one-level-based family fits.
    public static void CheckPlacement(string placementType)
    {
        var reason = placementType switch
        {
            "OneLevelBased" => null,
            "OneLevelBasedHosted" => "the family is hosted (wall, ceiling, floor or roof based) and needs a host; hosted placement is not supported yet, use a level-based version of the family",
            "WorkPlaneBased" => "the family is work-plane or face based and needs a face or work plane; that placement is not supported yet, use a level-based version of the family",
            _ => "the family is " + placementType + "; family_instance.place takes a OneLevelBased family (see placement_type in revit_query_family_symbols)",
        };
        if (reason is not null) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.symbol", reason);
    }

    public static double NormalizeDegrees(double degrees)
    {
        var turned = degrees % 360;
        if (turned < 0) turned += 360;
        return turned >= 360 ? 0 : turned;
    }

    public static bool SameRotation(double expectedDeg, double observedDeg)
    {
        var difference = Math.Abs(NormalizeDegrees(expectedDeg) - NormalizeDegrees(observedDeg));
        return Math.Min(difference, 360 - difference) <= RotationToleranceDeg;
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackFamilies
{
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string F(double? value) => value is { } number ? F(number) : "unreadable";
    static string F(PlanPoint? point) => point is null ? "unreadable" : "(" + F(point.X) + ", " + F(point.Y) + ")";
    static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string? Family(string expectedName, ObservedFamily observed)
    {
        if (!string.Equals(observed.Name, expectedName, StringComparison.OrdinalIgnoreCase)) return "the loaded family is named '" + observed.Name + "', expected '" + expectedName + "' (the file name)";
        if (observed.Symbols.Count == 0) return "the family '" + observed.Name + "' has no types";
        return null;
    }

    // expectedZMm: the level's project elevation plus the offset; used when the instance has no elevation parameter.
    public static string? Instance(string symbolUid, string levelUid, PlanPoint point, double offsetMm, double expectedZMm, double rotationDeg, ObservedFamilyInstance observed)
    {
        if (!Same(observed.SymbolUid, symbolUid)) return "instance type is " + observed.SymbolUid + ", expected " + symbolUid;
        if (!Same(observed.LevelUid, levelUid)) return "instance level is " + (observed.LevelUid ?? "unreadable") + ", expected " + levelUid;
        if (observed.Point is null || !CommandGeometry.Near(observed.Point, point)) return "instance is at " + F(observed.Point) + ", expected " + F(point);
        if (observed.OffsetMm is { } offset)
        {
            if (!CommandGeometry.Near(offset, offsetMm)) return "instance offset (Elevation from Level) is " + F(offset) + " mm, expected " + F(offsetMm);
        }
        else if (observed.ZMm is not { } z || !CommandGeometry.Near(z, expectedZMm)) return "instance offset unreadable and its elevation is " + F(observed.ZMm) + " mm, expected " + F(expectedZMm);
        if (observed.RotationDeg is not { } rotation || !CommandRulesFamilies.SameRotation(rotationDeg, rotation)) return "instance rotation is " + F(observed.RotationDeg) + " degrees, expected " + F(CommandRulesFamilies.NormalizeDegrees(rotationDeg));
        return null;
    }

    // ---- Readback JSON (snake_case, millimetres, degrees, rows in a stable order) ----

    static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number)) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    public static string FamilyJson(string outcome, string sourcePath, ObservedFamily family) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("family");
        writer.WriteString("name", family.Name);
        writer.WriteString("unique_id", family.UniqueId);
        writer.WriteString("outcome", outcome);
        writer.WriteString("source_path", sourcePath);
        Text(writer, "category", RevitFamilyRules.FriendlyCategory(family.BuiltInCategory));
        Text(writer, "category_name", family.CategoryName);
        Text(writer, "built_in_category", family.BuiltInCategory);
        writer.WriteString("placement_type", family.PlacementType);
        writer.WriteStartArray("symbols");
        foreach (var symbol in family.Symbols.OrderBy(symbol => symbol.Type, StringComparer.Ordinal).ThenBy(symbol => symbol.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("type", symbol.Type);
            writer.WriteString("reference", CommandReferences.Qualified(family.Name, symbol.Type));
            writer.WriteString("unique_id", symbol.UniqueId);
            writer.WriteBoolean("active", symbol.Active);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string InstanceJson(ObservedFamilyInstance instance) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("family_instance");
        writer.WriteString("unique_id", instance.UniqueId);
        writer.WriteString("symbol_unique_id", instance.SymbolUid);
        writer.WriteString("family", instance.Family);
        writer.WriteString("type", instance.Type);
        writer.WriteString("reference", CommandReferences.Qualified(instance.Family, instance.Type));
        Text(writer, "category", RevitFamilyRules.FriendlyCategory(instance.BuiltInCategory));
        Text(writer, "built_in_category", instance.BuiltInCategory);
        Text(writer, "level_unique_id", instance.LevelUid);
        Text(writer, "host_unique_id", instance.HostUid);
        if (instance.Point is null) writer.WriteNull("point");
        else
        {
            writer.WriteStartArray("point");
            writer.WriteNumberValue(instance.Point.X);
            writer.WriteNumberValue(instance.Point.Y);
            writer.WriteEndArray();
        }
        Number(writer, "z_mm", instance.ZMm);
        Number(writer, "offset_mm", instance.OffsetMm);
        Number(writer, "rotation_deg", instance.RotationDeg);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
