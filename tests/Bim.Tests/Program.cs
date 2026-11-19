using Bim.Core;
using System.Diagnostics;
try {
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
// P5-24: JurisdictionRegistry.Load and LoadLocked expose the loader's typed SkillException of their one directory read (LoaderError).
{
 var p524=Path.Combine(envRoot(),"bim-p524-"+Guid.NewGuid());
 try {
  var missing=Path.Combine(p524,"absent");
  var missingLoad=JurisdictionRegistry.Load(missing);
  SkillException? missingDirect=null; try {SkillLoader.LoadDirectory(missing);} catch(SkillException e){missingDirect=e;}
  Check(missingDirect is not null && missingLoad.Refusal?.Code==JurisdictionCodes.SkillsInvalid && missingLoad.LoaderError is {} me
   && me.Code==missingDirect.Code && me.Field==missingDirect.Field && me.Detail==missingDirect.Detail && me.File==missingDirect.File,"P5-24 registry load of a missing directory carries the loader's typed error");
  Check(missingLoad.Refusal!.Subject==(missingDirect!.File ?? ".") && missingLoad.Refusal.Detail==missingDirect.Code+(missingDirect.Detail is null ? "" : ": "+missingDirect.Detail),"P5-24 skills_invalid refusal subject and detail unchanged");
  var badRoot=Path.Combine(p524,"bad");
  Directory.CreateDirectory(Path.Combine(badRoot,"xx.bad.pack"));
  File.WriteAllText(Path.Combine(badRoot,"xx.bad.pack","skill.json"),"{ \"manifest_version\": 1, ");
  var badLoad=JurisdictionRegistry.Load(badRoot);
  SkillException? badDirect=null; try {SkillLoader.LoadDirectory(badRoot);} catch(SkillException e){badDirect=e;}
  Check(badDirect is not null && badLoad.Registry is null && badLoad.LoaderError is {} be && be.Code==badDirect.Code && be.Field==badDirect.Field && be.Detail==badDirect.Detail && be.File==badDirect.File,"P5-24 malformed manifest: LoaderError equals the loader's error");
  var badLocked=JurisdictionRegistry.LoadLocked(badRoot,new SkillLock(Array.Empty<SkillLockEntry>()));
  Check(badDirect is not null && badLocked.Registry is null && badLocked.Refusal?.Code==JurisdictionCodes.SkillsInvalid && badLocked.Refusal==badLoad.Refusal
   && badLocked.LoaderError is {} le && le.Code==badDirect.Code && le.Field==badDirect.Field && le.Detail==badDirect.Detail && le.File==badDirect.File,"P5-24 LoadLocked on a malformed manifest: LoaderError equals the loader's error, refusal as unlocked");
  Directory.CreateDirectory(Path.Combine(p524,"empty"));
  var emptyLoad=JurisdictionRegistry.Load(Path.Combine(p524,"empty"));
  Check(emptyLoad.Succeeded && emptyLoad.LoaderError is null,"P5-24 a successful load has no LoaderError");
  Check(new JurisdictionLoad(null,new JurisdictionIssue("json_invalid","x/jurisdiction.json","d")).LoaderError is null,"P5-24 LoaderError defaults to null");
 } finally { if(Directory.Exists(p524)) Directory.Delete(p524,true); }
}
// P5-25: JurisdictionLoad.LoaderError is set only inside Bim.Core and only travels with the skills_invalid Refusal it explains.
{
 var p525=Path.Combine(envRoot(),"bim-p525-"+Guid.NewGuid());
 try {
  var qLoadType=typeof(JurisdictionLoad);
  var qLoaderProperty=qLoadType.GetProperty(nameof(JurisdictionLoad.LoaderError))!;
  var qLoaderSetter=qLoaderProperty.GetSetMethod(true);
  Check(qLoaderProperty.GetGetMethod() is {IsPublic:true} && (qLoaderSetter is null || qLoaderSetter.IsAssembly || qLoaderSetter.IsPrivate),"P5-25 LoaderError has no public or protected setter or init accessor");
  Check(qLoadType.GetConstructors().All(qCtor=>qCtor.GetParameters().All(qPa=>!typeof(Exception).IsAssignableFrom(qPa.ParameterType))),"P5-25 no public constructor takes the loader error");
  Check(qLoadType.GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public).Length==0,"P5-25 no public instance field exposes the loader error");
  var qBad=Path.Combine(p525,"bad");
  Directory.CreateDirectory(Path.Combine(qBad,"xx.bad.pack"));
  File.WriteAllText(Path.Combine(qBad,"xx.bad.pack","skill.json"),"{ \"manifest_version\": 1, ");
  var qBadLoad=JurisdictionRegistry.Load(qBad);
  var qBadLocked=JurisdictionRegistry.LoadLocked(qBad,new SkillLock(Array.Empty<SkillLockEntry>()));
  Check(qBadLoad.Refusal?.Code==JurisdictionCodes.SkillsInvalid && qBadLoad.LoaderError is not null && qBadLocked.Refusal?.Code==JurisdictionCodes.SkillsInvalid && qBadLocked.LoaderError is not null,"P5-25 skills_invalid from Load and LoadLocked carries a LoaderError");
  var qOther=new JurisdictionIssue("json_invalid","x/jurisdiction.json","d");
  Check((qBadLoad with {Refusal=qOther}).LoaderError is null && (qBadLocked with {Refusal=qOther}).LoaderError is null,"P5-25 a with-copy replacing the Refusal drops the stale LoaderError");
  // P5-26: clearing the Refusal of a refusal would leave neither Registry nor Refusal, so that with-copy now throws.
  try { _=qBadLoad with {Refusal=null}; Check(false,"P5-25/P5-26 a with-copy clearing the Refusal throws"); } catch(ArgumentException) { Check(true,"P5-25/P5-26 a with-copy clearing the Refusal throws"); }
  Check((qBadLoad with {Refusal=qBadLoad.Refusal! with {}}).LoaderError is null,"P5-25 an equal but distinct Refusal instance does not inherit the LoaderError");
  Check(ReferenceEquals((qBadLoad with {Registry=null}).LoaderError,qBadLoad.LoaderError),"P5-25 a with-copy keeping the same Refusal keeps its LoaderError");
  // Equality compares Registry, Refusal and the derived LoaderError (P5-26); a copy with another refusal is unequal and reads LoaderError as null.
  Check((qBadLoad with {}).Equals(qBadLoad) && !(qBadLoad with {Refusal=qOther}).Equals(qBadLoad) && (qBadLoad with {Refusal=qOther}).LoaderError is null,"P5-25 equality: a plain copy is equal, a with { Refusal = other } copy is unequal and has no LoaderError");
  var qSide=Path.Combine(p525,"side");
  Directory.CreateDirectory(Path.Combine(qSide,"xx.p525.pack"));
  File.WriteAllText(Path.Combine(qSide,"xx.p525.pack","skill.json"),"{\"manifest_version\":1,\"id\":\"xx.p525.pack\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fixture\",\"priority\":10,\"rules\":[]}");
  File.WriteAllText(Path.Combine(qSide,"xx.p525.pack","jurisdiction.json"),"{");
  var qSideLoad=JurisdictionRegistry.Load(qSide);
  Check(qSideLoad.Refusal is {} qSr && qSr.Code==JurisdictionCodes.JsonInvalid && qSr.Subject=="xx.p525.pack/jurisdiction.json" && qSideLoad.LoaderError is null,"P5-25 a sidecar refusal from Load has no LoaderError ("+qSideLoad.Refusal?.Code+")");
  var qSideLocked=JurisdictionRegistry.LoadLocked(qSide,SkillLocking.Pin(qSide,["xx.p525.pack"]));
  Check(qSideLocked.Refusal is {} qLr && qLr.Code==JurisdictionCodes.JsonInvalid && qLr.Subject=="xx.p525.pack/jurisdiction.json" && qSideLocked.LoaderError is null,"P5-25 a sidecar refusal from LoadLocked has no LoaderError ("+qSideLocked.Refusal?.Code+")");
  File.Delete(Path.Combine(qSide,"xx.p525.pack","jurisdiction.json"));
  var qOkLoad=JurisdictionRegistry.Load(qSide);
  var qOkLocked=JurisdictionRegistry.LoadLocked(qSide,SkillLocking.Pin(qSide,["xx.p525.pack"]));
  Check(qOkLoad.Succeeded && qOkLoad.LoaderError is null && qOkLocked.Succeeded && qOkLocked.LoaderError is null,"P5-25 successful Load and LoadLocked have no LoaderError");
 } finally { if(Directory.Exists(p525)) Directory.Delete(p525,true); }
}
// P5-26: exactly one of JurisdictionLoad.Registry / Refusal is non-null, and equality is (Registry, Refusal, LoaderError).
{
 var p526=Path.Combine(envRoot(),"bim-p526-"+Guid.NewGuid());
 bool r26Throws(Func<object> make) { try { make(); return false; } catch(ArgumentException) { return true; } }
 string? r26Param(Func<object> make) { try { make(); return "(no throw)"; } catch(ArgumentException e) { return e.ParamName; } }
 bool r26One(JurisdictionLoad load) => (load.Registry is null) != (load.Refusal is null) && load.Succeeded == (load.Registry is not null);
 try {
  var r26Empty=Path.Combine(p526,"empty");
  Directory.CreateDirectory(r26Empty);
  var r26Ok=JurisdictionRegistry.Load(r26Empty);
  var r26Reg=r26Ok.Registry!;
  var r26Issue=new JurisdictionIssue("json_invalid","x/jurisdiction.json","d");
  Check(r26Throws(()=>new JurisdictionLoad(null,null)),"P5-26 a constructor with neither Registry nor Refusal throws");
  Check(r26Throws(()=>new JurisdictionLoad(r26Reg,r26Issue)),"P5-26 a constructor with both Registry and Refusal throws");
  Check(r26One(new JurisdictionLoad(r26Reg,null)) && r26One(new JurisdictionLoad(null,r26Issue)),"P5-26 a constructor with exactly one of Registry and Refusal is accepted");
  var r26Bad=Path.Combine(p526,"bad");
  Directory.CreateDirectory(Path.Combine(r26Bad,"xx.bad.pack"));
  File.WriteAllText(Path.Combine(r26Bad,"xx.bad.pack","skill.json"),"{ \"manifest_version\": 1, ");
  var r26BadLoad=JurisdictionRegistry.Load(r26Bad);
  var r26Side=Path.Combine(p526,"side");
  Directory.CreateDirectory(Path.Combine(r26Side,"xx.p526.pack"));
  File.WriteAllText(Path.Combine(r26Side,"xx.p526.pack","skill.json"),"{\"manifest_version\":1,\"id\":\"xx.p526.pack\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fixture\",\"priority\":10,\"rules\":[]}");
  var r26SideLock=SkillLocking.Pin(r26Side,["xx.p526.pack"]);
  File.WriteAllText(Path.Combine(r26Side,"xx.p526.pack","jurisdiction.json"),"{");
  var r26Results=new (string Name,JurisdictionLoad Load)[] {
   ("Load missing directory",JurisdictionRegistry.Load(Path.Combine(p526,"absent"))),
   ("Load malformed manifest",r26BadLoad),
   ("LoadLocked malformed manifest",JurisdictionRegistry.LoadLocked(r26Bad,new SkillLock(Array.Empty<SkillLockEntry>()))),
   ("Load malformed sidecar",JurisdictionRegistry.Load(r26Side)),
   ("LoadLocked drifted sidecar",JurisdictionRegistry.LoadLocked(r26Side,r26SideLock)),
   ("LoadLocked missing lock file",JurisdictionRegistry.LoadLocked(r26Side,Path.Combine(p526,"absent.lock"))),
   ("Load empty",r26Ok),
   ("LoadLocked empty",JurisdictionRegistry.LoadLocked(r26Empty,new SkillLock(Array.Empty<SkillLockEntry>()))),
  };
  Check(r26Results[0].Load.Registry is null && r26Results[0].Load.Refusal?.Code==JurisdictionCodes.SkillsInvalid,"P5-26 Load of a missing directory sets Refusal (skills_invalid), not Registry ("+r26Results[0].Load.Refusal?.Code+")");
  foreach(var (r26Name,r26Load) in r26Results) Check(r26One(r26Load),"P5-26 exactly one of Registry and Refusal: "+r26Name+" ("+r26Load.Refusal?.Code+")");
  Check(r26Throws(()=>r26Ok with {Registry=null}),"P5-26 a with-copy clearing the Registry of a success throws");
  Check(r26Throws(()=>r26Ok with {Refusal=r26Issue}),"P5-26 a with-copy adding a Refusal to a success throws");
  Check(r26Throws(()=>r26BadLoad with {Refusal=null}),"P5-26 a with-copy clearing the Refusal of a refusal throws");
  Check(r26Throws(()=>r26BadLoad with {Registry=r26Reg}),"P5-26 a with-copy adding a Registry to a refusal throws");
  Check(r26Param(()=>r26Ok with {Registry=null})=="Registry" && r26Param(()=>r26Ok with {Refusal=r26Issue})=="Refusal" && r26Param(()=>r26BadLoad with {Refusal=null})=="Refusal" && r26Param(()=>r26BadLoad with {Registry=r26Reg})=="Registry","P5-26 a with-copy violation names the member being assigned as ParamName");
  Check(r26Param(()=>new JurisdictionLoad(null,null))=="Refusal" && r26Param(()=>new JurisdictionLoad(r26Reg,r26Issue))=="Registry","P5-26 constructor violations name Refusal (neither set) and Registry (both set)");
  Check(r26Throws(()=>r26BadLoad with {Registry=r26Reg,Refusal=null}) && r26Throws(()=>r26Ok with {Refusal=r26Issue,Registry=null}),"P5-26 a with-copy switching between success and refusal throws in either assignment order");
  Check(r26One(r26Ok with {Registry=JurisdictionRegistry.Load(r26Empty).Registry}) && r26One(r26BadLoad with {Refusal=r26Issue}) && r26One(r26BadLoad with {Registry=null}),"P5-26 a with-copy keeping exactly one member is accepted");
  // Equality is exactly Registry (reference: a class), Refusal (record value) and LoaderError (reference: an exception).
  var r26Same=r26BadLoad with {};
  Check(r26Same.Equals(r26BadLoad) && r26Same==r26BadLoad && r26Same.GetHashCode()==r26BadLoad.GetHashCode(),"P5-26 a plain copy (same Registry, Refusal and LoaderError) is equal with an equal hash");
  Check(new JurisdictionLoad(null,r26Issue)==new JurisdictionLoad(null,r26Issue with {}) && new JurisdictionLoad(null,r26Issue).GetHashCode()==new JurisdictionLoad(null,r26Issue with {}).GetHashCode(),"P5-26 refusals with equal Refusal and no LoaderError are equal with an equal hash");
  Check(new JurisdictionLoad(r26Reg,null)==new JurisdictionLoad(r26Reg,null) && new JurisdictionLoad(r26Reg,null)!=new JurisdictionLoad(JurisdictionRegistry.Load(r26Empty).Registry,null),"P5-26 successes compare by Registry reference");
  var r26Distinct=r26BadLoad with {Refusal=r26BadLoad.Refusal! with {}};
  Check(r26Distinct.Refusal==r26BadLoad.Refusal && r26Distinct.LoaderError is null && r26BadLoad.LoaderError is not null && !r26Distinct.Equals(r26BadLoad) && r26Distinct!=r26BadLoad,"P5-26 an equal but distinct refusal copy drops LoaderError and is unequal to the original");
  Check(r26Distinct==new JurisdictionLoad(null,r26BadLoad.Refusal! with {}) && r26Distinct.GetHashCode()==new JurisdictionLoad(null,r26BadLoad.Refusal! with {}).GetHashCode(),"P5-26 that copy equals a public refusal with the same Refusal and no LoaderError");
  var r26Again=JurisdictionRegistry.Load(r26Bad);
  Check(r26Again.Refusal==r26BadLoad.Refusal && !ReferenceEquals(r26Again.LoaderError,r26BadLoad.LoaderError) && r26Again!=r26BadLoad,"P5-26 two reads of one bad directory carry distinct loader errors and are unequal");
 } finally { if(Directory.Exists(p526)) Directory.Delete(p526,true); }
}
// P5-27: JurisdictionRegistry equality is reference identity, declared explicitly (IEquatable, Equals, GetHashCode) and
// pinned: two Load (or LoadLocked) reads of one valid directory give equal content but unequal registries, so their loads are
// unequal too (JurisdictionLoad compares Registry by that equality).
{
 var p527=Path.Combine(envRoot(),"bim-p527-"+Guid.NewGuid());
 try {
  var r27Type=typeof(JurisdictionRegistry);
  Check(typeof(IEquatable<JurisdictionRegistry>).IsAssignableFrom(r27Type),"P5-27 JurisdictionRegistry declares IEquatable<JurisdictionRegistry> (equality chosen explicitly)");
  Check(r27Type.GetMethod(nameof(object.Equals),[typeof(object)])?.DeclaringType==r27Type && r27Type.GetMethod(nameof(object.GetHashCode),Type.EmptyTypes)?.DeclaringType==r27Type,"P5-27 JurisdictionRegistry overrides Equals(object) and GetHashCode itself");
  var r27Root=Path.Combine(p527,"skills");
  Directory.CreateDirectory(Path.Combine(r27Root,"xx.p527.pack"));
  File.WriteAllText(Path.Combine(r27Root,"xx.p527.pack","skill.json"),"{\"manifest_version\":1,\"id\":\"xx.p527.pack\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fixture\",\"priority\":10,\"rules\":[]}");
  File.WriteAllText(Path.Combine(r27Root,"xx.p527.pack","jurisdiction.json"),"{\"jurisdiction_version\":1,\"code\":\"xx.p527\",\"pack\":\"xx.p527.pack\",\"country_names\":[\"Fixture Country\"],\"municipality_names\":[\"Fixture Town\"],\"stub\":true}");
  var r27Lock=SkillLocking.Pin(r27Root,["xx.p527.pack"]);
  var r27Pairs=new (string Name,JurisdictionLoad A,JurisdictionLoad B)[] {
   ("Load",JurisdictionRegistry.Load(r27Root),JurisdictionRegistry.Load(r27Root)),
   ("LoadLocked",JurisdictionRegistry.LoadLocked(r27Root,r27Lock),JurisdictionRegistry.LoadLocked(r27Root,r27Lock)),
   ("Load vs LoadLocked",JurisdictionRegistry.Load(r27Root),JurisdictionRegistry.LoadLocked(r27Root,r27Lock)),
  };
  foreach(var (r27Name,r27A,r27B) in r27Pairs) {
   Check(r27A.Registry is {} r27Ra && r27B.Registry is {} r27Rb && r27Ra.Codes.SequenceEqual(r27Rb.Codes) && r27Ra.Claims.Count==1 && r27Rb.Claims.Count==1 && r27Ra.Claims[0].Pack==r27Rb.Claims[0].Pack,"P5-27 "+r27Name+": both reads succeed with the same claims ("+r27A.Refusal?.Code+"/"+r27B.Refusal?.Code+")");
   var r27X=r27A.Registry!; var r27Y=r27B.Registry!;
   Check(!ReferenceEquals(r27X,r27Y) && !r27X.Equals(r27Y) && !r27X.Equals((object)r27Y) && !EqualityComparer<JurisdictionRegistry>.Default.Equals(r27X,r27Y) && !(r27X==r27Y) && r27X!=r27Y,"P5-27 "+r27Name+": two reads give unequal registries (reference identity)");
   Check(r27X.Equals(r27X) && r27X.Equals((object)r27X) && !r27X.Equals((object)"x"),"P5-27 "+r27Name+": a registry equals itself only");
   Check(r27X.GetHashCode()==System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r27X) && r27Y.GetHashCode()==System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r27Y) && r27X.GetHashCode()==r27X.GetHashCode(),"P5-27 "+r27Name+": the hash is the identity hash and stable");
   Check(new HashSet<JurisdictionRegistry>{r27X,r27Y,r27X}.Count==2,"P5-27 "+r27Name+": a hash set keeps both reads");
   Check(r27A!=r27B && !r27A.Equals(r27B),"P5-27 "+r27Name+": the two loads are unequal (JurisdictionLoad follows Registry identity)");
   Check(new JurisdictionLoad(r27X,null)==r27A && new JurisdictionLoad(r27X,null).GetHashCode()==r27A.GetHashCode() && (r27B with {Registry=r27X})==r27A,"P5-27 "+r27Name+": loads over the same registry instance are equal with an equal hash");
   // A separate local: nullable analysis treats x.Equals(null) as a null test of x, so after it x is maybe-null and a later
   // dereference of x is CS8602 (an error under --warnaserror); r27X stays untouched for the checks above.
   var r27Z=r27X;
   Check(!r27Z.Equals(null) && !r27Z.Equals((object?)null),"P5-27 "+r27Name+": a registry is not equal to null");
  }
 } finally { if(Directory.Exists(p527)) Directory.Delete(p527,true); }
}
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {passed=count, core_test_ms=timer.Elapsed.TotalMilliseconds, native_verified=false}));
} catch(Exception exception) {
    Console.Error.WriteLine(exception);
    Environment.ExitCode=1;
}
