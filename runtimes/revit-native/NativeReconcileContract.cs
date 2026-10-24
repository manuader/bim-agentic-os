using Bim.Core;
using System.Text.Json;
namespace Bim.Revit;

// ADR0007 option A, Revit path. Autodesk-free on purpose: guard tests source-link this file without Revit DLLs.
// Input is the ExtensibleStorage Payload that BatchExecutor.Run persisted {Project (revision+1), Map, Receipts},
// read from a document that was saved, closed and reopened (FixtureCheckpoint.Run's verify_reopened_storage).
// SavedAndReopened is the caller's attestation of that; a receipt that only ever lived in memory (C8) is not evidence.
// Set SavedAndReopened only from a successful BatchExecutor.VerifyReceipt on the freshly reopened document, never from Run's in-memory result.
// Map is the persisted logical/native map; it must cover the snapshot exactly (BatchReceiptGuard's rule).
public sealed record NativeSavedState(string DocumentIdentity,string ProjectJson,int Revision,Dictionary<string,string> Receipts,Dictionary<Guid,string> Map,bool SavedAndReopened);
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
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Project",out var project)||project.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Receipts",out var receipts)||receipts.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("Map",out var map)||map.ValueKind!=JsonValueKind.Object||!project.TryGetProperty("Revision",out var revision)||revision.ValueKind!=JsonValueKind.Number||!revision.TryGetInt32(out var value))throw new ArgumentException("invalid_native_state");
            var parsed=receipts.Deserialize<Dictionary<string,string>>() ?? throw new ArgumentException("invalid_native_state");
            var mapping=map.Deserialize<Dictionary<Guid,string>>() ?? throw new ArgumentException("invalid_native_state");
            if(parsed.Values.Any(hash=>hash is null))throw new ArgumentException("invalid_native_state");
            return new(documentIdentity,project.GetRawText(),value,parsed,mapping,savedAndReopened);
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
        if(saved is null||saved.Elements is null||saved.Elements.Any(e=>e?.GeometryMetres is null||e.Dependencies is null||e.Kind is null)||native.Receipts is null||native.Map is null||saved.Revision!=native.Revision)return Fail("invalid_native_state");
        if(JsonSerializer.Serialize(saved)!=native.ProjectJson)return Fail("noncanonical_native_snapshot");
        var nativeRevision=saved.Revision; var nativeHash=Compiler.Hash(saved);
        NativeReconcileDecision Refuse(string code)=>Fail(code,nativeRevision,nativeHash);
        var map=native.Map;
        if(map.Count!=saved.Elements.Length||saved.Elements.Any(e=>!map.ContainsKey(e.Id))||map.Values.Any(string.IsNullOrWhiteSpace)||map.Values.Distinct(StringComparer.Ordinal).Count()!=map.Count)return Refuse("native_mapping_conflict");
        if(packet is null||string.IsNullOrWhiteSpace(packet.IdempotencyKey)||packet.Before?.Elements is null||packet.After?.Elements is null||packet.Plan?.Operations is null)return Refuse("invalid_retained_packet");
        try { if(NativeBatchContract.Preflight(packet.Before,packet.After,packet.Plan).Length!=0)return Refuse("invalid_retained_packet"); }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException or NullReferenceException) { return Refuse("invalid_retained_packet"); }
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
    // Directory-aware entry point: refuses unsaved evidence before touching the disk, then refuses before Core is read
    // when project.json has a OneDrive conflict copy or any Core file is cloud-only, and scans again just before the
    // replay Commit. The re-scan narrows the window between check and write; it does not close it.
    public static NativeReconcileResult Reconcile(string coreDirectory,NativeSavedState native,NativeBatchRequest packet,Func<string,FileAttributes>? readAttributes=null)
    {
        ArgumentNullException.ThrowIfNull(coreDirectory); ArgumentNullException.ThrowIfNull(native);
        if(!native.SavedAndReopened)return Unread(NotSavedAndReopened);
        NativeReconcileResult? Scan() {
            CloudHazardFinding? hazard;
            try { hazard=CloudGuard.Scan(coreDirectory,["project.json"],readAttributes); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException) { return Unread("core_io_error"); }
            return hazard is null?null:Unread(hazard.Code);
        }
        return Scan() ?? Reconcile(new ProjectStore(coreDirectory),native,packet,Scan);
    }
    static NativeReconcileResult Unread(string code)=>new(false,code,null,null,null,null);
    // The only write is the replay through the unchanged ProjectStore.Commit; every other outcome leaves project.json untouched.
    // Internal so callers cannot skip the Core directory guard; the source-linked guard tests reach it directly.
    internal static NativeReconcileResult Reconcile(ProjectStore store,NativeSavedState native,NativeBatchRequest packet,Func<NativeReconcileResult?>? beforeCommit=null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(native);
        if(!native.SavedAndReopened)return Unread(NotSavedAndReopened);
        Project core;
        try { core=store.Read(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { return Unread("core_io_error"); }
        // ProjectStore.Read dereferences a null state ("null" in project.json) before returning.
        catch(Exception error) when(error is JsonException or NullReferenceException) { return Unread("invalid_core_state"); }
        if(core?.Elements is null)return Unread("invalid_core_state");
        var decision=Decide(core,native,packet);
        if(decision.Replay is not {} replay)return decision.Result;
        var result=decision.Result;
        NativeReconcileResult Fail(string code)=>result with {Succeeded=false,Code=code};
        var late=beforeCommit?.Invoke();
        if(late is not null)return late;
        try { store.Commit(replay.Desired,replay.Plan,replay.Key); }
        catch(InvalidOperationException) { return Fail("core_commit_rejected"); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { return Fail("core_io_error"); }
        Project published;
        // A failed read-back after Commit cannot prove what was published: fail closed as unverified.
        try { published=store.Read(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or NullReferenceException) { return Fail("core_commit_unverified"); }
        if(published is null)return Fail("core_commit_unverified");
        var publishedHash=Compiler.Hash(published);
        if(published.Revision!=result.NativeRevision||publishedHash!=result.NativeHash)return new(false,"core_commit_unverified",published.Revision,result.NativeRevision,publishedHash,result.NativeHash);
        return new(true,"replayed",published.Revision,result.NativeRevision,publishedHash,result.NativeHash);
    }
}
