using Bim.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Bim.Ifc;

// ADR0007 option A. Adapter protocol only: the worker's BimExecution receipt as read by `worker.py --receipt`.
// Core State, BIM-IR and the IFC receipt format stay unchanged.
public sealed record IfcOperationReceipt(double Width,string Revision,string? CompiledFingerprint,string? PlanAfterHash);
public sealed record IfcBackendState(string Revision,int? CompiledRevision,string? CompiledHash,Guid? CompiledProject,Dictionary<string,IfcOperationReceipt> Receipts);
// CloudHazard/CloudHazardEntries: the worker's OneDrive conflict-copy or placeholder finding next to room.ifc; State is then null (never read).
public sealed record IfcBackendReceipt(IfcBackendState State,bool StagingArtifactPresent,string? StagingHash,string? CloudHazard=null,string[]? CloudHazardEntries=null);
// CoreRevision/CoreHash are null when Core was deliberately not read (cloud hazard in either store, or Core directory unreadable).
public sealed record IfcReconcileResult(bool Succeeded,string Code,int? CoreRevision,int? BackendRevision,string? CoreHash,string? BackendHash);
public sealed record IfcCloudHazard(string Code,string[] Entries);

public static class IfcReconcile
{
    static readonly JsonSerializerOptions ReceiptOptions=new() { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    static string Revision(int revision)=>"r"+revision.ToString(CultureInfo.InvariantCulture);
    // Forwarders to the shared Bim.Core CloudGuard; kept so existing callers and tests stay unchanged.
    public const string CloudConflictPresent=CloudGuard.ConflictPresent;
    public const string CloudPlaceholderPresent=CloudGuard.PlaceholderPresent;
    public static IfcBackendReceipt ParseReceipt(string json)
    {
        try {
            // Fail closed on a receipt that does not state its cloud check (for example from an older worker).
            using(var document=JsonDocument.Parse(json)) {
                if(document.RootElement.ValueKind!=JsonValueKind.Object||!document.RootElement.TryGetProperty("cloud_hazard",out _)||!document.RootElement.TryGetProperty("cloud_hazard_entries",out _))throw new ArgumentException("invalid_backend_receipt");
            }
            return JsonSerializer.Deserialize<IfcBackendReceipt>(json,ReceiptOptions) ?? throw new ArgumentException("invalid_backend_receipt");
        }
        catch(JsonException error) { throw new ArgumentException("invalid_backend_receipt",error); }
    }
    public static bool IsConflictCopy(string name,string primary)=>CloudGuard.IsConflictCopy(name,primary);
    public static IfcCloudHazard? CloudHazard(string directory,IReadOnlyCollection<string> primaries,Func<string,FileAttributes>? readAttributes=null)
        =>CloudGuard.Scan(directory,primaries,readAttributes) is {} finding?new(finding.Code,finding.Entries):null;
    // Directory-aware entry point: refuses before Core is read when project.json has a conflict copy or any Core file is cloud-only.
    public static IfcReconcileResult Reconcile(string coreDirectory,IfcBackendReceipt receipt,IfcCompiledRequest packet,Func<string,FileAttributes>? readAttributes=null)
    {
        ArgumentNullException.ThrowIfNull(coreDirectory);
        var hazard=ReceiptHazard(receipt);
        if(hazard is not null)return hazard;
        IfcReconcileResult? Scan() {
            IfcCloudHazard? core;
            try { core=CloudHazard(coreDirectory,["project.json"],readAttributes); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException) { return Unread("core_io_error",receipt); }
            return core is null?null:Unread(core.Code,receipt);
        }
        // Scanned before Core is read and again immediately before the replay Commit: this narrows the window, it does not close it.
        return Scan() ?? Reconcile(new ProjectStore(coreDirectory),receipt,packet,Scan);
    }
    static IfcReconcileResult Unread(string code,IfcBackendReceipt? receipt)=>new(false,code,null,receipt?.State?.CompiledRevision,null,receipt?.State?.CompiledHash);
    static IfcReconcileResult? ReceiptHazard(IfcBackendReceipt? receipt)
    {
        if(receipt is null)return null;
        if(receipt.CloudHazard is null)return receipt.CloudHazardEntries is {Length:>0}?Unread("invalid_backend_receipt",receipt):null;
        var known=string.Equals(receipt.CloudHazard,CloudConflictPresent,StringComparison.Ordinal)||string.Equals(receipt.CloudHazard,CloudPlaceholderPresent,StringComparison.Ordinal);
        return Unread(known?receipt.CloudHazard:"invalid_backend_receipt",receipt);
    }
    public static IfcCompiledRequest ParsePacket(string json)
    {
        try { return JsonSerializer.Deserialize<IfcCompiledRequest>(json) ?? throw new ArgumentException("invalid_compiled_packet"); }
        catch(JsonException error) { throw new ArgumentException("invalid_compiled_packet",error); }
    }
    // Same digest as compiled_request.validate_request's fingerprint.
    public static string Fingerprint(IfcCompiledRequest packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return IfcCompiler.Sha256(packet.BeforeJson+"\n"+packet.AfterJson+"\n"+packet.CommittedAfterJson+"\n"+packet.PlanJson);
    }
    // Read-only comparison of Core with the IFC receipt. The only write is the replay of the latest
    // one-revision gap through the unchanged ProjectStore.Commit; every other state fails closed.
    // This overload honours the worker's room.ifc cloud finding; the Core directory scan comes from Reconcile(coreDirectory,...) via beforeCommit.
    // Internal so callers cannot skip the Core directory guard; tests reach it through InternalsVisibleTo.
    internal static IfcReconcileResult Reconcile(ProjectStore store,IfcBackendReceipt receipt,IfcCompiledRequest packet,Func<IfcReconcileResult?>? beforeCommit=null)
    {
        ArgumentNullException.ThrowIfNull(store);
        // The worker found a conflict copy or placeholder next to room.ifc: refuse before reading Core.
        var hazard=ReceiptHazard(receipt);
        if(hazard is not null)return hazard;
        var core=store.Read(); var coreHash=Compiler.Hash(core);
        IfcReconcileResult Fail(string code)=>new(false,code,core.Revision,receipt?.State?.CompiledRevision,coreHash,receipt?.State?.CompiledHash);
        if(receipt is null||receipt.State is null)return Fail("invalid_backend_receipt");
        if(receipt.StagingArtifactPresent)return Fail("staging_artifact_present");
        var backend=receipt.State;
        if(backend.Revision is null||backend.Receipts is null||backend.CompiledRevision is not int backendRevision||backend.CompiledHash is not string backendHash||backend.CompiledProject is not Guid backendProject||backend.Revision!=Revision(backendRevision))return Fail("invalid_backend_receipt");
        if(packet is null)return Fail("invalid_compiled_packet");
        ExecutionPlan plan; Project after;
        try {
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
        var late=beforeCommit?.Invoke();
        if(late is not null)return late;
        try { store.Commit(after,plan,packet.OperationId); }
        catch(InvalidOperationException) { return Fail("core_commit_rejected"); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { return Fail("core_io_error"); }
        Project published;
        // A failed read-back after Commit cannot prove what was published: fail closed as unverified.
        try { published=store.Read(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or NullReferenceException) { return Fail("core_commit_unverified"); }
        if(published is null)return Fail("core_commit_unverified");
        var publishedHash=Compiler.Hash(published);
        if(published.Revision!=backendRevision||publishedHash!=backendHash)return new(false,"core_commit_unverified",published.Revision,backendRevision,publishedHash,backendHash);
        return new(true,"replayed",published.Revision,backendRevision,publishedHash,backendHash);
    }
}
