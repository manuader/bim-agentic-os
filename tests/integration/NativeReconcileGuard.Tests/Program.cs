using Bim.Core;
using Bim.Revit;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
// ADR0007 option A, Revit path (test plan item 7, C8/C9), Autodesk-free decision contract only.
// The native state here mirrors the ExtensibleStorage Payload that BatchExecutor.Run persists:
// {Project (revision+1), Map, Receipts}. No Revit process is involved; native execution stays owner-gated.
var root=Path.Combine(Path.GetTempPath(),"native-reconcile-guards-"+Guid.NewGuid());
Directory.CreateDirectory(root);
var count=0;
try {
    void Check(bool condition,string name) { if(!condition) throw new InvalidOperationException("FAIL "+name); Console.WriteLine("PASS "+name); count++; }
    string FileHash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    string[] Snapshot(string directory)=>Directory.Exists(directory)?Directory.GetFiles(directory).OrderBy(file=>file,StringComparer.Ordinal).Select(file=>Path.GetFileName(file)+":"+FileHash(file)).ToArray():[];
    string CoreReceipt(string directory,string key)=>JsonNode.Parse(File.ReadAllText(Path.Combine(directory,"project.json")))?["Receipts"]?[key]?.GetValue<string>() ?? "missing";

    const string Identity="fixture-uid";
    var projectId=Guid.Parse("4f1c2a9e-7d1b-4c55-9a3e-2b8f0d6c1e77");
    var levelId=Guid.Parse("11111111-1111-1111-1111-111111111111");
    var wallId=Guid.Parse("22222222-2222-2222-2222-222222222222");
    var provenance=new Provenance("native reconcile guard","approved");
    Project Model(int revision,double length)=>new(projectId,revision,[new(levelId,"level",[0],[],provenance),new(wallId,"wall",[0,0,length,0,3,.2],[levelId],provenance)]);
    NativeBatchRequest Packet(Project before,Project after,string key)=>new(Identity,key,before,after,Compiler.Diff(before,after));
    Project Committed(NativeBatchRequest packet)=>packet.After with {Revision=packet.After.Revision+1};
    var map=new Dictionary<Guid,string>{{levelId,"native-level"},{wallId,"native-wall"}};
    // Same shape and property order as BatchExecutor.State(Project, Map, Receipts).
    string Payload(Project project,Dictionary<string,string> receipts)=>JsonSerializer.Serialize(new {Project=project,Map=map,Receipts=receipts});
    NativeSavedState Saved(Project project,Dictionary<string,string> receipts,bool savedAndReopened=true,string identity=Identity)=>NativeReconcileContract.ParsePayload(Payload(project,receipts),identity,savedAndReopened);

    var r1=Model(1,5); var packet1=Packet(r1,Model(1,5.4),"resize-400");
    var r2=Committed(packet1); var packet2=Packet(r2,Model(2,5.8),"resize-next");
    var r3=Committed(packet2);
    var receipts1=new Dictionary<string,string>{{packet1.IdempotencyKey,packet1.Plan.AfterHash}};
    var receipts2=new Dictionary<string,string>(receipts1){{packet2.IdempotencyKey,packet2.Plan.AfterHash}};

    ProjectStore Core(string name,params NativeBatchRequest[] applied)=>CoreFrom(name,r1,applied);
    ProjectStore CoreFrom(string name,Project initial,params NativeBatchRequest[] applied) {
        var store=new ProjectStore(Path.Combine(root,name)); store.Initialize(initial);
        foreach(var packet in applied) store.Commit(packet.After,packet.Plan,packet.IdempotencyKey);
        return store;
    }
    void ExpectFailure(string name,string directory,ProjectStore store,NativeSavedState native,NativeBatchRequest packet,string code) {
        var before=Snapshot(directory); var nativeReceipts=native.Receipts.Count;
        var result=NativeReconcileContract.Reconcile(store,native,packet);
        Check(!result.Succeeded&&result.Code==code,name+" returns "+code+" (got "+result.Code+")");
        Check(before.SequenceEqual(Snapshot(directory)),name+" leaves every Core file byte-identical (zero writes)");
        Check(native.Receipts.Count==nativeReceipts,name+" does not mutate the native evidence");
    }

    // Payload parsing: the exact saved ExtensibleStorage text is the evidence, never a re-serialized copy.
    var parsed=Saved(r2,receipts1);
    Check(parsed.ProjectJson==JsonSerializer.Serialize(r2)&&parsed.Revision==2&&parsed.Receipts[packet1.IdempotencyKey]==packet1.Plan.AfterHash&&parsed.SavedAndReopened&&parsed.DocumentIdentity==Identity,"payload parses project snapshot JSON, revision and receipts");
    foreach(var (bad,label) in new[]{("[]","array"),("{}","empty object"),("not json","garbage"),(JsonSerializer.Serialize(new {Project=r2,Map=map}),"missing receipts"),(JsonSerializer.Serialize(new {Map=map,Receipts=receipts1}),"missing project"),(JsonSerializer.Serialize(new {Project=r2,Map=map,Receipts=new Dictionary<string,int>{{"k",1}}}),"non-string receipt")}) {
        try { NativeReconcileContract.ParsePayload(bad,Identity,true); Check(false,"malformed payload rejected: "+label); }
        catch(ArgumentException error) { Check(error.Message=="invalid_native_state","malformed payload rejected: "+label+" (got "+error.Message+")"); }
    }

    // in_sync: both stores at r2 with the latest packet.
    var syncDirectory=Path.Combine(root,"in-sync"); var sync=Core("in-sync",packet1);
    var before=Snapshot(syncDirectory);
    var inSync=NativeReconcileContract.Reconcile(sync,Saved(r2,receipts1),packet1);
    Check(inSync.Succeeded&&inSync.Code=="in_sync"&&inSync.CoreRevision==2&&inSync.NativeRevision==2&&inSync.CoreHash==inSync.NativeHash&&inSync.NativeHash==Compiler.Hash(r2),"in_sync when Core and saved native snapshot agree");
    Check(before.SequenceEqual(Snapshot(syncDirectory)),"in_sync writes nothing");
    // A retained packet that neither store has applied yet: nothing to replay.
    var pending=Core("pending");
    var pendingResult=NativeReconcileContract.Reconcile(pending,Saved(r1,new()),packet1);
    Check(pendingResult.Succeeded&&pendingResult.Code=="in_sync"&&pendingResult.CoreRevision==1,"in_sync for an unapplied retained packet");

    // C9: native saved and reopened at r2 with its receipt, Core still at r1 -> replay exactly once.
    var replayDirectory=Path.Combine(root,"replay"); var replay=Core("replay");
    var replayed=NativeReconcileContract.Reconcile(replay,Saved(r2,receipts1),packet1);
    Check(replayed.Succeeded&&replayed.Code=="replayed"&&replayed.CoreRevision==2&&replayed.NativeRevision==2,"one-behind replays the saved native receipt (got "+replayed.Code+")");
    Check(Compiler.Hash(replay.Read())==Compiler.Hash(r2)&&replayed.CoreHash==Compiler.Hash(r2),"replayed Core hash equals Hash(nativeState.Project)");
    Check(CoreReceipt(replayDirectory,packet1.IdempotencyKey)==packet1.Plan.AfterHash,"replayed Core receipt equals the native receipt hash");
    var afterReplay=Snapshot(replayDirectory);
    var again=NativeReconcileContract.Reconcile(replay,Saved(r2,receipts1),packet1);
    Check(again.Succeeded&&again.Code=="in_sync"&&afterReplay.SequenceEqual(Snapshot(replayDirectory)),"second reconcile after replay is a byte-identical no-op");
    // Latest retry after recovery goes through the unchanged Commit and returns the stored project.
    Check(Compiler.Hash(replay.Commit(packet1.After,packet1.Plan,packet1.IdempotencyKey))==Compiler.Hash(r2)&&afterReplay.SequenceEqual(Snapshot(replayDirectory)),"latest Core retry after replay is idempotent and writes nothing");

    // C8: the receipt existed only in memory (no Save + reopen) -> not evidence, even when everything else would replay.
    var unsavedDirectory=Path.Combine(root,"unsaved"); var unsaved=Core("unsaved");
    ExpectFailure("unsaved document",unsavedDirectory,unsaved,Saved(r2,receipts1,savedAndReopened:false),packet1,"native_not_saved_and_reopened");
    var unread=NativeReconcileContract.Reconcile(new ProjectStore(Path.Combine(root,"never-created")),Saved(r2,receipts1,savedAndReopened:false),packet1);
    Check(unread.Code=="native_not_saved_and_reopened"&&unread.CoreRevision is null&&unread.CoreHash is null&&!Directory.Exists(Path.Combine(root,"never-created")),"unsaved evidence is refused before Core is read");

    // Core ahead of the saved native state.
    var aheadDirectory=Path.Combine(root,"ahead"); var ahead=Core("ahead",packet1);
    ExpectFailure("Core ahead",aheadDirectory,ahead,Saved(r1,new()),packet1,"core_ahead_of_backend");

    // Gap greater than one revision.
    var gapDirectory=Path.Combine(root,"gap"); var gap=Core("gap");
    ExpectFailure("gap > 1 latest packet",gapDirectory,gap,Saved(r3,receipts2),packet2,"backend_gap_gt_1");
    ExpectFailure("gap > 1 older packet",gapDirectory,gap,Saved(r3,receipts2),packet1,"backend_gap_gt_1");

    // Hash mismatches between the saved native state and the retained packet.
    var hashDirectory=Path.Combine(root,"hash"); var hash=Core("hash");
    ExpectFailure("native receipt hash mismatch",hashDirectory,hash,Saved(r2,new(){{packet1.IdempotencyKey,"forged"}}),packet1,"receipt_packet_mismatch");
    ExpectFailure("native receipt missing",hashDirectory,hash,Saved(r2,new()),packet1,"receipt_packet_mismatch");
    ExpectFailure("native snapshot hash mismatch",hashDirectory,hash,Saved(Model(2,5.6),receipts1),packet1,"receipt_packet_mismatch");
    ExpectFailure("retained packet for another correction",hashDirectory,hash,Saved(r2,receipts1),Packet(r1,Model(1,5.6),"resize-400"),"receipt_packet_mismatch");
    var divergentDirectory=Path.Combine(root,"divergent"); var divergent=Core("divergent",packet1);
    ExpectFailure("equal revisions divergent hash",divergentDirectory,divergent,Saved(Model(2,5.6),receipts1),packet1,"divergent_state");

    // Historical packet after a later correction applied to both stores, or with Core one behind.
    var historyDirectory=Path.Combine(root,"history"); var history=Core("history",packet1,packet2);
    ExpectFailure("historical packet, both stores at r3",historyDirectory,history,Saved(r3,receipts2),packet1,"retry_superseded");
    var historyBehindDirectory=Path.Combine(root,"history-behind"); var historyBehind=Core("history-behind",packet1);
    ExpectFailure("historical packet, Core one behind",historyBehindDirectory,historyBehind,Saved(r3,receipts2),packet1,"retry_superseded");

    // Identity, project and packet integrity.
    var guardDirectory=Path.Combine(root,"guards"); var guard=Core("guards");
    ExpectFailure("other document",guardDirectory,guard,Saved(r2,receipts1,identity:"other-uid"),packet1,"document_identity_mismatch");
    var foreign=r2 with {Id=Guid.NewGuid()};
    ExpectFailure("other project",guardDirectory,guard,Saved(foreign,receipts1),packet1,"project_mismatch");
    ExpectFailure("tampered retained plan",guardDirectory,guard,Saved(r2,receipts1),packet1 with {Plan=packet1.Plan with {Operations=[]}},"invalid_retained_packet");
    ExpectFailure("blank idempotency key",guardDirectory,guard,Saved(r2,receipts1),packet1 with {IdempotencyKey=" "},"invalid_retained_packet");
    ExpectFailure("revision disagrees with snapshot",guardDirectory,guard,Saved(r2,receipts1) with {Revision=3},packet1,"invalid_native_state");
    ExpectFailure("noncanonical snapshot JSON",guardDirectory,guard,Saved(r2,receipts1) with {ProjectJson=" "+JsonSerializer.Serialize(r2)},packet1,"noncanonical_native_snapshot");
    ExpectFailure("unparseable snapshot JSON",guardDirectory,guard,Saved(r2,receipts1) with {ProjectJson="{"},packet1,"invalid_native_state");
    var missingCore=NativeReconcileContract.Reconcile(new ProjectStore(Path.Combine(root,"no-core")),Saved(r2,receipts1),packet1);
    Check(!missingCore.Succeeded&&missingCore.Code=="core_io_error"&&!Directory.Exists(Path.Combine(root,"no-core")),"unreadable Core fails closed core_io_error without creating it");

    // Review follow-up: payload revision type, Core state shape, mapping integrity and narrower hash cases.
    foreach(var (value,label) in new (JsonNode?,string)[]{(JsonValue.Create("2"),"string"),(new JsonObject(),"object"),(null,"null"),(JsonValue.Create(true),"bool")}) {
        var node=JsonNode.Parse(Payload(r2,receipts1))!; node["Project"]!["Revision"]=value;
        try { NativeReconcileContract.ParsePayload(node.ToJsonString(),Identity,true); Check(false,"non-numeric Revision rejected: "+label); }
        catch(ArgumentException error) { Check(error.Message=="invalid_native_state","non-numeric Revision rejected: "+label+" (got "+error.Message+")"); }
    }
    try { NativeReconcileContract.ParsePayload(JsonSerializer.Serialize(new {Project=r2,Receipts=receipts1}),Identity,true); Check(false,"payload without Map rejected"); }
    catch(ArgumentException error) { Check(error.Message=="invalid_native_state","payload without Map rejected (got "+error.Message+")"); }
    Check(Saved(r2,receipts1).Map.Count==2&&Saved(r2,receipts1).Map[wallId]=="native-wall","payload parses the logical/native map");
    foreach(var (content,label) in new[]{("{","truncated"),("null","null state"),("{\"Project\":null,\"Receipts\":{}}","null project")}) {
        var badDirectory=Path.Combine(root,"bad-core-"+label.Replace(' ','-'));
        Directory.CreateDirectory(badDirectory); File.WriteAllText(Path.Combine(badDirectory,"project.json"),content);
        ExpectFailure("Core project.json "+label,badDirectory,new ProjectStore(badDirectory),Saved(r2,receipts1),packet1,"invalid_core_state");
    }
    var bodyDirectory=Path.Combine(root,"other-r1"); var otherBody=CoreFrom("other-r1",Model(1,4.9));
    ExpectFailure("one-behind with a different r1 body",bodyDirectory,otherBody,Saved(r2,receipts1),packet1,"receipt_packet_mismatch");
    var missingReceiptDirectory=Path.Combine(root,"equal-no-receipt"); var equalNoReceipt=Core("equal-no-receipt",packet1);
    ExpectFailure("equal revision, native receipt missing",missingReceiptDirectory,equalNoReceipt,Saved(r2,new()),packet1,"receipt_packet_mismatch");
    var mapDirectory=Path.Combine(root,"map"); var mapStore=Core("map");
    foreach(var (bad,label) in new[]{(new Dictionary<Guid,string>{{levelId,"native-level"}},"element missing"),(new Dictionary<Guid,string>{{levelId,"native-level"},{wallId," "}},"blank native id"),(new Dictionary<Guid,string>{{levelId,"same"},{wallId,"same"}},"duplicate native id"),(new Dictionary<Guid,string>{{levelId,"native-level"},{Guid.NewGuid(),"native-wall"}},"foreign logical id"),(new Dictionary<Guid,string>{{levelId,"native-level"},{wallId,"native-wall"},{Guid.NewGuid(),"extra"}},"extra entry")})
        ExpectFailure("incomplete map: "+label,mapDirectory,mapStore,Saved(r2,receipts1) with {Map=bad},packet1,"native_mapping_conflict");

    // Commit-time I/O failure: another holder of the Core lock file. project.json must stay unchanged.
    var lockedDirectory=Path.Combine(root,"locked"); var locked=Core("locked");
    var lockedBefore=Snapshot(lockedDirectory); NativeReconcileResult lockedResult;
    using(new FileStream(Path.Combine(lockedDirectory,"lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) lockedResult=NativeReconcileContract.Reconcile(locked,Saved(r2,receipts1),packet1);
    Check(!lockedResult.Succeeded&&lockedResult.Code=="core_io_error"&&lockedResult.CoreRevision==1,"held Core lock fails closed core_io_error at commit (got "+lockedResult.Code+")");
    Check(lockedBefore.SequenceEqual(Snapshot(lockedDirectory)),"held Core lock leaves every Core file byte-identical");

    // Directory-aware entry point: unsaved check, cloud scan, read, decide, scan again just before Commit.
    var cloudDirectory=Path.Combine(root,"cloud"); Core("cloud");
    var unsavedCloud=NativeReconcileContract.Reconcile(Path.Combine(root,"never-scanned"),Saved(r2,receipts1,savedAndReopened:false),packet1);
    Check(unsavedCloud.Code=="native_not_saved_and_reopened"&&unsavedCloud.CoreRevision is null,"directory overload refuses unsaved evidence before scanning or reading Core");
    File.WriteAllText(Path.Combine(cloudDirectory,"project-DESKTOP-AB12.json"),"conflict copy");
    var cloudBefore=Snapshot(cloudDirectory);
    var conflict=NativeReconcileContract.Reconcile(cloudDirectory,Saved(r2,receipts1),packet1);
    Check(conflict.Code==CloudGuard.ConflictPresent&&conflict.CoreRevision is null&&conflict.CoreHash is null&&cloudBefore.SequenceEqual(Snapshot(cloudDirectory)),"conflict copy beside project.json fails closed before Core is read (got "+conflict.Code+")");
    File.Delete(Path.Combine(cloudDirectory,"project-DESKTOP-AB12.json"));
    cloudBefore=Snapshot(cloudDirectory);
    var placeholder=NativeReconcileContract.Reconcile(cloudDirectory,Saved(r2,receipts1),packet1,_=>FileAttributes.Offline);
    Check(placeholder.Code==CloudGuard.PlaceholderPresent&&placeholder.CoreRevision is null&&cloudBefore.SequenceEqual(Snapshot(cloudDirectory)),"cloud-only placeholder fails closed before Core is read (got "+placeholder.Code+")");
    var entries=Directory.GetFileSystemEntries(cloudDirectory).Length; var calls=0;
    var late=NativeReconcileContract.Reconcile(cloudDirectory,Saved(r2,receipts1),packet1,_=>++calls>entries?FileAttributes.Offline:FileAttributes.Normal);
    Check(late.Code==CloudGuard.PlaceholderPresent&&calls>entries&&cloudBefore.SequenceEqual(Snapshot(cloudDirectory)),"placeholder appearing before the replay Commit fails closed with zero writes (got "+late.Code+")");
    var viaDirectory=NativeReconcileContract.Reconcile(cloudDirectory,Saved(r2,receipts1),packet1,_=>FileAttributes.Normal);
    Check(viaDirectory.Succeeded&&viaDirectory.Code=="replayed"&&Compiler.Hash(new ProjectStore(cloudDirectory).Read())==Compiler.Hash(r2),"directory overload replays a clean one-behind gap (got "+viaDirectory.Code+")");
    var missingDirectory=NativeReconcileContract.Reconcile(Path.Combine(root,"no-core-directory"),Saved(r2,receipts1),packet1);
    Check(missingDirectory.Code=="core_io_error"&&missingDirectory.CoreRevision is null,"missing Core directory fails closed core_io_error");
    Console.WriteLine($"PASS native reconcile guards: {count} checks");
} finally {
    var target=Path.GetFullPath(root);
    if(Path.GetDirectoryName(target)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(target).StartsWith("native-reconcile-guards-",StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup target");
    Directory.Delete(target,true);
}
