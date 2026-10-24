using Bim.Core;
using Bim.Ifc;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// ADR0007 option A: deterministic cross-store crash injection without timing or process kills.
// IFC receipts here mirror what runtimes/ifc/worker.py resize_compiled persists; tests/ifc/test_reconcile.py repeats the IFC cases with real files.
try {
var count=0;
void Check(bool condition,string name) { if(!condition) throw new InvalidOperationException("FAIL "+name); Console.WriteLine("PASS "+name); count++; }
string NewDirectory(string name)=>Path.Combine(Environment.GetEnvironmentVariable("BIM_TEST_ROOT") ?? Path.GetTempPath(),"bim-reconcile-"+name+"-"+Guid.NewGuid());
string FileHash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string[] Snapshot(string directory)=>Directory.GetFiles(directory).OrderBy(file=>file,StringComparer.Ordinal).Select(file=>Path.GetFileName(file)+":"+FileHash(file)).ToArray();
bool Same(string[] a,string[] b)=>a.SequenceEqual(b);
ExecutionPlan Plan(IfcCompiledRequest packet)=>JsonSerializer.Deserialize<ExecutionPlan>(packet.PlanJson)!;
Project Before(IfcCompiledRequest packet)=>JsonSerializer.Deserialize<Project>(packet.BeforeJson)!;
Project After(IfcCompiledRequest packet)=>JsonSerializer.Deserialize<Project>(packet.AfterJson)!;
string Rev(int revision)=>"r"+revision.ToString(CultureInfo.InvariantCulture);
string CoreReceipt(string directory,string key)=>JsonNode.Parse(File.ReadAllText(Path.Combine(directory,"project.json")))?["Receipts"]?[key]?.GetValue<string>() ?? "missing";

var room=new RoomIntent(Guid.Parse("a71c442d-5181-4b11-9663-a4257ac1261f"),5,4,3,.2,-.2,-.2,"m",new("test","approved"));
var projectId=Guid.Parse("66ef39da-f724-4328-b055-c33e78dbb2ed");
var r7=RoomCompiler.Compile(projectId,7,room);
var r7After=RoomCompiler.Compile(projectId,7,room with {WidthMetres=5.4});
var packet1=IfcCompiler.Export(r7,r7After,Compiler.Diff(r7,r7After),room.RoomId,"resize-400");
var r8=r7After with {Revision=8};
var r8After=RoomCompiler.Compile(projectId,8,room with {WidthMetres=5.8});
var packet2=IfcCompiler.Export(r8,r8After,Compiler.Diff(r8,r8After),room.RoomId,"resize-next");

// Worker state after create(path, compiled): revision r, compiled_hash = plan BeforeHash, no receipts.
IfcBackendReceipt Initial(IfcCompiledRequest packet)=>new(new(Rev(Plan(packet).ExpectedRevision),Plan(packet).ExpectedRevision,Plan(packet).BeforeHash,Before(packet).Id,new()),false,null);
// Worker state after resize_compiled publishes: revision r+1, compiled_hash = sha(CommittedAfterJson), receipt with fingerprint and plan AfterHash.
IfcBackendReceipt Applied(IfcBackendReceipt prior,IfcCompiledRequest packet) {
    var revision=Plan(packet).ExpectedRevision+1;
    var receipts=new Dictionary<string,IfcOperationReceipt>(prior.State.Receipts) {[packet.OperationId]=new(5400,Rev(revision),IfcReconcile.Fingerprint(packet),Plan(packet).AfterHash)};
    return new(new(Rev(revision),revision,IfcCompiler.Sha256(packet.CommittedAfterJson),prior.State.CompiledProject,receipts),false,null);
}
ProjectStore NewStore(string name,out string directory) { directory=NewDirectory(name); var store=new ProjectStore(directory); store.Initialize(Before(packet1)); return store; }
void CommitPacket(ProjectStore store,IfcCompiledRequest packet)=>store.Commit(After(packet),Plan(packet),packet.OperationId);
void ExpectFailure(ProjectStore store,string directory,IfcBackendReceipt receipt,IfcCompiledRequest packet,string code,string name) {
    var before=Snapshot(directory);
    var result=IfcReconcile.Reconcile(store,receipt,packet);
    Check(!result.Succeeded && result.Code==code,name+" returns "+code+" (got "+result.Code+")");
    Check(Same(before,Snapshot(directory)),name+" leaves every Core file byte-identical");
}

// Byte identity the replay depends on (ADR0007 Unverified): Serialize(Deserialize(raw)) == raw.
foreach(var packet in new[]{packet1,packet2}) {
    Check(JsonSerializer.Serialize(After(packet))==packet.AfterJson,"AfterJson round-trip byte identity "+packet.OperationId);
    Check(JsonSerializer.Serialize(Before(packet))==packet.BeforeJson,"BeforeJson round-trip byte identity "+packet.OperationId);
    Check(JsonSerializer.Serialize(JsonSerializer.Deserialize<Project>(packet.CommittedAfterJson))==packet.CommittedAfterJson,"CommittedAfterJson round-trip byte identity "+packet.OperationId);
    Check(JsonSerializer.Serialize(Plan(packet))==packet.PlanJson,"PlanJson round-trip byte identity "+packet.OperationId);
}
var awkward=new Project(projectId,3,[new(Guid.NewGuid(),"level",[0.1+0.2,1e-7,1.0/3,double.Epsilon,double.MaxValue,-1234567.891011,5.4,0.2],[],new("Ñandú «source» \u2028 \"q\" <&>","apprové"))]);
var awkwardJson=JsonSerializer.Serialize(awkward);
Check(JsonSerializer.Serialize(JsonSerializer.Deserialize<Project>(awkwardJson))==awkwardJson,"awkward doubles and non-ASCII provenance round-trip byte identity");
var awkwardDirectory=NewDirectory("awkward"); var awkwardStore=new ProjectStore(awkwardDirectory); awkwardStore.Initialize(awkward);
Check(Compiler.Hash(awkwardStore.Read())==Compiler.Hash(awkward),"project.json round-trip preserves Core hash");

// Test plan 1 (C3) and 5: IFC published r+1, Core still at r.
{
    var store=NewStore("c3",out var directory);
    var receipt=Applied(Initial(packet1),packet1);
    var result=IfcReconcile.Reconcile(store,receipt,packet1);
    Check(result.Succeeded && result.Code=="replayed","C3 reconcile replays the latest one-revision gap");
    var core=store.Read();
    Check(core.Revision==receipt.State.CompiledRevision && result.CoreRevision==core.Revision,"C3 Core revision equals compiled_revision");
    Check(Compiler.Hash(core)==receipt.State.CompiledHash && result.CoreHash==receipt.State.CompiledHash,"C3 Core hash equals compiled_hash");
    Check(Compiler.Hash(core)==IfcCompiler.Sha256(packet1.CommittedAfterJson),"C3 Core snapshot bytes equal CommittedAfterJson");
    Check(CoreReceipt(directory,packet1.OperationId)==receipt.State.Receipts[packet1.OperationId].PlanAfterHash,"C3 Core receipt equals plan_after_hash");
    var replayed=Snapshot(directory);
    var again=IfcReconcile.Reconcile(store,receipt,packet1);
    Check(again.Succeeded && again.Code=="in_sync","second reconcile is a no-op");
    Check(Same(replayed,Snapshot(directory)),"second reconcile leaves every Core file byte-identical");
    var retry=store.Commit(After(packet1),Plan(packet1),packet1.OperationId);
    Check(Compiler.Hash(retry)==Compiler.Hash(core),"latest retry after recovery returns the identical project");
    Check(Same(replayed,Snapshot(directory)),"latest retry after recovery writes nothing");
}

// Test plan 2 (C1/C2): a crash-left staging file blocks reconcile before any comparison.
foreach(var (label,receipt) in new[]{("C1",Initial(packet1)),("C2",Initial(packet1)),("C3 with stale staging",Applied(Initial(packet1),packet1))}) {
    var store=NewStore("staging",out var directory);
    ExpectFailure(store,directory,receipt with {StagingArtifactPresent=true,StagingHash="00"},packet1,"staging_artifact_present",label+" staging artifact");
    Check(store.Read().Revision==7,label+" Core stays at r");
}

// Test plan 3 (C4/C5/C6): a crash inside Commit never publishes; the same key then succeeds.
foreach(var point in new[]{"after_checkpoint","after_journal","after_tmp"}) {
    NewStore("core-"+point,out var directory);
    var published=File.ReadAllBytes(Path.Combine(directory,"project.json"));
    var crashing=new ProjectStore(directory) {CrashAfter=step=>{ if(step==point) throw new IOException("injected_crash:"+step); }};
    var crashed=false;
    try { CommitPacket(crashing,packet1); } catch(IOException error) when(error.Message=="injected_crash:"+point) { crashed=true; }
    Check(crashed,point+" injection point reached");
    Check(File.ReadAllBytes(Path.Combine(directory,"project.json")).SequenceEqual(published),point+" leaves project.json at r");
    Check(new ProjectStore(directory).Read().Revision==7,point+" Core revision unchanged");
    if(point=="after_tmp") Check(File.Exists(Path.Combine(directory,"project.tmp")),"after_tmp leaves project.tmp behind");
    if(point!="after_checkpoint") Check(File.ReadAllText(Path.Combine(directory,"journal.jsonl")).Contains("\"prepared\"",StringComparison.Ordinal),point+" leaves an unmatched prepared journal line");
    // Torn checkpoint and torn trailing journal line: no reader may depend on them.
    File.WriteAllText(Path.Combine(directory,$"checkpoint-7.json"),"{\"Id\":\"torn");
    File.AppendAllText(Path.Combine(directory,"journal.jsonl"),"{\"key\":\"resize-400\",\"pla");
    var store=new ProjectStore(directory);
    var committed=store.Commit(After(packet1),Plan(packet1),packet1.OperationId);
    Check(committed.Revision==8 && Compiler.Hash(store.Read())==IfcCompiler.Sha256(packet1.CommittedAfterJson),point+" retry with the same key succeeds");
    Check(!File.Exists(Path.Combine(directory,"project.tmp")),point+" retry consumes any leftover project.tmp");
    var after=Snapshot(directory);
    store.Commit(After(packet1),Plan(packet1),packet1.OperationId);
    Check(Same(after,Snapshot(directory)),point+" second retry writes nothing");
}

// Test plan 4: historical packet after a later correction in both stores.
{
    var store=NewStore("historical",out var directory);
    CommitPacket(store,packet1); CommitPacket(store,packet2);
    var receipt=Applied(Applied(Initial(packet1),packet1),packet2);
    ExpectFailure(store,directory,receipt,packet1,"retry_superseded","historical reconcile after later correction");
    var before=Snapshot(directory);
    var replay=store.Commit(After(packet1),Plan(packet1),packet1.OperationId);
    Check(replay.Revision==9 && Same(before,Snapshot(directory)),"historical Core replay writes nothing");
    var latest=IfcReconcile.Reconcile(store,receipt,packet2);
    Check(latest.Succeeded && latest.Code=="in_sync" && Same(before,Snapshot(directory)),"latest packet after two corrections is in sync");
}
{
    var store=NewStore("historical-gap",out var directory);
    CommitPacket(store,packet1);
    var receipt=Applied(Applied(Initial(packet1),packet1),packet2);
    ExpectFailure(store,directory,receipt,packet1,"retry_superseded","historical packet while Core is one behind");
    var result=IfcReconcile.Reconcile(store,receipt,packet2);
    Check(result.Succeeded && result.Code=="replayed" && store.Read().Revision==9,"latest packet replays the second correction");
}

// Test plan 6: forged states each return a typed code with zero writes.
{
    var store=NewStore("core-ahead",out var directory); CommitPacket(store,packet1);
    ExpectFailure(store,directory,Initial(packet1),packet1,"core_ahead_of_backend","Core ahead of IFC");
}
{
    var store=NewStore("gap",out var directory);
    ExpectFailure(store,directory,Applied(Applied(Initial(packet1),packet1),packet2),packet2,"backend_gap_gt_1","IFC two revisions ahead");
}
{
    var store=NewStore("forged",out var directory);
    var good=Applied(Initial(packet1),packet1);
    var receipts=new Dictionary<string,IfcOperationReceipt>(good.State.Receipts);
    receipts[packet1.OperationId]=receipts[packet1.OperationId] with {CompiledFingerprint=new string('0',64)};
    ExpectFailure(store,directory,good with {State=good.State with {Receipts=receipts}},packet1,"receipt_packet_mismatch","packet fingerprint differs from receipt");
    receipts=new Dictionary<string,IfcOperationReceipt>(good.State.Receipts);
    receipts[packet1.OperationId]=receipts[packet1.OperationId] with {PlanAfterHash=new string('0',64)};
    ExpectFailure(store,directory,good with {State=good.State with {Receipts=receipts}},packet1,"receipt_packet_mismatch","plan_after_hash differs from receipt");
    ExpectFailure(store,directory,good with {State=good.State with {CompiledHash=new string('0',64)}},packet1,"receipt_packet_mismatch","compiled_hash differs from packet");
    ExpectFailure(store,directory,good with {State=good.State with {Receipts=new()}},packet1,"receipt_packet_mismatch","receipt missing the packet operation");
    ExpectFailure(store,directory,good with {State=good.State with {CompiledProject=Guid.NewGuid()}},packet1,"project_mismatch","IFC receipt for another project");
    ExpectFailure(store,directory,good with {State=good.State with {Revision="r9"}},packet1,"invalid_backend_receipt","revision label disagrees with compiled_revision");
    ExpectFailure(store,directory,good with {State=good.State with {CompiledRevision=null}},packet1,"invalid_backend_receipt","receipt without compiled revision");
    ExpectFailure(store,directory,Initial(packet1) with {State=Initial(packet1).State with {CompiledHash=new string('0',64)}},packet1,"divergent_state","same revision with different hashes");
    ExpectFailure(store,directory,good,packet1 with {PlanJson="{}"},"invalid_compiled_packet","tampered compiled packet");
    ExpectFailure(store,directory,good,packet2,"receipt_packet_mismatch","packet for a later base revision");
    Check(store.Read().Revision==7,"forged states leave Core at r");
}

// Equal revisions with a retained packet neither store has applied: nothing to replay.
{
    var store=NewStore("unapplied",out var directory);
    var before=Snapshot(directory);
    var result=IfcReconcile.Reconcile(store,Initial(packet1),packet1);
    Check(result.Succeeded && result.Code=="in_sync" && Same(before,Snapshot(directory)),"equal revisions with an unapplied packet are in sync without writes");
}
// Commit refusal after every reconcile precondition passed: a conflicting Core receipt for the same key.
{
    var store=NewStore("commit-rejected",out var directory);
    File.WriteAllText(Path.Combine(directory,"project.json"),JsonSerializer.Serialize(new ProjectStore.State(Before(packet1),new() {[packet1.OperationId]=new string('0',64)})));
    ExpectFailure(store,directory,Applied(Initial(packet1),packet1),packet1,"core_commit_rejected","Core idempotency conflict during replay");
}
// I/O failure inside the replay Commit is typed and never publishes project.json.
{
    NewStore("core-io",out var directory);
    var published=File.ReadAllBytes(Path.Combine(directory,"project.json"));
    var failing=new ProjectStore(directory) {CrashAfter=step=>{ if(step=="after_checkpoint") throw new IOException("injected_io:"+step); }};
    var result=IfcReconcile.Reconcile(failing,Applied(Initial(packet1),packet1),packet1);
    Check(!result.Succeeded && result.Code=="core_io_error","Commit I/O failure returns core_io_error (got "+result.Code+")");
    Check(File.ReadAllBytes(Path.Combine(directory,"project.json")).SequenceEqual(published),"Commit I/O failure leaves project.json at r");
}
// Missing or malformed compiled packets are typed.
{
    var store=NewStore("null-packet",out var directory);
    ExpectFailure(store,directory,Applied(Initial(packet1),packet1),null!,"invalid_compiled_packet","null compiled packet");
    Check(JsonSerializer.Serialize(IfcReconcile.ParsePacket(JsonSerializer.Serialize(packet1)))==JsonSerializer.Serialize(packet1),"compiled packet JSON parses");
    foreach(var bad in new[]{"null","not json","{\"ProtocolVersion\":\"x\"}"}) {
        try { IfcReconcile.ParsePacket(bad); throw new InvalidOperationException("malformed packet accepted"); } catch(ArgumentException error) when(error.Message=="invalid_compiled_packet") { Check(true,"malformed compiled packet JSON returns invalid_compiled_packet"); }
    }
}

// ADR0007 caveat: OneDrive conflict copies and cloud-only placeholders beside project.json fail closed before Core is read.
foreach(var (name,primary) in new[]{("project-DESKTOP-ABC.json","project.json"),("project-LAPTOP-1.json","project.json"),("project (1).json","project.json"),("project - Copy.json","project.json"),("Project-DESKTOP-AB12.JSON","project.json"),("PROJECT.json","project.json"),("room-LAPTOP-1.ifc","room.ifc"),("room (2).ifc","room.ifc"),("compiled-request-DESKTOP-ABC.json","compiled-request.json")})
    Check(IfcReconcile.IsConflictCopy(name,primary),"conflict copy name "+name+" next to "+primary);
foreach(var (name,primary) in new[]{("project.json","project.json"),("project.tmp","project.json"),("checkpoint-7.json","project.json"),("journal.jsonl","project.json"),("lock","project.json"),("projects.json","project.json"),("project-DESKTOP-ABC.tmp","project.json"),("room.ifc","room.ifc"),("room.pending.ifc","room.ifc"),("before.ifc","room.ifc"),("compiled-request.json","room.ifc"),("compiled-request.pending.json","compiled-request.json")})
    Check(!IfcReconcile.IsConflictCopy(name,primary),"ordinary store file "+name+" is not a conflict copy of "+primary);
void ExpectCloudFailure(string directory,IfcBackendReceipt receipt,string code,string name,Func<string,FileAttributes>? attributes=null) {
    var before=Snapshot(directory);
    var result=IfcReconcile.Reconcile(directory,receipt,packet1,attributes);
    Check(!result.Succeeded && result.Code==code,name+" returns "+code+" (got "+result.Code+")");
    Check(result.CoreRevision is null && result.CoreHash is null,name+" never reads Core");
    Check(Same(before,Snapshot(directory)),name+" leaves every Core file byte-identical");
}
foreach(var conflict in new[]{"project-DESKTOP-ABC.json","project (1).json","Project-LAPTOP-1.JSON"}) {
    NewStore("conflict",out var directory);
    File.WriteAllText(Path.Combine(directory,conflict),File.ReadAllText(Path.Combine(directory,"project.json")));
    // The receipt is a valid C3 gap, so without the guard reconcile would write.
    ExpectCloudFailure(directory,Applied(Initial(packet1),packet1),"cloud_conflict_present","Core conflict copy "+conflict);
    Check(new ProjectStore(directory).Read().Revision==7,"Core conflict copy "+conflict+" keeps Core at r");
}
foreach(var (flag,label) in new[]{(FileAttributes.Offline,"Offline"),((FileAttributes)0x00040000,"RecallOnOpen"),((FileAttributes)0x00400000,"RecallOnDataAccess")}) {
    foreach(var target in new[]{"project.json","journal.jsonl"}) {
        NewStore("placeholder",out var directory);
        if(target=="journal.jsonl") File.WriteAllText(Path.Combine(directory,target),"");
        FileAttributes Fake(string path)=>string.Equals(Path.GetFileName(path),target,StringComparison.Ordinal)?FileAttributes.Archive|FileAttributes.ReparsePoint|flag:FileAttributes.Archive;
        ExpectCloudFailure(directory,Applied(Initial(packet1),packet1),"cloud_placeholder_present",label+" placeholder "+target,Fake);
    }
}
{
    // HANDOFF section 7: OneDrive marks hydrated real files ReparsePoint without a link target; that alone is not a placeholder.
    var store=NewStore("reparse-only",out var directory);
    var result=IfcReconcile.Reconcile(directory,Applied(Initial(packet1),packet1),packet1,_=>FileAttributes.Archive|FileAttributes.ReparsePoint);
    Check(result.Succeeded && result.Code=="replayed" && store.Read().Revision==8,"ReparsePoint without recall attributes still replays (got "+result.Code+")");
    var clean=NewStore("clean-directory",out var cleanDirectory);
    var plain=IfcReconcile.Reconcile(cleanDirectory,Applied(Initial(packet1),packet1),packet1);
    Check(plain.Succeeded && plain.Code=="replayed" && clean.Read().Revision==8,"directory reconcile without cloud hazards replays (got "+plain.Code+")");
    var missing=IfcReconcile.Reconcile(NewDirectory("missing"),Applied(Initial(packet1),packet1),packet1);
    Check(!missing.Succeeded && missing.Code=="core_io_error","missing Core directory returns core_io_error (got "+missing.Code+")");
}
// A case-only variant is a conflict only beside the exact-case primary; alone it is just the store file under another case.
foreach(var variant in new[]{"Project.json","PROJECT.json"}) {
    var directory=NewDirectory("case-only"); Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory,variant),"{}"); File.WriteAllText(Path.Combine(directory,"lock"),"");
    Check(IfcReconcile.CloudHazard(directory,["project.json"]) is null,"only "+variant+" present is no cloud hazard");
    // Case-sensitive file systems only: both spellings side by side are a conflict.
    if(!File.Exists(Path.Combine(directory,"project.json"))) {
        File.WriteAllText(Path.Combine(directory,"project.json"),"{}");
        Check(IfcReconcile.CloudHazard(directory,["project.json"]) is {Code:"cloud_conflict_present"} hazard && hazard.Entries.SequenceEqual([variant]),variant+" beside project.json is a conflict copy");
    }
}
// TOCTOU: the Core-side scan runs again immediately before the replay Commit.
{
    NewStore("recheck",out var directory);
    var scans=0;
    FileAttributes Late(string path) { if(!string.Equals(Path.GetFileName(path),"project.json",StringComparison.Ordinal))return FileAttributes.Archive; scans++; return scans>1?FileAttributes.Archive|FileAttributes.Offline:FileAttributes.Archive; }
    ExpectCloudFailure(directory,Applied(Initial(packet1),packet1),"cloud_placeholder_present","placeholder appearing before the replay commit",Late);
    Check(scans==2,"Core directory scanned before reading and again before Commit (scans "+scans.ToString(CultureInfo.InvariantCulture)+")");
    var conflictScans=0;
    FileAttributes LateConflict(string path) { if(string.Equals(Path.GetFileName(path),"project.json",StringComparison.Ordinal)&&++conflictScans==1)File.WriteAllText(Path.Combine(directory,"project-DESKTOP-ABC.json"),"{}"); return FileAttributes.Archive; }
    var result=IfcReconcile.Reconcile(directory,Applied(Initial(packet1),packet1),packet1,LateConflict);
    Check(!result.Succeeded && result.Code=="cloud_conflict_present" && result.CoreRevision is null,"conflict copy appearing before the replay commit returns cloud_conflict_present (got "+result.Code+")");
    Check(new ProjectStore(directory).Read().Revision==7,"late conflict copy keeps Core at r");
}
// Crash leftovers (C4-C6: project.tmp, torn checkpoint, prepared and torn journal lines) are not cloud hazards: the directory overload replays.
{
    NewStore("leftovers",out var directory);
    var crashing=new ProjectStore(directory) {CrashAfter=step=>{ if(step=="after_tmp") throw new IOException("injected_crash:"+step); }};
    try { CommitPacket(crashing,packet1); } catch(IOException error) when(error.Message=="injected_crash:after_tmp") { }
    File.WriteAllText(Path.Combine(directory,"checkpoint-7.json"),"{\"Id\":\"torn");
    File.AppendAllText(Path.Combine(directory,"journal.jsonl"),"{\"key\":\"resize-400\",\"pla");
    Check(File.Exists(Path.Combine(directory,"project.tmp")),"leftover project.tmp present before reconcile");
    var result=IfcReconcile.Reconcile(directory,Applied(Initial(packet1),packet1),packet1);
    Check(result.Succeeded && result.Code=="replayed" && new ProjectStore(directory).Read().Revision==8,"crash leftovers through the directory overload replay (got "+result.Code+")");
}
// Callers cannot skip the guard: the ProjectStore overload is not public.
Check(!typeof(IfcReconcile).GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static).Any(method=>method.Name=="Reconcile"&&method.GetParameters()[0].ParameterType==typeof(ProjectStore)),"Reconcile(ProjectStore, ...) is not public");

// The worker reports the same hazards next to room.ifc in its receipt; reconcile refuses them through either entry point.
foreach(var code in new[]{"cloud_conflict_present","cloud_placeholder_present"}) {
    var store=NewStore("receipt-"+code,out var directory);
    var hazard=new IfcBackendReceipt(null!,false,null,code,["room-DESKTOP-ABC.ifc"]);
    ExpectFailure(store,directory,hazard,packet1,code,"worker receipt "+code);
    ExpectFailure(store,directory,Applied(Initial(packet1),packet1) with {CloudHazard=code,CloudHazardEntries=["room.ifc"]},packet1,code,"worker receipt "+code+" with state");
    ExpectCloudFailure(directory,hazard,code,"directory reconcile of worker receipt "+code);
}
{
    var store=NewStore("receipt-hazard-forged",out var directory);
    ExpectFailure(store,directory,Applied(Initial(packet1),packet1) with {CloudHazard="cloud_something_else",CloudHazardEntries=["x"]},packet1,"invalid_backend_receipt","unknown cloud hazard code");
    ExpectFailure(store,directory,Applied(Initial(packet1),packet1) with {CloudHazardEntries=["room-DESKTOP-ABC.ifc"]},packet1,"invalid_backend_receipt","cloud hazard entries without a code");
}

// Receipt parsing uses the worker's snake_case JSON and rejects unknown members.
var raw="{\"state\":{\"compiled_hash\":\"H\",\"compiled_project\":\"66ef39da-f724-4328-b055-c33e78dbb2ed\",\"compiled_revision\":8,\"receipts\":{\"resize-400\":{\"compiled_fingerprint\":\"F\",\"plan_after_hash\":\"P\",\"revision\":\"r8\",\"width\":5400.0}},\"revision\":\"r8\"},\"staging_artifact_present\":false,\"staging_hash\":null,\"cloud_hazard\":null,\"cloud_hazard_entries\":[]}";
var parsed=IfcReconcile.ParseReceipt(raw);
Check(parsed.State.CompiledRevision==8 && parsed.State.CompiledHash=="H" && parsed.State.CompiledProject==projectId && parsed.State.Revision=="r8" && !parsed.StagingArtifactPresent && parsed.CloudHazard is null,"worker receipt JSON parses");
Check(parsed.State.Receipts["resize-400"]==new IfcOperationReceipt(5400,"r8","F","P"),"worker operation receipt parses");
var hazardous=IfcReconcile.ParseReceipt("{\"state\":null,\"staging_artifact_present\":false,\"staging_hash\":null,\"cloud_hazard\":\"cloud_placeholder_present\",\"cloud_hazard_entries\":[\"room.ifc\"]}");
Check(hazardous.CloudHazard=="cloud_placeholder_present" && hazardous.CloudHazardEntries is ["room.ifc"],"worker cloud hazard receipt parses without state");
foreach(var bad in new[]{raw.Replace("\"staging_hash\":null","\"staging_hash\":null,\"extra\":1",StringComparison.Ordinal),raw.Replace(",\"cloud_hazard\":null,\"cloud_hazard_entries\":[]","",StringComparison.Ordinal),raw.Replace(",\"cloud_hazard_entries\":[]","",StringComparison.Ordinal),"not json","null","[]"}) {
    try { IfcReconcile.ParseReceipt(bad); throw new InvalidOperationException("malformed receipt accepted"); } catch(ArgumentException) { Check(true,"malformed worker receipt rejected"); }
}
// D01: durable Core writes keep the exact persisted bytes. Flush(true) reaching the disk is not observable from a test;
// these cases pin bytes, file presence and sizes at each crash point, and that leftovers never block the next Commit or reconcile.
// Reference bytes come from the File.WriteAllText/File.AppendAllText calls ProjectStore used before D01.
var legacyDirectory=NewDirectory("legacy"); Directory.CreateDirectory(legacyDirectory);
var legacyCheckpointPath=Path.Combine(legacyDirectory,"checkpoint-7.json");
File.WriteAllText(legacyCheckpointPath,JsonSerializer.Serialize(Before(packet1)));
var legacyCheckpoint=File.ReadAllBytes(legacyCheckpointPath);
var legacyJournalPath=Path.Combine(legacyDirectory,"journal.jsonl");
File.AppendAllText(legacyJournalPath,JsonSerializer.Serialize(new { key=packet1.OperationId, plan=Plan(packet1), state="prepared" })+Environment.NewLine);
var legacyJournalOne=File.ReadAllBytes(legacyJournalPath);
File.AppendAllText(legacyJournalPath,JsonSerializer.Serialize(new { key=packet2.OperationId, plan=Plan(packet2), state="prepared" })+Environment.NewLine);
var legacyJournalTwo=File.ReadAllBytes(legacyJournalPath);
string Listing(string directory)=>string.Join(",",Directory.GetFiles(directory).Select(file=>Path.GetFileName(file)).OrderBy(name=>name,StringComparer.Ordinal));
{
    var store=NewStore("durable-bytes",out var directory);
    CommitPacket(store,packet1);
    Check(File.ReadAllBytes(Path.Combine(directory,"checkpoint-7.json")).SequenceEqual(legacyCheckpoint),"checkpoint-7.json bytes equal the File.WriteAllText bytes");
    Check(File.ReadAllBytes(Path.Combine(directory,"journal.jsonl")).SequenceEqual(legacyJournalOne),"first journal line bytes equal the File.AppendAllText bytes");
    CommitPacket(store,packet2);
    Check(File.ReadAllBytes(Path.Combine(directory,"journal.jsonl")).SequenceEqual(legacyJournalTwo),"second appended journal line bytes equal the File.AppendAllText bytes");
    Check(Listing(directory)=="checkpoint-7.json,checkpoint-8.json,journal.jsonl,lock,project.json","successful commits leave no temp file (got "+Listing(directory)+")");
}
var durablePoints=new[]{"after_checkpoint_tmp","after_checkpoint","after_journal","after_tmp"};
foreach(var point in durablePoints) {
    NewStore("durable-"+point,out var directory);
    string At(string name)=>Path.Combine(directory,name);
    var stage=Array.IndexOf(durablePoints,point);
    var published=File.ReadAllBytes(At("project.json"));
    var crashing=new ProjectStore(directory) {CrashAfter=step=>{ if(step==point) throw new IOException("injected_crash:"+step); }};
    var crashed=false;
    try { CommitPacket(crashing,packet1); } catch(IOException error) when(error.Message=="injected_crash:"+point) { crashed=true; }
    Check(crashed,point+" durability injection point reached");
    Check(File.ReadAllBytes(At("project.json")).SequenceEqual(published),point+" leaves project.json byte-identical at r");
    Check(stage==0?File.Exists(At("checkpoint-7.json.tmp"))&&new FileInfo(At("checkpoint-7.json.tmp")).Length==legacyCheckpoint.Length&&File.ReadAllBytes(At("checkpoint-7.json.tmp")).SequenceEqual(legacyCheckpoint):!File.Exists(At("checkpoint-7.json.tmp")),point+" checkpoint temp is complete when present and gone after replace");
    Check(stage==0?!File.Exists(At("checkpoint-7.json")):new FileInfo(At("checkpoint-7.json")).Length==legacyCheckpoint.Length&&File.ReadAllBytes(At("checkpoint-7.json")).SequenceEqual(legacyCheckpoint),point+" checkpoint-7.json is absent or complete, never torn");
    Check(stage<2?!File.Exists(At("journal.jsonl")):new FileInfo(At("journal.jsonl")).Length==legacyJournalOne.Length&&File.ReadAllBytes(At("journal.jsonl")).SequenceEqual(legacyJournalOne),point+" journal.jsonl is absent or holds exactly one prepared line");
    Check(stage<3?!File.Exists(At("project.tmp")):new FileInfo(At("project.tmp")).Length>0,point+" project.tmp exists only after the snapshot temp write");
    var leftover=stage==3?File.ReadAllBytes(At("project.tmp")):null;
    var store=new ProjectStore(directory);
    var committed=store.Commit(After(packet1),Plan(packet1),packet1.OperationId);
    Check(committed.Revision==8 && Compiler.Hash(store.Read())==IfcCompiler.Sha256(packet1.CommittedAfterJson),point+" retry after the durability crash succeeds");
    Check(Listing(directory)=="checkpoint-7.json,journal.jsonl,lock,project.json",point+" retry leaves no temp file (got "+Listing(directory)+")");
    Check(File.ReadAllBytes(At("checkpoint-7.json")).SequenceEqual(legacyCheckpoint),point+" retry leaves a complete checkpoint-7.json");
    Check(File.ReadAllBytes(At("journal.jsonl")).SequenceEqual(stage<2?legacyJournalOne:legacyJournalOne.Concat(legacyJournalOne).ToArray()),point+" retry appends exactly one prepared line with unchanged bytes");
    if(leftover is not null) Check(leftover.SequenceEqual(File.ReadAllBytes(At("project.json"))),point+" leftover project.tmp held the complete snapshot the retry publishes");
}
{
    // An older checkpoint at the same path survives a crash between the temp write and the replace untouched.
    NewStore("durable-old-checkpoint",out var directory);
    var old=Encoding.UTF8.GetBytes("{\"old\":true}");
    File.WriteAllBytes(Path.Combine(directory,"checkpoint-7.json"),old);
    var crashing=new ProjectStore(directory) {CrashAfter=step=>{ if(step=="after_checkpoint_tmp") throw new IOException("injected_crash:"+step); }};
    try { CommitPacket(crashing,packet1); } catch(IOException error) when(error.Message=="injected_crash:after_checkpoint_tmp") { }
    Check(File.ReadAllBytes(Path.Combine(directory,"checkpoint-7.json")).SequenceEqual(old),"crash before the checkpoint replace leaves the old checkpoint byte-identical");
    Check(File.ReadAllBytes(Path.Combine(directory,"checkpoint-7.json.tmp")).SequenceEqual(legacyCheckpoint),"crash before the checkpoint replace leaves a complete temp");
    Check(IfcReconcile.CloudHazard(directory,["project.json"]) is null,"leftover checkpoint-7.json.tmp is not a cloud hazard beside project.json");
    var result=IfcReconcile.Reconcile(directory,Applied(Initial(packet1),packet1),packet1);
    Check(result.Succeeded && result.Code=="replayed" && new ProjectStore(directory).Read().Revision==8,"leftover checkpoint temp does not block directory reconcile (got "+result.Code+")");
    Check(File.ReadAllBytes(Path.Combine(directory,"checkpoint-7.json")).SequenceEqual(legacyCheckpoint) && !File.Exists(Path.Combine(directory,"checkpoint-7.json.tmp")),"reconcile replay replaces the old checkpoint and consumes the temp");
}
{
    // A torn temp (crash during the temp write itself) is overwritten by the next Commit.
    var store=NewStore("durable-torn-temp",out var directory);
    File.WriteAllText(Path.Combine(directory,"checkpoint-7.json.tmp"),"{\"Id\":\"torn");
    Check(IfcReconcile.CloudHazard(directory,["project.json"]) is null,"torn checkpoint temp is not a cloud hazard");
    CommitPacket(store,packet1);
    Check(store.Read().Revision==8 && File.ReadAllBytes(Path.Combine(directory,"checkpoint-7.json")).SequenceEqual(legacyCheckpoint),"torn checkpoint temp does not break the next Commit");
    Check(Listing(directory)=="checkpoint-7.json,journal.jsonl,lock,project.json","Commit consumes the torn checkpoint temp (got "+Listing(directory)+")");
}
foreach(var (name,primary) in new[]{("checkpoint-7.json.tmp","project.json"),("checkpoint-0.json.tmp","project.json"),("checkpoint-7.json.tmp","checkpoint-7.json"),("checkpoint-7.json","checkpoint-17.json")})
    Check(!IfcReconcile.IsConflictCopy(name,primary),"checkpoint temp name "+name+" is not a conflict copy of "+primary);

Console.WriteLine(JsonSerializer.Serialize(new {passed=count, native_verified=false}));
} catch(Exception exception) {
    Console.Error.WriteLine(exception);
    Environment.ExitCode=1;
}
