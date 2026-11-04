using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// P2-05 query.journal Revit executor: the catalogue form of the raw pipe Operation journal.read (JournalRecorder.cs).
// Same tracker (JournalRecorder.Tracker: one journal, one baseline per document UniqueId and path), same fresh
// query.snapshot of the active document (JournalSnapshot, the query.snapshot collector and mapper) and the same reply
// body (core RevitJournalQuery.Body), returned as the query's readback. Admission (document path and UniqueId of the
// active document) already ran in CommandExecutor.Execute; queries never touch the receipt store and open no
// transaction, so the read changes nothing in the document. A read without peek moves the in-memory baseline, as
// journal.read does.
// Revit 2025 API used (https://www.revitapidocs.com/2025/): Document.PathName, and through JournalSnapshot the
// query.snapshot collector (Mirror.cs).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
internal static partial class CommandExecutor
{
    static string QueryJournal(Document d, JournalQueryArgs args) =>
        RevitJournalQuery.Read(JournalRecorder.Tracker, JournalRecorder.Attached, JournalSnapshot(d), d.PathName ?? "", args);
}
