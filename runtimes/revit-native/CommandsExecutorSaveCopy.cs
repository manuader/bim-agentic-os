using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// P1-08 document.save_copy Revit executor: Document.SaveAs of the open test copy to a new BIM-Agentic-Test*.rvt under
// <repository>/.bim/native-runs, never overwriting. Every decision that needs no Revit object (target folder, name,
// links, existence, test-copy source, document state, readback, JSON) is in core RevitCommandsSaveCopy.cs (unit-tested).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Document.SaveAs(string, SaveAsOptions), SaveAsOptions.OverwriteExistingFile = false,
//   Document.PathName / IsModified / IsReadOnly / IsWorkshared / IsModelInCloud / IsFamilyDocument
// Runs after the admission write checks (identity, copies-only and link guards, read-only, family, workshared, open
// transaction), then RevitSaveCopyRules.CheckCopyable and RevitSaveCopyGuard.Check again on Document.PathName with the
// add-in's own repository policy (any same-user process can write to the pipe). Like document.save it stores no
// receipt: recording one would be a transaction that leaves the copy modified right after the save. Repeating the same
// operation_id is refused (target_exists), never a second write.
// Revit's SaveAs re-points the open document: afterwards Document.PathName is the copy, the title changes, the
// ProjectInformation UniqueId (the envelope document identity) stays, unsaved changes are written to the copy only and
// the source file keeps its last saved state. The readback requires exactly that, and the reply warns that later
// requests must name the copy's path. There is no API to write a copy and keep the open document on the source.
// Unverified in Revit (owner session required): that SaveAs re-points PathName and clears IsModified as documented
// for the UI Save As; that it leaves the source file's size and last-write time untouched; that it shows no dialog
// for a file upgraded from an older release; that OverwriteExistingFile = false throws (rather than prompts) when a
// file appears at the target between the check and the save.
internal static partial class CommandExecutor
{
    // ---- document.save_copy (write, no receipt) ----
    static string SaveCopyExecute(Document d, CommandRequest request, DocumentSaveCopyArgs args)
    {
        var envelope = request.Envelope;
        RevitSaveCopyRules.CheckCopyable(new SaveCopyFacts(d.PathName, d.IsReadOnly, d.IsWorkshared, d.IsModelInCloud, d.IsFamilyDocument), envelope.Document.Path);
        var source = d.PathName;
        if (RevitSaveCopyGuard.Check(args.Path, source, Policy().RepositoryDirectory, RevitFileSystemProbe.Real, RevitSaveCopyProbe.Real) is { } refused)
            throw refused.ToException();
        var hadUnsavedChanges = d.IsModified;
        var sourceBefore = FileState(source);
        try { d.SaveAs(args.Path, new SaveAsOptions { OverwriteExistingFile = false }); }
        catch (Autodesk.Revit.Exceptions.ApplicationException error)
        {
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.path", "Revit did not save the copy: " + error.Message);
        }
        var copy = FileState(args.Path);
        string? sha = null;
        try { if (copy.Exists) sha = RevitSaveCopyGuard.Sha256File(args.Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { sha = null; }
        var observed = new ObservedSaveCopy(d.PathName, d.IsModified, copy, sha, sourceBefore, FileState(source));
        if (RevitSaveCopyRules.Readback(args.Path, observed) is { } mismatch)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "Revit saved the copy, but the readback differs (the open document may now be the copy): " + mismatch);
        return CommandResponses.Success(request, Array.Empty<string>(), Array.Empty<string>(), RevitSaveCopyRules.Json(source, observed, hadUnsavedChanges),
            new[] { RevitSaveCopyRules.DocumentNowCopyWarning(observed.DocumentPath, source) }).ToJson();
    }

    static SaveCopyFileState FileState(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists ? new SaveCopyFileState(true, file.Length, file.LastWriteTimeUtc) : new SaveCopyFileState(false, 0, DateTime.MinValue);
    }
}
