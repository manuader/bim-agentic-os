using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// View image export and document save (chat-export-save), so the owner can review the model from a phone through
// Claude and save without touching the computer. Read-only view.export_image writes image files only under the
// repository's .bim/exports/<utc> folder and never changes the model (a requested visual style is set inside a
// transaction that is always rolled back); document.save is a write, gated like every write (copies-only and link
// guards, then the executor's document checks). Same envelope (protocol_version 1), schema subset, unit rules and
// guard as catalogue v1; earlier commands are unchanged. Vendor-free like every RevitCommands*.cs file; the add-in
// executor (CommandsExecutorExportSave.cs) consumes these types.

// View: name, "kind: name", "number - name" (sheets) or UniqueId. PixelSize is the longest side of the image in pixels.
// Format "png" or "jpg"; VisualStyle null keeps the view's own; OutputName null keeps the file name Revit gives.
public sealed record ViewExportImageArgs(string View, int PixelSize, string Format, string? VisualStyle, string? OutputName) : RevitCommandArgs;
// Save the open document to its own path; no save-as.
public sealed record DocumentSaveArgs : RevitCommandArgs;

public static class RevitExportRules
{
    public const int MinPixelSize = 256;
    public const int MaxPixelSize = 4096;
    public const int DefaultPixelSize = 1600;
    public const string DefaultFormat = "png";
    public const int MaxOutputNameChars = 100;
    // Relative to the repository root (RevitCommandPolicy.RepositoryRoot).
    public const string ExportsDirectory = ".bim/exports";
    public static readonly IReadOnlyList<string> Formats = new[] { "png", "jpg" };
    public static readonly IReadOnlyList<string> VisualStyles = new[] { "shaded", "realistic", "hidden_line", "consistent_colors" };

    static readonly Regex OutputName = new(@"\A[A-Za-z0-9][A-Za-z0-9 ._-]*\z", RegexOptions.CultureInvariant);
    static readonly Regex DeviceName = new(@"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // A plain file name without extension, usable on Windows: letters, digits, space, dot, underscore and hyphen,
    // starting with a letter or digit, not ending with a dot or space, not a reserved device name (before the first dot).
    public static void CheckOutputName(string name, string field)
    {
        if (name.Length == 0 || name.Length > MaxOutputNameChars || !OutputName.IsMatch(name) || name.EndsWith('.') || name.EndsWith(' ') || DeviceName.IsMatch(name.Split('.')[0]))
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "output_name is a file name without folder or extension: 1 to 100 letters, digits, spaces, dots, underscores or hyphens, starting with a letter or digit, not ending with a dot or space, not a reserved Windows device name");
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> ExportSaveSpecs = new(BuildExportSave);

    public static IReadOnlyList<RevitCommandSpec> ExportSave => ExportSaveSpecs.Value;

    // document.save overwrites the saved file (MCP destructiveHint).
    static bool ExportSaveDestructive(string command) => command == "document.save";

    static IReadOnlyList<RevitCommandSpec> BuildExportSave()
    {
        string Choices(IReadOnlyList<string> values) => string.Join(",", values.Select(value => "\"" + value + "\""));
        var export = Fill("""{"type":"object","properties":{"view":@0@,"pixel_size":{"type":"integer","description":"Longest side of the image in pixels, from @1@ to @2@; default @3@"},"format":{"type":"string","description":"Image format; default png","enum":[@4@]},"visual_style":{"type":"string","description":"Visual style for this export only: shaded, realistic, hidden_line or consistent_colors; set in a transaction that is rolled back after the export, so the view keeps its own; omit to keep the view's; not for sheets","enum":[@5@]},"output_name":@6@},"required":["view"],"additionalProperties":false}""",
            Reference("View or sheet: name, kind: name (for example floor_plan: Level 1), number - name for a sheet, or UniqueId; 3D views, plans, sections, elevations, detail views and sheets"),
            RevitExportRules.MinPixelSize.ToString(CultureInfo.InvariantCulture), RevitExportRules.MaxPixelSize.ToString(CultureInfo.InvariantCulture), RevitExportRules.DefaultPixelSize.ToString(CultureInfo.InvariantCulture),
            Choices(RevitExportRules.Formats), Choices(RevitExportRules.VisualStyles),
            Text("File name without folder or extension (letters, digits, space, . _ -); optional, default the name Revit gives", RevitExportRules.MaxOutputNameChars));
        return new[]
        {
            new RevitCommandSpec("view.export_image", false, "Export one view or sheet (3D, plan, section, elevation, detail or sheet) as a PNG or JPG image, fitted so its longest side is pixel_size pixels (256-4096, default 1600), into a new folder of the repository's .bim/exports (never anywhere else), "
                + "optionally in another visual style (shaded, realistic, hidden_line, consistent_colors) that is set only for the export and rolled back, so the model is not changed. Reads back the absolute file path, size in bytes, pixel width and height read from the image header, and sha256. Read-only: allowed on any document.", export, BindExportImage),
            new RevitCommandSpec("document.save", true, "Save the open document to its own path (Revit Save; no save-as), then read back the path, file size, last-write time and that Revit reports no unsaved changes. Refused for workshared, cloud, family and read-only documents, for a document never saved, and for one whose path is not the request's document path. "
                + "Allowed only on enabled test copies and owner-enabled project folders.", EmptySchema, _ => new DocumentSaveArgs()),
        };
    }

    static RevitCommandArgs BindExportImage(JsonElement args)
    {
        var pixelSize = RevitExportRules.DefaultPixelSize;
        if (args.TryGetProperty("pixel_size", out var size))
        {
            if (!size.TryGetInt64(out var value) || value < RevitExportRules.MinPixelSize || value > RevitExportRules.MaxPixelSize)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.pixel_size", "pixel_size is an integer from 256 to 4096");
            pixelSize = (int)value;
        }
        var outputName = args.TryGetProperty("output_name", out var name) ? name.GetString()! : null;
        if (outputName is not null) RevitExportRules.CheckOutputName(outputName, "args.output_name");
        return new ViewExportImageArgs(args.GetProperty("view").GetString()!, pixelSize,
            args.TryGetProperty("format", out var format) ? format.GetString()! : RevitExportRules.DefaultFormat,
            args.TryGetProperty("visual_style", out var style) ? style.GetString() : null, outputName);
    }
}
