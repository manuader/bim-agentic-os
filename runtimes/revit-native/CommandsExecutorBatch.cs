using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// P1-07 command.batch Revit executor. Same admission (copies-only guard on the batch's document, checked once by
// Execute before this runs) and receipt store as every catalogue write (CommandsExecutor.cs). Every decision that needs
// no Revit object (step validation, step envelopes, fresh step operation_ids, receipt store room, the step loop with its
// stop-and-roll-back rule, the survivor check, refusal and reply assembly) is in core RevitCommandsBatch.cs
// (RevitCommandBatch.Run, unit-tested with a fake runner). Here: one outer TransactionGroup; each step runs through
// the ordinary Dispatch path (its own nested transaction group, transaction, readback and step receipt, assimilated
// into the outer group); the first step that fails rolls the outer group back, which undoes every earlier step and its
// receipt; after the last step the batch receipt is persisted and the outer group is assimilated, so the whole batch is
// one undo entry. A replay of the batch operation_id is answered by Dispatch from the stored batch receipt before this
// runs. Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/): TransactionGroup(Document, string), Start, RollBack,
//   Assimilate, GetStatus (transaction groups may be nested; an assimilated inner group becomes part of the outer one);
//   Document.GetElement(string uniqueId) and Element.IsValidObject for the survivor check.
// Unverified in Revit (owner session required): that a StairsEditScope or SketchEditScope step starts inside two nested
//   groups as it does inside one; that Revit's undo list shows exactly one entry named after the outer group.
internal static partial class CommandExecutor
{
    static string BatchExecute(Document d, CommandRequest request, ReceiptStore store, CommandBatchArgs batch)
    {
        var envelope = request.Envelope;
        using var group = new TransactionGroup(d, "BIM Agentic OS " + envelope.Command + " " + envelope.OperationId);
        if (group.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction group did not start");
        try
        {
            return RevitCommandBatch.Run(envelope, request.RequestSha256, store.Receipts.ContainsKey, store.Receipts.Count, CommandReceiptStore.MaxReceipts,
                step => RevitCommandResponse.Parse(Dispatch(d, new CommandRequest(step, step.RequestSha256))),
                uid => d.GetElement(uid) is { IsValidObject: true },
                () => { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); },
                // The steps created or extended the receipt store: read it again so the batch receipt joins theirs.
                response => Persist(d, request, ReadReceipts(d), group, response));
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }
}
