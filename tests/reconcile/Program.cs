using Bim.Core;
using Bim.Ifc;
using System.Globalization;
using System.Security.Cryptography;
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
var awkward=new Project(projectId,3,[new(Guid.NewGuid(),"level",[0.1+0.2,1e-7,1.0/3,double.Epsilon,double.MaxValue,-1234567.891011,5.4,0.2],[],new("Ñandú «source»   \"q\" <&>","apprové"))]);
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

// Receipt parsing uses the worker's snake_case JSON and rejects unknown members.
var raw="{\"state\":{\"compiled_hash\":\"H\",\"compiled_project\":\"66ef39da-f724-4328-b055-c33e78dbb2ed\",\"compiled_revision\":8,\"receipts\":{\"resize-400\":{\"compiled_fingerprint\":\"F\",\"plan_after_hash\":\"P\",\"revision\":\"r8\",\"width\":5400.0}},\"revision\":\"r8\"},\"staging_artifact_present\":false,\"staging_hash\":null}";
var parsed=IfcReconcile.ParseReceipt(raw);
Check(parsed.State.CompiledRevision==8 && parsed.State.CompiledHash=="H" && parsed.State.CompiledProject==projectId && parsed.State.Revision=="r8" && !parsed.StagingArtifactPresent,"worker receipt JSON parses");
Check(parsed.State.Receipts["resize-400"]==new IfcOperationReceipt(5400,"r8","F","P"),"worker operation receipt parses");
foreach(var bad in new[]{raw.Replace("\"staging_hash\":null","\"staging_hash\":null,\"extra\":1",StringComparison.Ordinal),"not json","null"}) {
    try { IfcReconcile.ParseReceipt(bad); throw new InvalidOperationException("malformed receipt accepted"); } catch(ArgumentException) { Check(true,"malformed worker receipt rejected"); }
}
Console.WriteLine(JsonSerializer.Serialize(new {passed=count, native_verified=false}));
} catch(Exception exception) {
    Console.Error.WriteLine(exception);
    Environment.ExitCode=1;
}
