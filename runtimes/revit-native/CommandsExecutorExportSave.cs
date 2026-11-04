using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// View image export and document save (chat-export-save) Revit executor: read-only view.export_image and the write
// document.save. Every decision that needs no Revit object, the export folder and the image header reading are in
// CommandsExportSave.cs (unit-tested).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   ImageExportOptions (ExportRange = ExportRange.SetOfViews, SetViewsAndSheets(IList<ElementId>), ZoomType =
//     ZoomFitType.FitToPage, PixelSize, FitDirection = FitDirectionType.Horizontal / Vertical, ImageResolution,
//     HLRandWFViewsFileType / ShadowViewsFileType = ImageFileType.PNG / JPEGLossless, FilePath), Document.ExportImage
//   View.ViewType / IsTemplate / Outline (BoundingBoxUV in paper space) / DisplayStyle, ViewSheet.SheetNumber
//   Document.Save(), Document.PathName / IsModified / IsReadOnly / IsWorkshared / IsModelInCloud / IsFamilyDocument
// view.export_image never changes the model: a requested visual style is set inside a transaction that is ALWAYS
// rolled back after Document.ExportImage, and no receipt is stored (it is a query). Images go only to a new
// <repository>/.bim/exports/<utc> folder (repository found from the add-in's location, as for every repo-relative
// path) and are collected only from there.
// document.save runs after the admission write checks (copies-only and link guards, read-only, family, workshared,
// open transaction) and CheckSavable; it stores no receipt, because recording one would be a transaction that leaves
// the document modified again after the save. Repeating the same operation_id saves again, which is harmless.
// Unverified in Revit (owner session required): that Document.ExportImage runs inside an open transaction and
// renders the temporarily set DisplayStyle; that rolling that transaction back leaves IsModified as it was and adds
// nothing to the undo list; that the Revit file name for a set of one view lands in the given folder (only that folder
// is searched); that FitToPage with PixelSize gives the longest side for the direction picked from View.Outline (a
// wrong first guess is retried once in the other direction); which file name Revit gives a sheet; that Document.Save
// rewrites the file when IsModified is true, leaves IsModified false, and whether it writes at all when there are no
// changes (then the last-write time is not checked); that Save shows no dialog for a file upgraded from an older
// release.
internal static partial class CommandExecutor
{
    // ---- view.export_image (read-only) ----
    static string ExportImage(Document d, ViewExportImageArgs args)
    {
        var view = ResolveExportView(d, args.View);
        var kind = CommandRulesExportSave.ExportKind(view.ViewType.ToString())!;
        CommandRulesExportSave.CheckVisualStyle(kind, args.VisualStyle);
        var repository = RepositoryOfAddIn();
        var exportsRoot = repository is null ? null : CommandExportFiles.ExportsRoot(repository);
        var viewStyle = kind == "sheet" ? null : CommandRulesExportSave.VisualStyleName(view.DisplayStyle.ToString());
        var direction = CommandRulesExportSave.FitDirection(OutlineWidth(view), OutlineHeight(view));

        var (directory, files) = ExportOnce(d, view, args, repository, direction);
        if (files.Any(file => !CommandRulesExportSave.LongestSideMatches(new ImageSize(file.Width, file.Height), args.PixelSize)))
        {
            // The outline guess fitted the shorter side: export once more with the other direction, drop the first try.
            CommandExportFiles.Discard(repository!, directory);
            direction = CommandRulesExportSave.OtherDirection(direction);
            (directory, files) = ExportOnce(d, view, args, repository, direction);
        }
        if (CommandReadbackExportSave.Export(args.PixelSize, files, exportsRoot!) is { } mismatch)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "export readback mismatch: " + mismatch);
        var name = view is ViewSheet sheet ? CommandRulesExportSave.SheetReferenceName(sheet.SheetNumber, sheet.Name) : view.Name;
        return CommandReadbackExportSave.ExportJson(new ExportedView(view.UniqueId, name, kind), args.Format, args.PixelSize, args.VisualStyle, viewStyle, direction, directory, files);
    }

    static string? RepositoryOfAddIn()
    {
        var directory = Path.GetDirectoryName(typeof(CommandExecutor).Assembly.Location);
        return string.IsNullOrEmpty(directory) ? null : RevitCommandPolicy.RepositoryRoot(directory);
    }

    static View ResolveExportView(Document d, string reference)
    {
        var views = AllViews(d).Where(view => !view.IsTemplate && CommandRulesExportSave.ExportKind(view.ViewType.ToString()) is not null).ToArray();
        // A UniqueId of a template or a schedule gets the precise refusal instead of "not found".
        if (RevitCommandProtocol.IsUniqueId(reference) && d.GetElement(reference) is View named && !views.Contains(named))
            CommandRulesExportSave.CheckExportView(CommandRulesExportSave.ExportKind(named.ViewType.ToString()), named.IsTemplate, named.ViewType.ToString());
        return Resolve(views, view => new CommandCandidate(view.UniqueId, view.Name, view is ViewSheet sheet
            ? CommandRulesExportSave.SheetReferenceName(sheet.SheetNumber, sheet.Name)
            : CommandRulesExportSave.ViewReferenceName(CommandRulesExportSave.ExportKind(view.ViewType.ToString())!, view.Name)), reference, "args.view", "view or sheet");
    }

    static double? OutlineWidth(View view) { try { var outline = view.Outline; return outline is null ? null : outline.Max.U - outline.Min.U; } catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; } }
    static double? OutlineHeight(View view) { try { var outline = view.Outline; return outline is null ? null : outline.Max.V - outline.Min.V; } catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; } }

    static (string Directory, IReadOnlyList<ExportedFile> Files) ExportOnce(Document d, View view, ViewExportImageArgs args, string? repository, string direction)
    {
        var directory = CommandExportFiles.CreateDirectory(repository, DateTime.UtcNow);
        var fileType = args.Format == "jpg" ? ImageFileType.JPEGLossless : ImageFileType.PNG;
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = args.PixelSize,
            FitDirection = direction == "Vertical" ? FitDirectionType.Vertical : FitDirectionType.Horizontal,
            ImageResolution = ImageResolution.DPI_150,
            HLRandWFViewsFileType = fileType,
            ShadowViewsFileType = fileType,
            FilePath = Path.Combine(directory, args.OutputName ?? "view"),
        };
        options.SetViewsAndSheets(new List<ElementId> { view.Id });
        if (args.VisualStyle is null) d.ExportImage(options);
        else ExportWithStyle(d, view, args.VisualStyle, options);
        return (directory, CommandExportFiles.Collect(repository!, directory, args.Format, args.OutputName));
    }

    static DisplayStyle RevitStyle(string style) => style switch
    {
        "shaded" => DisplayStyle.Shading,
        "realistic" => DisplayStyle.Realistic,
        "hidden_line" => DisplayStyle.HLR,
        "consistent_colors" => DisplayStyle.FlatColors,
        _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.visual_style", "unknown visual style " + style),
    };

    // The style is set only for the export: the transaction is always rolled back, so the model is unchanged.
    static void ExportWithStyle(Document d, View view, string style, ImageExportOptions options)
    {
        var failures = new CommandFailures();
        using var tx = new Transaction(d, "BIM Agentic OS view.export_image visual style");
        if (tx.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.visual_style", "the visual style cannot be changed in this document (read-only?); export without visual_style");
        try
        {
            var handling = tx.GetFailureHandlingOptions();
            handling.SetFailuresPreprocessor(failures);
            handling.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(handling);
            var wanted = RevitStyle(style);
            try { view.DisplayStyle = wanted; }
            catch (Autodesk.Revit.Exceptions.ApplicationException error)
            {
                throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.visual_style", "Revit refused the visual style (a view template may control it): " + error.Message);
            }
            d.Regenerate();
            if (view.DisplayStyle != wanted)
                throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.visual_style", "the view's template controls its visual style; export without visual_style or change the template in Revit");
            d.ExportImage(options);
        }
        finally
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
        }
    }

    // ---- document.save (write, no receipt) ----
    static string SaveExecute(Document d, CommandRequest request)
    {
        var envelope = request.Envelope;
        CommandRulesExportSave.CheckSavable(new SaveFacts(d.PathName, d.IsReadOnly, d.IsWorkshared, d.IsModelInCloud, d.IsFamilyDocument), envelope.Document.Path);
        var hadUnsavedChanges = d.IsModified;
        var started = DateTime.UtcNow;
        try { d.Save(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException error)
        {
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "Revit did not save the document: " + error.Message);
        }
        var file = new FileInfo(d.PathName);
        file.Refresh();
        var observed = new ObservedSavedDocument(d.PathName, file.Exists ? file.Length : 0, file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue, d.IsModified);
        if (CommandReadbackExportSave.Saved(envelope.Document.Path, hadUnsavedChanges, started, observed) is { } mismatch)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "Revit saved, but the readback differs (the save cannot be undone): " + mismatch);
        return CommandResponses.Success(request, Array.Empty<string>(), Array.Empty<string>(), CommandReadbackExportSave.SaveJson(observed, hadUnsavedChanges), Array.Empty<string>()).ToJson();
    }
}
