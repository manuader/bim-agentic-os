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
