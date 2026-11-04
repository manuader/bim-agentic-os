using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// View image export and document save (chat-export-save): executor decisions, the export folder and image files,
// readback checks and readback JSON. Autodesk-free on purpose: tests/revit-commands source-links this file and runs
// the folder and header code against a temporary repository. CommandsExecutorExportSave.cs reads the Revit facts and
// calls Document.ExportImage / Document.Save.

public readonly record struct ImageSize(int Width, int Height);
// One image written by an export: absolute path, size in bytes, pixel size from its header, lowercase hex SHA-256.
public sealed record ExportedFile(string Path, long Bytes, int Width, int Height, string Sha256);
// The exported view: Kind from CommandRulesExportSave.ExportKind.
public sealed record ExportedView(string UniqueId, string Name, string Kind);
// What the add-in reads from the active Document before saving.
public sealed record SaveFacts(string? PathName, bool IsReadOnly, bool IsWorkshared, bool IsModelInCloud, bool IsFamilyDocument);
// The saved file as the file system reports it after Document.Save, and Document.IsModified.
public sealed record ObservedSavedDocument(string Path, long Bytes, DateTime LastWriteUtc, bool IsModified);

public static class CommandRulesExportSave
{
    // Revit ViewType member name -> exported kind; null for views that are not exported (schedules, legends,
    // drafting views, browsers, walkthroughs, reports, ...).
    public static string? ExportKind(string revitViewType) => revitViewType switch
    {
        "ThreeD" => "3d",
        "FloorPlan" => "floor_plan",
        "CeilingPlan" => "ceiling_plan",
        "EngineeringPlan" => "structural_plan",
        "AreaPlan" => "area_plan",
        "Section" => "section",
        "Elevation" => "elevation",
        "Detail" => "detail",
        "DrawingSheet" => "sheet",
        _ => null,
    };

    public static void CheckExportView(string? kind, bool isTemplate, string revitViewType)
    {
        if (isTemplate) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view", "a view template cannot be exported; pick a view or sheet");
        if (kind is null) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view", "a " + revitViewType + " view cannot be exported; pick a 3D view, plan, section, elevation, detail view or sheet");
    }

    public static void CheckVisualStyle(string kind, string? style)
    {
        if (style is not null && kind == "sheet") throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.visual_style", "a sheet has no visual style; set it on the views placed on the sheet, or export one of those views");
    }

    // Request style -> Revit DisplayStyle member name.
    public static string RevitDisplayStyle(string style) => style switch
    {
        "shaded" => "Shading",
        "realistic" => "Realistic",
        "hidden_line" => "HLR",
        "consistent_colors" => "FlatColors",
        _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.visual_style", "unknown visual style " + style),
    };

    // Revit DisplayStyle member name -> snake case style as read back.
    public static string VisualStyleName(string revitDisplayStyle) => revitDisplayStyle switch
    {
        "Shading" => "shaded",
        "Realistic" => "realistic",
        "HLR" => "hidden_line",
        "FlatColors" => "consistent_colors",
        "ShadingWithEdges" => "shaded_with_edges",
        "RealisticWithEdges" => "realistic_with_edges",
        _ => Snake(revitDisplayStyle),
    };

    static string Snake(string name)
    {
        var text = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) text.Append('_');
            text.Append(char.ToLowerInvariant(name[i]));
        }
        return text.ToString();
    }

    // Revit FitDirectionType member name: the pixel size applies to the view's longest side (outline in paper space);
    // unknown or square outlines fit horizontally.
    public static string FitDirection(double? width, double? height) => width is { } w && height is { } h && h > w ? "Vertical" : "Horizontal";
    public static string OtherDirection(string direction) => direction == "Vertical" ? "Horizontal" : "Vertical";

    public const int PixelTolerance = 2;
    public static bool LongestSideMatches(ImageSize size, int pixelSize) => Math.Abs(Math.Max(size.Width, size.Height) - pixelSize) <= PixelTolerance;

    // Revit ImageFileType member name.
    public static string RevitImageFileType(string format) => format == "jpg" ? "JPEGLossless" : "PNG";
    public static string Extension(string format) => format == "jpg" ? ".jpg" : ".png";

    public static string ViewReferenceName(string kind, string name) => kind + ": " + name;
    public static string SheetReferenceName(string number, string name) => number + " - " + name;

    // document.save refusals, after the admission checks (repeated here because the executor saves what is open).
    public static void CheckSavable(SaveFacts facts, string envelopePath)
    {
        if (facts.PathName is not { Length: > 0 } path) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "the document has never been saved; save it once in Revit into an enabled folder");
        if (!CommandAdmission.SamePath(path, envelopePath)) throw new RevitCommandException(RevitCommandCodes.DocumentConflict, "document.path", "the open document is saved at a different path than the request names");
        if (facts.IsModelInCloud) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "cloud models are not saved by document.save");
        if (facts.IsWorkshared) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "workshared documents are not saved by document.save (synchronize with central in Revit)");
        if (facts.IsFamilyDocument) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "family documents are not saved by document.save");
        if (facts.IsReadOnly) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "the document is read-only");
    }
}

// The export folder: every image goes to a new <repository>/.bim/exports/<utc> folder and is collected from there
// only; nothing outside .bim/exports is created, renamed or deleted. Links on the way are refused.
public static class CommandExportFiles
{
    public const int MaxFolderAttempts = 99;
    public const int MaxFiles = 8;

    public static string ExportsRoot(string repositoryRoot) => Path.Combine(repositoryRoot, ".bim", "exports");

    static RevitCommandException Failed(string detail) => new(RevitCommandCodes.TransactionFailed, null, detail);

    static void RefuseLink(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw Failed("export folder " + path + " is a link; refusing to write through it");
    }

    static void CheckUnderExports(string repositoryRoot, string path)
    {
        if (!RevitCopiesGuard.IsUnder(path, ExportsRoot(repositoryRoot)) || CommandAdmission.SamePath(path, ExportsRoot(repositoryRoot)))
            throw Failed(path + " is not inside the repository's " + RevitExportRules.ExportsDirectory + " folder");
    }

    // A new, empty folder .bim/exports/<yyyyMMddTHHmmssfffZ>, with -2, -3, ... when that name exists.
    public static string CreateDirectory(string? repositoryRoot, DateTime utcNow)
    {
        if (repositoryRoot is null) throw Failed("no repository folder (global.json) above the add-in; images are written only under its " + RevitExportRules.ExportsDirectory);
        var bim = Path.Combine(repositoryRoot, ".bim");
        var exports = ExportsRoot(repositoryRoot);
        RefuseLink(bim);
        RefuseLink(exports);
        Directory.CreateDirectory(exports);
        RefuseLink(exports);
        var stem = utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        for (var attempt = 1; attempt <= MaxFolderAttempts; attempt++)
        {
            var path = Path.Combine(exports, attempt == 1 ? stem : stem + "-" + attempt.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            RefuseLink(path);
            CheckUnderExports(repositoryRoot, path);
            return path;
        }
        throw Failed("too many exports in the same millisecond");
    }

    static bool Matches(string file, string format)
    {
        var extension = Path.GetExtension(file);
        return format == "jpg"
            ? extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            : extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    // The images Revit wrote into directory (one export folder), renamed to outputName + extension when there is
    // exactly one, each read back with its header size and SHA-256.
    public static IReadOnlyList<ExportedFile> Collect(string repositoryRoot, string directory, string format, string? outputName)
    {
        CheckUnderExports(repositoryRoot, directory);
        RefuseLink(directory);
        var files = Directory.GetFiles(directory).Where(file => Matches(file, format)).OrderBy(file => file, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw Failed("Revit wrote no " + format + " image into " + directory);
        if (files.Length > MaxFiles) throw Failed("Revit wrote " + files.Length + " images; at most " + MaxFiles + " expected");
        if (outputName is not null && files.Length == 1)
        {
            var target = Path.Combine(directory, outputName + CommandRulesExportSave.Extension(format));
            if (!string.Equals(files[0], target, StringComparison.Ordinal))
            {
                CheckUnderExports(repositoryRoot, target);
                File.Move(files[0], target);
                files[0] = target;
            }
        }
        var exported = new List<ExportedFile>();
        foreach (var file in files)
        {
            CheckUnderExports(repositoryRoot, file);
            var info = new FileInfo(file);
            if (info.LinkTarget is not null) throw Failed(file + " is a link");
            var bytes = File.ReadAllBytes(file);
            var size = ImageHeader.Read(bytes, format) ?? throw Failed(Path.GetFileName(file) + " is not a readable " + format + " image");
            exported.Add(new ExportedFile(Path.GetFullPath(file), bytes.LongLength, size.Width, size.Height, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        return exported;
    }

    // Removes one export folder (a retried attempt); refuses anything outside .bim/exports.
    public static void Discard(string repositoryRoot, string directory)
    {
        CheckUnderExports(repositoryRoot, directory);
        RefuseLink(directory);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

// Pixel size from the file header: PNG IHDR, JPEG start-of-frame (SOF0-SOF15 except DHT, JPG, DAC).
public static class ImageHeader
{
    static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static ImageSize? Read(byte[] bytes, string format) => format == "jpg" ? Jpeg(bytes) : Png(bytes);

    static ImageSize? Valid(long width, long height) => width > 0 && height > 0 && width <= int.MaxValue && height <= int.MaxValue ? new ImageSize((int)width, (int)height) : null;

    public static ImageSize? Png(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature) || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R') return null;
        static long BigEndian(byte[] b, int at) => ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3];
        return Valid(BigEndian(bytes, 16), BigEndian(bytes, 20));
    }

    public static ImageSize? Jpeg(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        var at = 2;
        while (at + 3 < bytes.Length)
        {
            if (bytes[at] != 0xFF) return null;
            var marker = bytes[at + 1];
            if (marker == 0xFF) { at++; continue; }
            // Markers without a length.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { at += 2; continue; }
            if (marker is 0xD9 or 0xDA) return null;
            var length = (bytes[at + 2] << 8) | bytes[at + 3];
            if (length < 2) return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (at + 8 >= bytes.Length) return null;
                return Valid((bytes[at + 7] << 8) | bytes[at + 8], (bytes[at + 5] << 8) | bytes[at + 6]);
            }
            at += 2 + length;
        }
        return null;
    }
}

public static class CommandReadbackExportSave
{
    // Null when every file is a non-empty image under .bim/exports whose longest side is the pixel size.
    public static string? Export(int pixelSize, IReadOnlyList<ExportedFile> files, string exportsRoot)
    {
        if (files.Count == 0) return "no image was exported";
        foreach (var file in files)
        {
            if (!RevitCopiesGuard.IsUnder(file.Path, exportsRoot)) return file.Path + " is outside " + RevitExportRules.ExportsDirectory;
            if (file.Bytes <= 0) return file.Path + " is empty";
            if (!CommandRulesExportSave.LongestSideMatches(new ImageSize(file.Width, file.Height), pixelSize))
                return "image is " + file.Width + " x " + file.Height + " px, expected the longest side " + pixelSize + " px";
        }
        return null;
    }

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

    // {export: {view: {unique_id, name, kind}, format, pixel_size, visual_style, view_visual_style, fit_direction,
    // directory, model_changed: false, files: [{path, bytes, width, height, sha256}]}}
    public static string ExportJson(ExportedView view, string format, int pixelSize, string? visualStyle, string? viewVisualStyle, string fitDirection, string directory, IReadOnlyList<ExportedFile> files) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("export");
        writer.WriteStartObject("view");
        writer.WriteString("unique_id", view.UniqueId);
        writer.WriteString("name", view.Name);
        writer.WriteString("kind", view.Kind);
        writer.WriteEndObject();
        writer.WriteString("format", format);
        writer.WriteNumber("pixel_size", pixelSize);
        Text(writer, "visual_style", visualStyle);
        Text(writer, "view_visual_style", viewVisualStyle);
        writer.WriteString("fit_direction", fitDirection.ToLowerInvariant());
        writer.WriteString("directory", directory);
        writer.WriteBoolean("model_changed", false);
        writer.WriteStartArray("files");
        foreach (var file in files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteNumber("bytes", file.Bytes);
            writer.WriteNumber("width", file.Width);
            writer.WriteNumber("height", file.Height);
            writer.WriteString("sha256", file.Sha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(2);

    // Null when the document was saved where the request says, the file exists and is not empty, Revit reports no
    // unsaved changes and, when there were changes, the file was rewritten after the save started.
    public static string? Saved(string envelopePath, bool hadUnsavedChanges, DateTime startedUtc, ObservedSavedDocument observed)
    {
        if (!CommandAdmission.SamePath(observed.Path, envelopePath)) return "document is now at " + observed.Path + ", expected " + envelopePath;
        if (observed.Bytes <= 0) return "the saved file is missing or empty";
        if (observed.IsModified) return "Revit still reports unsaved changes";
        if (hadUnsavedChanges && observed.LastWriteUtc < startedUtc - ClockTolerance) return "the file was not rewritten (last write " + observed.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture) + ")";
        return null;
    }

    // {document: {path, bytes, last_write_utc, is_modified, had_unsaved_changes}}
    public static string SaveJson(ObservedSavedDocument observed, bool hadUnsavedChanges) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("document");
        writer.WriteString("path", observed.Path);
        writer.WriteNumber("bytes", observed.Bytes);
        writer.WriteString("last_write_utc", DateTime.SpecifyKind(observed.LastWriteUtc.ToUniversalTime(), DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture));
        writer.WriteBoolean("is_modified", observed.IsModified);
        writer.WriteBoolean("had_unsaved_changes", hadUnsavedChanges);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
