using Bim.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Bim.Ifc;

// ADR0007 option A. Adapter protocol only: the worker's BimExecution receipt as read by `worker.py --receipt`.
// Core State, BIM-IR and the IFC receipt format stay unchanged.
public sealed record IfcOperationReceipt(double Width,string Revision,string? CompiledFingerprint,string? PlanAfterHash);
public sealed record IfcBackendState(string Revision,int? CompiledRevision,string? CompiledHash,Guid? CompiledProject,Dictionary<string,IfcOperationReceipt> Receipts);
public sealed record IfcBackendReceipt(IfcBackendState State,bool StagingArtifactPresent,string? StagingHash);
public sealed record IfcReconcileResult(bool Succeeded,string Code,int CoreRevision,int? BackendRevision,string CoreHash,string? BackendHash);

public static class IfcReconcile
{
    static readonly JsonSerializerOptions ReceiptOptions=new() { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    static string Revision(int revision)=>"r"+revision.ToString(CultureInfo.InvariantCulture);
    public static IfcBackendReceipt ParseReceipt(string json)
    {
        try { return JsonSerializer.Deserialize<IfcBackendReceipt>(json,ReceiptOptions) ?? throw new ArgumentException("invalid_backend_receipt"); }
        catch(JsonException error) { throw new ArgumentException("invalid_backend_receipt",error); }
    }
    // Same digest as compiled_request.validate_request's fingerprint.
    public static string Fingerprint(IfcCompiledRequest packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return IfcCompiler.Sha256(packet.BeforeJson+"\n"+packet.AfterJson+"\n"+packet.CommittedAfterJson+"\n"+packet.PlanJson);
    }
    // Read-only comparison of Core with the IFC receipt. The only write is the replay of the latest
    // one-revision gap through the unchanged ProjectStore.Commit; every other state fails closed.
    public static IfcReconcileResult Reconcile(ProjectStore store,IfcBackendReceipt receipt,IfcCompiledRequest packet)
    {
        ArgumentNullException.ThrowIfNull(store);
        var core=store.Read(); var coreHash=Compiler.Hash(core);
        IfcReconcileResult Fail(string code)=>new(false,code,core.Revision,receipt?.State?.CompiledRevision,coreHash,receipt?.State?.CompiledHash);
        if(receipt is null||receipt.State is null)return Fail("invalid_backend_receipt");
        if(receipt.StagingArtifactPresent)return Fail("staging_artifact_present");
        var backend=receipt.State;
        if(backend.Revision is null||backend.Receipts is null||backend.CompiledRevision is not int backendRevision||backend.CompiledHash is not string backendHash||backend.CompiledProject is not Guid backendProject||backend.Revision!=Revision(backendRevision))return Fail("invalid_backend_receipt");
        ExecutionPlan plan; Project after;
        try {
            if(packet is null)return Fail("invalid_compiled_packet");
            IfcCompiler.Validate(packet);
            plan=JsonSerializer.Deserialize<ExecutionPlan>(packet.PlanJson)!;
            after=JsonSerializer.Deserialize<Project>(packet.AfterJson)!;
        } catch(Exception error) when(error is ArgumentException or InvalidOperationException or JsonException or OverflowException) { return Fail("invalid_compiled_packet"); }
        if(backendProject!=core.Id||plan.ProjectId!=core.Id)return Fail("project_mismatch");
        var coreRevision=core.Revision;
        var latest=IfcCompiler.Sha256(packet.CommittedAfterJson)==backendHash;
        var receiptMatches=backend.Receipts.TryGetValue(packet.OperationId,out var operation)&&operation is not null&&operation.Revision==Revision(backendRevision)&&operation.CompiledFingerprint==Fingerprint(packet)&&operation.PlanAfterHash==plan.AfterHash;
        if(coreRevision>backendRevision)return Fail("core_ahead_of_backend");
        if((long)backendRevision-coreRevision>1)return Fail("backend_gap_gt_1");
        if(backendRevision==coreRevision) {
            if(backendHash!=coreHash)return Fail("divergent_state");
            if(latest)return receiptMatches?new(true,"in_sync",coreRevision,backendRevision,coreHash,backendHash):Fail("receipt_packet_mismatch");
            // A retained packet that neither store has applied yet: nothing to replay.
            if(plan.ExpectedRevision==coreRevision&&plan.BeforeHash==coreHash&&!backend.Receipts.ContainsKey(packet.OperationId))return new(true,"in_sync",coreRevision,backendRevision,coreHash,backendHash);
            return Fail(plan.ExpectedRevision<coreRevision-1?"retry_superseded":"receipt_packet_mismatch");
        }
        // Core is exactly one revision behind the IFC receipt.
        if(plan.ExpectedRevision<coreRevision)return Fail("retry_superseded");
        if(plan.ExpectedRevision!=coreRevision||plan.BeforeHash!=coreHash||!latest||!receiptMatches)return Fail("receipt_packet_mismatch");
        if(JsonSerializer.Serialize(after)!=packet.AfterJson)return Fail("noncanonical_packet");
        try { store.Commit(after,plan,packet.OperationId); }
        catch(InvalidOperationException) { return Fail("core_commit_rejected"); }
        var published=store.Read(); var publishedHash=Compiler.Hash(published);
        if(published.Revision!=backendRevision||publishedHash!=backendHash)return new(false,"core_commit_rejected",published.Revision,backendRevision,publishedHash,backendHash);
        return new(true,"replayed",published.Revision,backendRevision,publishedHash,backendHash);
    }
}
