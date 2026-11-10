using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// P9-08 document.save_as Revit executor: Document.SaveAs of the open document to a new .rvt inside an enabled root
// (.bim/native-runs or a folder listed in .bim/revit-enabled-roots.txt), never overwriting. Every decision that needs
// no Revit object (target path shape, enabled root, links, existence, enabled source, document state, readback, JSON)
// is in core RevitCommandsSaveAs.cs (unit-tested). Evidence level: compiled against the Revit 2025 reference
// assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Document.SaveAs(string, SaveAsOptions), SaveAsOptions.OverwriteExistingFile = false,
//   Document.PathName / IsModified / IsReadOnly / IsWorkshared / IsModelInCloud / IsFamilyDocument
// Runs after the admission write checks (identity, copies-only and link guards, read-only, family, workshared, open
// transaction), then RevitSaveAsRules.CheckSavable and RevitSaveAsGuard.Check again on Document.PathName with the
// add-in's own repository policy (any same-user process can write to the pipe). Like document.save and
// document.save_copy it stores no receipt (recording one would be a transaction that leaves the new file modified
// right after the save); repeating the same operation_id is refused target_exists, never a second write.
// Revit's SaveAs re-points the open document: afterwards Document.PathName is the new file, the title changes, the
// ProjectInformation UniqueId (the envelope document identity) stays, unsaved changes are written to the new file only
// and the previous file keeps its last saved state. The readback requires exactly that, and the reply reports both
// paths and warns that later requests must name the new one. After SaveAs the target is checked again
// (RevitSaveAsGuard.WrittenProblem: full path; final path with every parent folder's symlink or junction resolved from
// the volume root down, compared with the matched enabled root resolved the same way; the link guard re-walked from
// that root) because a folder could be swapped for a junction between the check and the save. This is DETECTION, not
// prevention: Revit has already written the file when the check runs. A failure is transaction_failed saying the
// document was written outside the enabled roots, naming where, and telling the caller to close it in Revit and not
// continue (the open document now IS that file). The SHA-256 is read with FileShare.ReadWrite | Delete
// because Revit keeps the saved file open.
// Unverified in Revit (owner session required): the same SaveAs behaviour document.save_copy relies on (PathName
// re-pointed, IsModified cleared, previous file untouched, no dialog for an upgraded file, OverwriteExistingFile =
// false throwing rather than prompting); whether relative links (Revit links, CAD imports by relative path) keep
// resolving after the document moves to another folder.
internal static partial class CommandExecutor
{
    // ---- document.save_as (write, no receipt) ----
    static string SaveAsExecute(Document d, CommandRequest request, DocumentSaveAsArgs args)
    {
        var envelope = request.Envelope;
        RevitSaveAsRules.CheckSavable(new SaveCopyFacts(d.PathName, d.IsReadOnly, d.IsWorkshared, d.IsModelInCloud, d.IsFamilyDocument), envelope.Document.Path);
        var previous = d.PathName;
        var policy = Policy();
        if (RevitSaveAsGuard.Check(args.Path, previous, policy, RevitFileSystemProbe.Real, RevitSaveCopyProbe.Real) is { } refused)
            throw refused.ToException();
        var hadUnsavedChanges = d.IsModified;
        var previousBefore = FileState(previous);
        try { d.SaveAs(args.Path, new SaveAsOptions { OverwriteExistingFile = false }); }
        catch (Autodesk.Revit.Exceptions.ApplicationException error)
        {
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.path", "Revit did not save the new file: " + error.Message);
        }
        // A folder could have been swapped for a junction between the check and the save: detect where the file landed.
        if (RevitSaveAsGuard.WrittenProblem(args.Path, policy, RevitFileSystemProbe.Real) is { } elsewhere)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.path", RevitSaveAsRules.WrittenElsewhereDetail(elsewhere, d.PathName));
        var written = FileState(args.Path);
        string? sha = null;
        try { if (written.Exists) sha = RevitSaveCopyGuard.Sha256File(args.Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { sha = null; }
        var observed = new ObservedSaveCopy(d.PathName, d.IsModified, written, sha, previousBefore, FileState(previous));
        if (RevitSaveAsRules.Readback(args.Path, observed) is { } mismatch)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "Revit saved the new file, but the readback differs (the open document may now be the new file): " + mismatch);
        return CommandResponses.Success(request, Array.Empty<string>(), Array.Empty<string>(), RevitSaveAsRules.Json(previous, observed, hadUnsavedChanges),
            new[] { RevitSaveAsRules.DocumentMovedWarning(observed.DocumentPath, previous) }).ToJson();
    }
}
