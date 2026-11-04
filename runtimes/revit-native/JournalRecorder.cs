using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Bim.Core;
namespace Bim.Revit;

// P2-03 external edit recorder. Subscribes to Application.DocumentChanged only in opted-in fixture sessions, after
// AddInGate.PipeAllowed validated the config (App.Expose; App.Withdraw and OnShutdown unsubscribe), and records, per event, the added and modified elements as UniqueIds, the deleted element ids (a
// deleted element can no longer be resolved to a UniqueId), the transaction names and the undo operation into the
// Revit-free core journal (core/Bim.Core/MirrorJournal.cs). It never writes to the document: no transaction, no
// receipt, nothing persisted. Changes raised while App.Execute runs a request, or committed under the add-in's own
// transaction names, are the add-in's and are skipped (counted) by the journal; undo and redo are always external.
//
// "journal.read" (pipe Operation, outside the revit.command catalogue) takes a fresh query.snapshot of the active
// document (UniqueId and path) and answers the journal entries since the previous journal.read of that document together
// with the snapshot diff against it, then makes the new snapshot the baseline. Read-only. The optional Request.Journal
// member {"after_sequence":n,"peek":true,"max_entries":m} pages through capped replies and re-reads a lost reply; peek
// leaves the baseline in place (MirrorJournalReadOptions).
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/): ControlledApplication.DocumentChanged,
//   DocumentChangedEventArgs.GetDocument / GetAddedElementIds / GetModifiedElementIds / GetDeletedElementIds /
//   GetTransactionNames / Operation (UndoOperation), ElementId.Value, Document.GetElement, Element.UniqueId,
//   Document.ProjectInformation, Document.IsFamilyDocument.
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
// Unverified in Revit (owner session required): which UndoOperation values Revit raises for undo/redo and for a
// rolled-back add-in transaction group, and whether a modified id list includes dependent elements (joins, rooms).
internal static class JournalRecorder
{
    public const string ReadOperation = "journal.read";
    // Shared with the P2-05 catalogue query query.journal (CommandsExecutorJournal.cs): one journal, one set of baselines.
    internal static readonly MirrorJournalTracker Tracker = new(new MirrorJournal(() => DateTimeOffset.UtcNow));
    // > 0 while App.Execute runs a request. DocumentChanged fires synchronously on Revit's main thread, the same
    // thread that runs the ExternalEvent handler, so a plain counter is enough.
    static int dispatching;
    static bool attached;
    internal static bool Attached => attached;

    public static void Attach(ControlledApplication application)
    {
        if (attached) return;
        application.DocumentChanged += OnDocumentChanged;
        attached = true;
    }

    public static void Detach(ControlledApplication application)
    {
        if (!attached) return;
        application.DocumentChanged -= OnDocumentChanged;
        attached = false;
    }

    // Runs one add-in request with the dispatch marker set, so its own changes are classified as add-in.
    public static T Dispatching<T>(Func<T> run)
    {
        dispatching++;
        try { return run(); }
        finally { dispatching--; }
    }

    static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var d = e.GetDocument();
            if (d is null || d.IsFamilyDocument) return;
            var documentUid = d.ProjectInformation?.UniqueId;
            if (string.IsNullOrWhiteSpace(documentUid)) { Tracker.Journal.RecordUnidentified(); return; }
            var added = e.GetAddedElementIds();
            var modified = e.GetModifiedElementIds();
            var deleted = e.GetDeletedElementIds();
            Tracker.Journal.Record(new MirrorJournalChange(documentUid, e.Operation.ToString(), e.GetTransactionNames()?.ToArray() ?? Array.Empty<string>(),
                Uids(d, added), Uids(d, modified), deleted.Take(MirrorJournal.MaxIdsPerList).Select(id => id.Value).ToArray(), dispatching > 0)
            {
                // True totals: ids beyond the cap (and any id that no longer resolves) make the entry truncated.
                AddedTotal = added.Count,
                ModifiedTotal = modified.Count,
                DeletedTotal = deleted.Count,
                DocumentPath = d.PathName ?? "",
            });
        }
        // Never throw into Revit's event dispatch; the fault is counted and reported by journal.read.
        catch (Exception) { Tracker.Journal.RecordFailure(); }
    }

    // At most MaxIdsPerList GetElement calls per list, so a huge paste cannot stall the event.
    static string[] Uids(Document d, ICollection<ElementId> ids) =>
        ids.Take(MirrorJournal.MaxIdsPerList).Select(id => d.GetElement(id)?.UniqueId).OfType<string>().Where(uid => uid.Length > 0).ToArray();

    public static string Read(UIApplication app, Request r)
    {
        var d = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("no_document");
        if (string.IsNullOrWhiteSpace(r.DocumentPath) || !string.Equals(d.PathName, r.DocumentPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("document_conflict");
        var options = MirrorJournalReadOptions.Parse(r.Journal is { } journalOptions ? journalOptions.GetRawText() : null);
        // The same serializer as query.journal (core RevitJournalQuery): the reply bytes are unchanged.
        return RevitJournalQuery.RawRead(Tracker, attached, CommandExecutor.JournalSnapshot(d), d.PathName ?? "", options);
    }
}

internal static partial class CommandExecutor
{
    // The same collector and mapper as query.snapshot (Mirror.cs), for journal.read.
    internal static MirrorSnapshot JournalSnapshot(Document d)
    {
        var uid = d.ProjectInformation?.UniqueId ?? throw new InvalidOperationException("document_unique_id_required");
        var spec = RevitCommandCatalogue.Find("query.snapshot") ?? throw new InvalidOperationException("snapshot_command_missing");
        var envelope = new RevitCommandEnvelope(RevitCommandProtocol.Version, JournalRecorder.ReadOperation, new RevitDocumentRef(d.PathName, uid), spec.Name, spec, new EmptyQueryArgs(), "{}");
        return MirrorSnapshotMapper.Map(MirrorObserve(d, envelope));
    }
}
