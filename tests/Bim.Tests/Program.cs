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
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {passed=count, core_test_ms=timer.Elapsed.TotalMilliseconds, native_verified=false}));
