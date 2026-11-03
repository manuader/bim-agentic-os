using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// Family commands: family.load and family_instance.place, plus the family category vocabulary query.family_symbols
// uses. Same envelope (protocol_version 1), schema subset, unit rules, copies-only guard and operation_id receipts as
// the earlier catalogue parts. family.load additionally reads a file: its path must lie under the repository's
// .bim/families or the Autodesk RVT 2025 content library (read-only source), with no link on the way, which the
// client (RevitCommandService) and the add-in executor (CommandsExecutorFamilies.cs) both check. Vendor-free like
// every RevitCommands*.cs file.

// Path: the absolute .rfa path as given; FamilyName: its file name without .rfa, the name Revit gives the family.
public sealed record FamilyLoadArgs(string Path, string FamilyName, bool Overwrite) : RevitCommandArgs;
// A non-hosted family instance of a symbol of Category at a plan point on Level, Offset above it (Revit's Elevation
// from Level), turned by Rotation about the vertical axis through the point.
public sealed record FamilyInstancePlaceArgs(string Category, string Symbol, string Level, Point2 Point, Length Offset, Angle Rotation) : RevitCommandArgs;

public static class RevitFamilyRules
{
    // query.family_symbols categories (door and window first, as before); names and BuiltInCategory as in the element
    // query vocabulary (RevitElementQueryRules.Categories).
    public static IReadOnlyList<string> SymbolCategories { get; } = new[]
    {
        "door", "window", "structural_column", "structural_framing", "generic_model", "specialty_equipment", "furniture",
        "lighting_fixture", "plumbing_fixture", "mechanical_equipment", "casework",
    };

    // family_instance.place categories: model families placed free on a level, without a host.
    public static IReadOnlyList<string> PlaceableCategories { get; } = new[]
    {
        "generic_model", "specialty_equipment", "furniture", "lighting_fixture", "plumbing_fixture", "mechanical_equipment", "casework",
    };

    public const double MaxRotationDegrees = 360;

    public static string BuiltInCategory(string category)
    {
        if (!SymbolCategories.Contains(category, StringComparer.Ordinal)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.category", "not a family category: " + category);
        return RevitElementQueryRules.BuiltInCategory(category, "args.category");
    }

    // The family category name for a BuiltInCategory name, or null when it is not one of SymbolCategories.
    public static string? FriendlyCategory(string? builtInCategory) =>
        builtInCategory is null ? null : SymbolCategories.FirstOrDefault(category => RevitElementQueryRules.BuiltInCategory(category, "category") == builtInCategory);
}

// Where family.load may read from. Pure text and link decisions; the probe supplies the file-system facts.
public static class RevitFamilySourceGuard
{
    public const string RepositoryFamiliesDirectory = ".bim/families";
    // Autodesk's content library for Revit 2025 (installed read-only content; per-language subfolders).
    public const string AutodeskContentRoot = @"C:\ProgramData\Autodesk\RVT 2025\Libraries";
    // Manufacturer families are typically a few MB; anything above 100 MiB is refused.
    public const long MaxFamilyBytes = 100L * 1024 * 1024;

    static readonly Regex ShortName = new(@"~[0-9]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<string> Roots(string? repositoryRoot) => repositoryRoot is null
        ? new[] { AutodeskContentRoot }
        : new[] { Path.Combine(repositoryRoot, ".bim", "families"), AutodeskContentRoot };

    // The last path segment without its .rfa extension.
    public static string FamilyName(string path)
    {
        var name = path.Split('/', '\\').Last();
        return name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    // Shape rules the binder applies; null when the path is acceptable text.
    public static string? PathProblem(string path)
    {
        if (!RevitCopiesGuard.IsAbsoluteNormalized(path)) return "absolute path without . or .. segments, device prefixes or ':' streams required";
        if (!path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) return "a Revit family file (.rfa) is required";
        if (FamilyName(path).Trim().Length == 0) return "the family file needs a name before .rfa";
        // An 8.3 short name (PROGRA~1) is another spelling of a directory; only long names compare against the roots.
        if (path.Split('/', '\\').Any(segment => ShortName.IsMatch(segment))) return "8.3 short names (with ~ and a digit) are not accepted; give the long path";
        return null;
    }

    // Null when allowed, else the reason (the caller reports family_source_refused at args.path). The family must lie
    // strictly inside one root (whole segments, the copies guard's case rules), and no component from that root down
    // to the file may be a symlink or junction, nor the file a hard link (same rule as RevitLinkGuard).
    public static string? Check(string path, IReadOnlyList<string> roots, RevitFileSystemProbe probe)
    {
        if (PathProblem(path) is { } problem) return problem;
        var root = roots.Where(candidate => RevitCopiesGuard.IsUnder(path, candidate) && !RevitCopiesGuard.IsUnder(candidate, path)).FirstOrDefault();
        if (root is null) return "families load only from the repository's " + RepositoryFamiliesDirectory + " folder or from " + AutodeskContentRoot + "; copy the .rfa into " + RepositoryFamiliesDirectory;
        if (RevitLinkGuard.Check(path, root, probe) is not null) return "a symlink, junction or hard link between " + root + " and the family file";
        return null;
    }

    // length: the file size in bytes, null when the file does not exist or cannot be read.
    public static string? SizeProblem(long? length) => length switch
    {
        null => "family file not found or unreadable",
        <= 0 => "the family file is empty",
        > MaxFamilyBytes => "the family file is larger than 100 MiB",
        _ => null,
    };
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> FamilySpecs = new(BuildFamilies);

    public static IReadOnlyList<RevitCommandSpec> Families => FamilySpecs.Value;

    static string FamilyCategoryEnum(IEnumerable<string> categories) => string.Join(",", categories.Select(category => "\"" + category + "\""));

    static IReadOnlyList<RevitCommandSpec> BuildFamilies()
    {
        var load = Fill("""{"type":"object","properties":{"path":{"type":"string","description":"Absolute path of the .rfa file, under the repository's .bim/families folder or @1@ (long names, no links)","minLength":5,"maxLength":1024,"pattern":"@0@"},"overwrite":{"type":"boolean","description":"Reload a family of the same name that is already loaded, overwriting its parameter values; default false (an already loaded family is left unchanged and read back)"}},"required":["path"],"additionalProperties":false}""",
            NoControl, AutodeskContentRootJson);

        var place = Fill("""{"type":"object","properties":{"category":{"type":"string","description":"Family category of the type","enum":[@0@]},"symbol":@1@,"level":@2@,"point":@3@,"offset_mm":{"type":"number","description":"Elevation of the instance above the level in millimetres (Revit Elevation from Level); may be negative, at most 9144000 either way; default 0"},"rotation_deg":{"type":"number","description":"Rotation about the vertical axis through the point, counter-clockwise in degrees, from -360 to 360; default 0"}},"required":["category","symbol","level","point"],"additionalProperties":false}""",
            FamilyCategoryEnum(RevitFamilyRules.PlaceableCategories), Reference("Family type: Family: Type, type name or UniqueId from revit_query_family_symbols"),
            Reference("Level: name or UniqueId from revit_query_levels"), Point);

        return new[]
        {
            new RevitCommandSpec("family.load", true, "Load a Revit family (.rfa) into the document from an absolute path under the repository's .bim/families folder or the Autodesk library " + RevitFamilySourceGuard.AutodeskContentRoot + "; links and files over 100 MiB are refused; a family already loaded is left unchanged unless overwrite is true; reads back the family, its category and its types with UniqueIds. Allowed only on enabled test copies.", load, BindFamilyLoad),
            new RevitCommandSpec("family_instance.place", true, "Place one non-hosted, level-based family instance (generic model, specialty equipment, furniture, lighting, plumbing, mechanical equipment, casework) of a loaded type at a plan point in millimetres on a level, offset_mm above it and rotated rotation_deg about the vertical; families that need a host (wall, ceiling, floor or face based) are refused. Allowed only on enabled test copies.", place, BindFamilyInstancePlace),
        };
    }

    // The content root as JSON string text (backslashes escaped).
    static string AutodeskContentRootJson => RevitFamilySourceGuard.AutodeskContentRoot.Replace("\\", "\\\\", StringComparison.Ordinal);

    static RevitCommandArgs BindFamilyLoad(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        if (RevitFamilySourceGuard.PathProblem(path) is { } problem) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.path", problem);
        var overwrite = args.TryGetProperty("overwrite", out var flag) && flag.GetBoolean();
        return new FamilyLoadArgs(path, RevitFamilySourceGuard.FamilyName(path), overwrite);
    }

    static RevitCommandArgs BindFamilyInstancePlace(JsonElement args)
    {
        var point = Coordinates(args, "point", "args.point");
        var offset = OptionalOffset(args, "offset_mm");
        var rotation = Angle.FromDegrees(0);
        if (args.TryGetProperty("rotation_deg", out var rotationElement))
        {
            var degrees = Number(rotationElement);
            rotation = RevitUnits.Degrees(degrees, "args.rotation_deg");
            if (Math.Abs(degrees) > RevitFamilyRules.MaxRotationDegrees) throw new RevitCommandException(RevitCommandCodes.AngleInvalid, "args.rotation_deg", "rotation beyond 360 degrees either way");
        }
        return new FamilyInstancePlaceArgs(args.GetProperty("category").GetString()!, args.GetProperty("symbol").GetString()!, args.GetProperty("level").GetString()!, ToPoint(point), offset, rotation);
    }
}
