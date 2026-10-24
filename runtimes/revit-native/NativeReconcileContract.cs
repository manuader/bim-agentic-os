using Bim.Core;
using System.Text.Json;
namespace Bim.Revit;

// ADR0007 option A, Revit path. Autodesk-free on purpose: guard tests source-link this file without Revit DLLs.
// Input is the ExtensibleStorage Payload that BatchExecutor.Run persisted {Project (revision+1), Map, Receipts},
// read from a document that was saved, closed and reopened (FixtureCheckpoint.Run's verify_reopened_storage).
// SavedAndReopened is the caller's attestation of that; a receipt that only ever lived in memory (C8) is not evidence.
public sealed record NativeSavedState(string DocumentIdentity,string ProjectJson,int Revision,Dictionary<string,string> Receipts,bool SavedAndReopened);
// CoreRevision/CoreHash are null when Core was deliberately not read (unsaved evidence) or could not be read.
public sealed record NativeReconcileResult(bool Succeeded,string Code,int? CoreRevision,int? NativeRevision,string? CoreHash,string? NativeHash);
// Replay is set only for the one decision that writes: Core exactly one revision behind with matching evidence.
public sealed record NativeReconcileReplay(Project Desired,ExecutionPlan Plan,string Key);
public sealed record NativeReconcileDecision(NativeReconcileResult Result,NativeReconcileReplay? Replay);

public static class NativeReconcileContract
{
    public const string NotSavedAndReopened="native_not_saved_and_reopened";
    // Keeps the exact saved Project text, so the hash check never runs on a re-serialized copy.
    public static NativeSavedState ParsePayload(string payload,string documentIdentity,bool savedAndReopened)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(documentIdentity);
        try {
            using var document=JsonDocument.Parse(payload);
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Project",out var project)||project.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Receipts",out var receipts)||receipts.ValueKind!=JsonValueKind.Object||!project.TryGetProperty("Revision",out var revision)||!revision.TryGetInt32(out var value))throw new ArgumentException("invalid_native_state");
            var parsed=receipts.Deserialize<Dictionary<string,string>>() ?? throw new ArgumentException("invalid_native_state");
            if(parsed.Values.Any(hash=>hash is null))throw new ArgumentException("invalid_native_state");
            return new(documentIdentity,project.GetRawText(),value,parsed,savedAndReopened);
        } catch(JsonException error) { throw new ArgumentException("invalid_native_state",error); }
    }
    // Pure decision: reads nothing, writes nothing.
    public static NativeReconcileDecision Decide(Project core,NativeSavedState native,NativeBatchRequest packet)
    {
        ArgumentNullException.ThrowIfNull(core); ArgumentNullException.ThrowIfNull(native);
        var coreHash=Compiler.Hash(core);
        NativeReconcileDecision Fail(string code,int? nativeRevision=null,string? nativeHash=null)=>new(new(false,code,core.Revision,nativeRevision,coreHash,nativeHash),null);
        if(!native.SavedAndReopened)return Fail(NotSavedAndReopened);
        Project? saved;
        try { saved=JsonSerializer.Deserialize<Project>(native.ProjectJson); }
        catch(JsonException) { return Fail("invalid_native_state"); }
        if(saved is null||saved.Elements is null||native.Receipts is null||saved.Revision!=native.Revision)return Fail("invalid_native_state");
        if(JsonSerializer.Serialize(saved)!=native.ProjectJson)return Fail("noncanonical_native_snapshot");
        var nativeRevision=saved.Revision; var nativeHash=Compiler.Hash(saved);
        NativeReconcileDecision Refuse(string code)=>Fail(code,nativeRevision,nativeHash);
        if(packet is null||string.IsNullOrWhiteSpace(packet.IdempotencyKey)||packet.Before?.Elements is null||packet.After?.Elements is null||packet.Plan?.Operations is null)return Refuse("invalid_retained_packet");
        try { if(NativeBatchContract.Preflight(packet.Before,packet.After,packet.Plan).Length!=0)return Refuse("invalid_retained_packet"); }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException) { return Refuse("invalid_retained_packet"); }
        if(!string.Equals(native.DocumentIdentity,packet.DocumentIdentity,StringComparison.Ordinal))return Refuse("document_identity_mismatch");
        if(saved.Id!=core.Id||packet.Plan.ProjectId!=core.Id)return Refuse("project_mismatch");
        var plan=packet.Plan; var coreRevision=core.Revision;
        // latest: the saved snapshot is exactly the packet's committed result (After at revision+1).
        var latest=Compiler.Hash(packet.After with {Revision=packet.After.Revision+1})==nativeHash;
        var receiptMatches=native.Receipts.TryGetValue(packet.IdempotencyKey,out var receipt)&&receipt==plan.AfterHash&&nativeRevision==plan.ExpectedRevision+1;
        if(coreRevision>nativeRevision)return Refuse("core_ahead_of_backend");
        if((long)nativeRevision-coreRevision>1)return Refuse("backend_gap_gt_1");
        if(nativeRevision==coreRevision) {
            if(nativeHash!=coreHash)return Refuse("divergent_state");
            if(latest)return receiptMatches?new(new(true,"in_sync",coreRevision,nativeRevision,coreHash,nativeHash),null):Refuse("receipt_packet_mismatch");
            // A retained packet that neither store has applied yet: nothing to replay.
            if(plan.ExpectedRevision==coreRevision&&plan.BeforeHash==coreHash&&!native.Receipts.ContainsKey(packet.IdempotencyKey))return new(new(true,"in_sync",coreRevision,nativeRevision,coreHash,nativeHash),null);
            return Refuse(plan.ExpectedRevision<coreRevision-1?"retry_superseded":"receipt_packet_mismatch");
        }
        // Core is exactly one revision behind the saved native state.
        if(plan.ExpectedRevision<coreRevision)return Refuse("retry_superseded");
        if(plan.ExpectedRevision!=coreRevision||plan.BeforeHash!=coreHash||!latest||!receiptMatches)return Refuse("receipt_packet_mismatch");
        // ADR0007: desired = native Project at Revision-1, plan = Diff(core, desired), which must equal the retained plan and the receipt.
        var desired=saved with {Revision=coreRevision};
        ExecutionPlan derived;
        try { derived=Compiler.Diff(core,desired); }
        catch(InvalidOperationException) { return Refuse("receipt_packet_mismatch"); }
        if(JsonSerializer.Serialize(derived)!=JsonSerializer.Serialize(plan)||derived.AfterHash!=receipt)return Refuse("receipt_packet_mismatch");
        return new(new(true,"replay_required",coreRevision,nativeRevision,coreHash,nativeHash),new(desired,derived,packet.IdempotencyKey));
    }
    // The only write is the replay through the unchanged ProjectStore.Commit; every other outcome leaves Core untouched.
    // Unsaved evidence is refused before Core is read. A Core directory cloud scan (IfcReconcile.CloudHazard) is the caller's job.
    public static NativeReconcileResult Reconcile(ProjectStore store,NativeSavedState native,NativeBatchRequest packet)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(native);
        if(!native.SavedAndReopened)return new(false,NotSavedAndReopened,null,null,null,null);
        Project core;
        try { core=store.Read(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { return new(false,"core_io_error",null,null,null,null); }
        catch(JsonException) { return new(false,"invalid_core_state",null,null,null,null); }
        if(core is null)return new(false,"invalid_core_state",null,null,null,null);
        var decision=Decide(core,native,packet);
        if(decision.Replay is not {} replay)return decision.Result;
        var result=decision.Result;
        NativeReconcileResult Fail(string code)=>result with {Succeeded=false,Code=code};
        try { store.Commit(replay.Desired,replay.Plan,replay.Key); }
        catch(InvalidOperationException) { return Fail("core_commit_rejected"); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { return Fail("core_io_error"); }
        var published=store.Read(); var publishedHash=Compiler.Hash(published);
        if(published.Revision!=result.NativeRevision||publishedHash!=result.NativeHash)return new(false,"core_commit_unverified",published.Revision,result.NativeRevision,publishedHash,result.NativeHash);
        return new(true,"replayed",published.Revision,result.NativeRevision,publishedHash,result.NativeHash);
    }
}
