using Bim.Core;
using System.Diagnostics;
int count=0;
void Check(bool condition,string name) { if(!condition) throw new Exception(name); Console.WriteLine("PASS "+name); count++; }
var wall=Guid.NewGuid(); var plan=Guid.NewGuid(); var sheet=Guid.NewGuid(); var other=Guid.NewGuid();
var p=new Project(Guid.NewGuid(),0,[new(wall,"wall",[0,0,5,0,3,.2],[],new("fixture","specified")),new(plan,"plan",[],[wall],new("fixture","specified")),new(sheet,"sheet",[],[plan],new("fixture","specified")),new(other,"level",[0],[],new("fixture","specified"))]);
var next=p with { Elements=p.Elements.Select(e=>e.Id==wall?e with {GeometryMetres=[0,0,5.4,0,3,.2]}:e).ToArray() };
var timer=Stopwatch.StartNew(); var diff=Compiler.Diff(p,next);
Check(Math.Abs(new Length(1).Feet-3.280839895)<1e-8,"metres to feet");
Check(diff.Operations.Length==1 && diff.Operations[0].Action=="update","minimal update");
Check(diff.DirtyIds.ToHashSet().SetEquals([wall,plan,sheet]),"transitive documentation closure");
Check(!diff.DirtyIds.Contains(other),"unrelated identity preserved");
Check(Compiler.Diff(p,p).Operations.Length==0,"no-op");
Check(Compiler.Validate(p with { Elements=[p.Elements[0],p.Elements[0]] }).Length>0,"duplicate rejected");
Check(Compiler.Validate(p with { Elements=[p.Elements[1]] }).Length>0,"dangling dependency rejected");
var selfCycle=p with { Elements=[p.Elements[3] with {Dependencies=[other]}] };
var pairCycle=p with { Elements=[p.Elements[1] with {Dependencies=[sheet]},p.Elements[2]] };
Check(Compiler.Validate(selfCycle).Any(i=>i.Code=="dependency_cycle"),"self dependency rejected as cycle");
Check(Compiler.Validate(pairCycle).Any(i=>i.Code=="dependency_cycle"),"two element dependency cycle rejected");
var tripleCycle=p with {Elements=[p.Elements[0] with {Dependencies=[sheet]},p.Elements[1],p.Elements[2]]};
Check(Compiler.Validate(tripleCycle).Any(i=>i.Code=="dependency_cycle"),"three element dependency cycle rejected");
var sharedDag=p with {Elements=[p.Elements[0],p.Elements[1],p.Elements[2] with {Dependencies=[wall,plan]}]};
Check(Compiler.Validate(sharedDag).Length==0,"shared prerequisite DAG accepted");
var chainIds=Enumerable.Range(0,12000).Select(_=>Guid.NewGuid()).ToArray();
var chain=p with {Elements=chainIds.Select((id,i)=>new Element(id,"level",[0],i+1<chainIds.Length?[chainIds[i+1]]:[],new("fixture","specified"))).ToArray()};
Check(Compiler.Validate(chain).Length==0,"12000 element dependency chain accepted without recursion");
Check(Compiler.Validate(pairCycle with {Elements=[pairCycle.Elements[0],pairCycle.Elements[0]]}).Any(i=>i.Code=="duplicate_id"),"duplicate cyclic identities report validation issue");
Check(Compiler.Validate(selfCycle with {Elements=[selfCycle.Elements[0] with {Dependencies=[other,Guid.NewGuid()]}]}).Any(i=>i.Code=="missing_dependency"),"cycle with dangling dependency reports validation issue");
foreach(var cyclic in new[]{selfCycle,pairCycle,tripleCycle}) {
 try {Compiler.Diff(p,cyclic);throw new Exception("cyclic_diff_accepted");}catch(InvalidOperationException ex) when(ex.Message=="invalid_ir"){Check(true,"diff rejects cyclic desired state");}
 try {Compiler.Diff(cyclic,p);throw new Exception("cyclic_before_accepted");}catch(InvalidOperationException ex) when(ex.Message=="invalid_ir"){Check(true,"diff rejects cyclic existing state");}
}
var cycleDir=Path.Combine(envRoot(),"bim-cycle-guard-"+Guid.NewGuid());
var cycleStore=new ProjectStore(cycleDir); cycleStore.Initialize(p);
var snapshotBefore=File.ReadAllBytes(Path.Combine(cycleDir,"project.json"));
var forgedCyclePlan=new ExecutionPlan(p.Id,p.Revision,Compiler.Hash(p),Compiler.Hash(pairCycle),[],[]);
try {cycleStore.Commit(pairCycle,forgedCyclePlan,"cycle");throw new Exception("cyclic_commit_accepted");}catch(InvalidOperationException ex) when(ex.Message=="invalid_ir"){Check(true,"intended store rejects cyclic snapshot");}
Check(File.ReadAllBytes(Path.Combine(cycleDir,"project.json")).SequenceEqual(snapshotBefore) && !File.Exists(Path.Combine(cycleDir,"journal.jsonl")) && !File.Exists(Path.Combine(cycleDir,"checkpoint-0.json")),"cyclic commit leaves persisted state unchanged");
var cycleInitDir=Path.Combine(envRoot(),"bim-cycle-init-"+Guid.NewGuid());
try {new ProjectStore(cycleInitDir).Initialize(selfCycle);throw new Exception("cyclic_initialize_accepted");}catch(InvalidOperationException ex) when(ex.Message=="invalid_ir"){Check(true,"intended store rejects cyclic initialization");}
Check(!File.Exists(Path.Combine(cycleInitDir,"project.json")),"cyclic initialization publishes no snapshot");
var dir=Path.Combine(Environment.GetEnvironmentVariable("BIM_TEST_ROOT") ?? Path.GetTempPath(),"bim-test-"+Guid.NewGuid()); var store=new ProjectStore(dir); store.Initialize(p);
Check(store.Commit(next,diff,"rev1").Revision==1,"durable revision");
Check(new ProjectStore(dir).Commit(next,diff,"rev1").Revision==1,"retry persisted idempotency");
try {store.Commit(next,diff,"different");throw new Exception("not rejected");} catch(InvalidOperationException) {Check(true,"stale revision rejected");}
try {store.Commit(p,Compiler.Diff(p,p),"rev1");throw new Exception("not rejected");} catch(InvalidOperationException) {Check(true,"idempotency payload conflict rejected");}
Check(store.Read().Revision==1,"rejected writes leave snapshot unchanged");

var roomIntent=new RoomIntent(Guid.Parse("11111111-1111-1111-1111-111111111111"),5,4,3,.2,10,20,"m",new("architect","approved"));
var room=RoomCompiler.Compile(p.Id,0,roomIntent);
Check(room.Elements.Count(e=>e.Kind=="wall")==4,"room has four walls");
Check(room.Elements.Single(e=>e.Kind=="space").GeometryMetres.SequenceEqual(new[]{10.2,20.2,5.0,4.0}),"room clear dimensions and exterior anchor in metres");
Check(room.Elements.All(e=>e.Provenance==roomIntent.Provenance),"room provenance retained");
Check(Compiler.Hash(room)==Compiler.Hash(RoomCompiler.Compile(p.Id,0,roomIntent)),"room deterministic");
var resized=RoomCompiler.Compile(p.Id,0,roomIntent with { WidthMetres=5.4 });
var roomDiff=Compiler.Diff(room,resized);
Check(roomDiff.Operations.Length==4,"resize minimal three walls and space");
Check(room.Elements.Select(e=>e.Id).SequenceEqual(resized.Elements.Select(e=>e.Id)),"resize preserves all logical identities");
Check(roomDiff.Operations.All(o=>o.Action=="update"),"resize never recreates");
var west=room.Elements.Single(e=>e.Id==RoomCompiler.Id(roomIntent.RoomId,"west"));
Check(!roomDiff.DirtyIds.Contains(west.Id) && resized.Elements.Single(e=>e.Id==west.Id).GeometryMetres.SequenceEqual(west.GeometryMetres),"west exterior anchor fixed");
Check(roomDiff.DirtyIds.Contains(RoomCompiler.Id(roomIntent.RoomId,"sheet")),"room revision invalidates sheet");
Check(!roomDiff.DirtyIds.Contains(RoomCompiler.Id(roomIntent.RoomId,"level")),"room revision retains level");
foreach(var bad in new[]{roomIntent with {WidthMetres=0},roomIntent with {Units="ft"},roomIntent with {HeightMetres=double.NaN},roomIntent with {ThicknessMetres=3},roomIntent with {RoomId=Guid.Empty}}) {
 try {RoomCompiler.Compile(p.Id,0,bad);throw new Exception("accepted_invalid_room");}catch(ArgumentException){Check(true,"invalid room intent rejected");}
}
Check(NativeBatchContract.Preflight(new(p.Id,0,[]),room,Compiler.Diff(new(p.Id,0,[]),room)).Any(i=>i.Code=="unsupported_native_kind"),"documentation and space block native room batch");
Check(NativeBatchContract.Preflight(room,resized,roomDiff with {Operations=[]}).Any(i=>i.Code=="plan_mismatch"),"tampered operations rejected");
Check(NativeBatchContract.Preflight(new(p.Id,0,[p.Elements[3]]),new(p.Id,0,[]),Compiler.Diff(new(p.Id,0,[p.Elements[3]]),new(p.Id,0,[]))).Any(i=>i.Code=="unsupported_native_delete"),"native deletes blocked to prevent untracked cascades");
var guardDir=Path.Combine(envRoot(),"bim-plan-guard-"+Guid.NewGuid());
string envRoot()=>Environment.GetEnvironmentVariable("BIM_TEST_ROOT") ?? Path.GetTempPath();
var guardedStore=new ProjectStore(guardDir); guardedStore.Initialize(p);
try {guardedStore.Commit(next,diff with { Operations=[] },"bad-plan");throw new Exception("tampered_store_plan_accepted");}catch(InvalidOperationException){Check(true,"intended store rejects tampered operations");}
try {RoomCompiler.Compile(p.Id,0,roomIntent with {WidthMetres=double.MaxValue,AnchorXMetres=double.MaxValue});throw new Exception("overflow_room_accepted");}catch(ArgumentException){Check(true,"room coordinate overflow rejected");}


var goodLevel=new Project(p.Id,0,[p.Elements[3]]);
var badLevel=goodLevel with {Elements=[p.Elements[3] with {GeometryMetres=[0,1]}]};
Check(NativeBatchContract.Preflight(goodLevel,badLevel,Compiler.Diff(goodLevel,badLevel)).Any(i=>i.Code=="invalid_level"),"native preflight validates new state of retained identities");
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {passed=count, core_test_ms=timer.Elapsed.TotalMilliseconds, native_verified=false}));
