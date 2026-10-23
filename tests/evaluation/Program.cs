using Bim.Evaluation;
try {
var checks = 0; var failures=new List<string>();
void Check(bool ok, string name) { if (!ok) failures.Add(name); checks++; }
var empty = EvaluationService.EvaluateOffline(new("revit", "synthetic", null, null));
Check(empty.ExitCode == 2 && empty.VerificationState == "unit-tested" && empty.Counts.Blocked > 0, "missing observations fail closed");
RoomObservation Complete() {
    var items = new List<ElementObservation> { new("level", "n-level", "Level", null, [], [0]),
      new("west", "n-west", "Wall", "level", [], [0,0,0,4000]), new("east", "n-east", "Wall", "level", [], [5000,0,5000,4000]),
      new("south", "n-south", "Wall", "level", [], [0,0,5000,0]), new("north", "n-north", "Wall", "level", [], [0,4000,5000,4000]),
      new("door", "n-door", "Door", "south", [], [2500,0]), new("w1", "n-w1", "Window", "west", [], [0,2000]), new("w2", "n-w2", "Window", "east", [], [5000,2000]),
      new("room", "n-room", "Room", "level", ["west","east","south","north"], []),
      new("dim", "n-dim", "Dimension", null, ["west","east"], []), new("plan", "n-plan", "Plan", "level", ["dim"], []),
      new("3d", "n-3d", "View3D", null, ["west","east","south","north"], []), new("schedule", "n-schedule", "Schedule", null, ["room"], []),
      new("sheet", "n-sheet", "Sheet", null, ["plan","schedule"], []) };
        items=items.Select(x=>x.Category switch {
        "Wall"=>x with { ThicknessMm=200,HeightMm=3000 }, "Door"=>x with { WidthMm=900 },
        "Dimension"=>x with { WidthMm=5000 }, "Room" or "Schedule"=>x with { AreaM2=20 }, _=>x }).ToList();
    return new(5000,4000,200,3000,900,"mm",items.ToArray(),5000,20,true,true,"fixture.rvt");
}
var good = Complete();
var a = EvaluationService.EvaluateOffline(new("revit","synthetic",good,null));
Check(a.Cases.Single(x=>x.Id=="A").Outcome=="pass", "complete synthetic A checks oracle");
Check(a.VerificationState=="unit-tested", "offline never promotes backend claim");
foreach(var category in new[]{"Door","Room","Window","Dimension","Plan","View3D","Schedule","Sheet"}) {
 var bad=EvaluationService.EvaluateOffline(new("revit","synthetic",good with { Elements=good.Elements.Where(x=>x.Category!=category).ToArray() },null));
 Check(bad.Cases[0].Outcome=="fail", "missing mandatory "+category);
}
Check(EvaluationService.EvaluateOffline(new("revit","historical primitive",null,null)).ExitCode==2,"historical evidence not promoted");
Check(EvaluationService.EvaluateOffline(new("ifc","synthetic",good,null)).Counts.Unsupported>0,"IFC cannot prove native docs");
Check(a.Metrics.Where(x=>x.Key!="total_ms").All(x=>x.Value.Value==null && x.Value.Reason.Length>0),"unknown metrics null with reason");
var after=good with { ClearWidthMm=5400, DimensionWidthMm=5400, ScheduleAreaM2=21.6, Elements=good.Elements.Select(x=>x.LogicalId=="east" ? x with { Geometry=[5400,0,5400,4000] } : x.LogicalId=="w2" ? x with { Geometry=[5400,2000] } : x.LogicalId=="south" ? x with { Geometry=[0,0,5400,0] } : x.LogicalId=="north" ? x with { Geometry=[0,4000,5400,4000] } : x).ToArray() };
after=after with { Elements=after.Elements.Select(x=>x.Category=="Dimension" ? x with { WidthMm=5400 } : x.Category is "Room" or "Schedule" ? x with { AreaM2=21.6 } : x).ToArray() };
var revision=new RevisionObservation(good,after,after,good,"west",["east","w2","south","north","room","dim","schedule"],["east","w2","south","north","room","dim","schedule"],["dim","plan","schedule","sheet"],"key-1","key-1",true,true,"r1","r2","r2","r1");
var full=EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision));
Check(full.ExitCode==0 && full.Cases[1].Outcome=="pass","complete synthetic B");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",null,revision)).ExitCode!=0,"B requires A");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { After=after with { ClearWidthMm=5399 } })).ExitCode==1,"exact 400 mm correction");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { After=after with { Elements=after.Elements.Select(x=>x.LogicalId=="west" ? x with { NativeId="drift" } : x).ToArray() } })).ExitCode==1,"unaffected native IDs preserved");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { SemanticChangedIds=["east"] })).ExitCode==1,"semantic diff exact measured changes");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { Rollback=after })).ExitCode==1,"rollback restores original");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good with { Elements=[..good.Elements,good.Elements[0]] },null)).ExitCode==1,"duplicate identities fail");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good with { ClearWidthMm=double.NaN },null)).ExitCode==1,"nonfinite fails closed");
var blocked=await EvaluationService.ExecuteAsync("revit");
Check(blocked.ExitCode==2 && blocked.VerificationState=="blocked","unimplemented native full A/B blocked");
Check((await EvaluationService.ExecuteAsync("ifc")).ExitCode==2,"missing IFC prerequisite blocked");
Check(EvaluationService.Malformed("bad-json").ExitCode==1,"malformed fails closed");
Check(!full.ProductAcceptance && full.EvidenceOrigin=="offline-observations", "offline oracle pass is not product acceptance");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { After=after with { Elements=good.Elements }, Retry=after with { Elements=good.Elements }, AllowedChangedIds=[],SemanticChangedIds=[] })).ExitCode==1,"width assertion without measured geometry correction fails");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { After=after with { Elements=after.Elements.Where(x=>x.Category!="Room").ToArray() } })).ExitCode==1,"B must retain complete room and documentation");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good with { Elements=good.Elements.Select(x=>x.LogicalId=="w2" ? x with { Geometry=[9000,2000] } : x).ToArray() },null)).ExitCode==1,"hosted opening must lie on host");
Check(EvaluationService.ParseOffline("{broken").ExitCode==1,"JSON syntax malformed fails closed");
Check(EvaluationService.ParseOffline("{\"backend\":\"revit\",\"source\":\"x\",\"verification_state\":\"verified-in-revit\"}").ExitCode==1,"unknown provenance property rejected");
Check((await EvaluationService.ExecuteAsync("unknown")).ExitCode==2,"unsupported backend blocked exit");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good with { Elements=good.Elements.Select(x=>x.Category=="Dimension" ? x with { References=["west","west"] } : x).ToArray() },null)).Cases[0].Outcome=="fail","dimension must reference distinct opposite width boundaries");
var displaced=after with { Elements=after.Elements.Select(x=>x.LogicalId=="door" ? x with { Geometry=[100,0] } : x).ToArray() };
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { After=displaced,Retry=displaced,AllowedChangedIds=[..revision.AllowedChangedIds,"door"],SemanticChangedIds=[..revision.SemanticChangedIds,"door"] })).ExitCode==1,"caller cannot authorize unrelated door displacement");
var swapped=after with { Elements=after.Elements.Select(x=>x.LogicalId=="door" ? x with { Category="Window" } : x.LogicalId=="w1" ? x with { Category="Door", HostId="south", Geometry=[1000,0] } : x).ToArray() };
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { After=swapped,Retry=swapped,AllowedChangedIds=[..revision.AllowedChangedIds,"door","w1"],SemanticChangedIds=[..revision.SemanticChangedIds,"door","w1"] })).ExitCode==1,"per-ID semantic category retained");
Check(EvaluationService.EvaluateOffline(new("ifc","synthetic",good with { DimensionWidthMm=0 },null)).ExitCode==1,"IFC unsupported does not erase measured violation");

Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good with { Elements=good.Elements.Select(x=>x.Category=="Wall" ? x with { ThicknessMm=150 } : x).ToArray() },null)).ExitCode==1,"individual wall measured thickness cannot hide behind aggregate");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good with { Elements=good.Elements.Select(x=>x.Category=="Door" ? x with { WidthMm=800 } : x).ToArray() },null)).ExitCode==1,"individual door measured width required");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good with { Elements=good.Elements.Select(x=>x.Category=="Room" ? x with { AreaM2=null } : x).ToArray() },null)).Cases[0].Counts.Blocked>0,"missing Room area is unknown and blocked");
Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good with { Elements=good.Elements.Select(x=>x.Category=="Dimension" ? x with { WidthMm=0 } : x).ToArray() },null)).ExitCode==1,"actual dimension readback required");

Check((await EvaluationService.ExecuteAsync("ifc")).Source.Contains("no runtime probe"),"IFC capability declaration does not claim runtime inspection");
Check((await EvaluationService.ExecuteAsync("revit")).Source.Contains("no process probe"),"Revit capability declaration does not claim process inspection");

Check(EvaluationService.EvaluateOffline(new("revit","synthetic",good,revision with { RevisionAfter=null!,RetryRevision=null! })).ExitCode==1,"missing after/retry revision tokens fail closed");
var portable=good with { NativeFile="fixture.ifc", Elements=good.Elements.Where(x=>x.Category is not ("Dimension" or "Plan" or "View3D" or "Schedule" or "Sheet")).ToArray() };
var portableResult=EvaluationService.EvaluateOffline(new("ifc","synthetic",portable,null));
Check(portableResult.Cases[0].Counts.Failed==0,"absent IFC native outputs unsupported rather than failed");
Check(!portableResult.ProductAcceptance,"IFC portable observations never full product acceptance");
var missingWorker=await EvaluationService.ExecuteAsync("ifc",new IfcExecutionOptions("missing-python","missing-worker",Path.GetTempPath()));
Check(missingWorker.ExitCode==2 && !missingWorker.BackendAcceptance && missingWorker.VerificationState=="blocked","missing IFC process inputs block without promotion");
object? ParseEnvelope(string json) => typeof(EvaluationService).GetMethod("ParseIfcEnvelope",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)?.Invoke(null,[json]);
var workerJson=System.Text.Json.JsonSerializer.Serialize(new {protocol_version=1,backend="ifc",backend_version="test-only",source="synthetic",observations=new BackendObservations("ifc","synthetic",good,revision),metrics=new Dictionary<string,Metric>(),checks=new[]{"ifc_schema","ifc_geometry","ifc_hosting","ifc_save_reopen","ifc_idempotency","ifc_rollback","ifc_incremental","ifc_receipts"}.Select(id=>new CriterionResult(id,"pass","test-only")).ToArray(),artifacts=new[]{"test.ifc"},errors=Array.Empty<string>()},EvaluationService.JsonOptions);
Check(ParseEnvelope(workerJson)!=null,"typed worker protocol validator accepts complete shape without issuing provenance");
Check(ParseEnvelope("{broken")==null,"malformed worker JSON rejected");
Check(ParseEnvelope(workerJson.Replace("\"protocol_version\": 1","\"protocol_version\": 2"))==null,"unsupported protocol rejected");
Check(ParseEnvelope(workerJson.Replace("ifc_receipts","ifc_geometry"))==null,"duplicate and missing worker checks rejected");
Check(ParseEnvelope(workerJson.Replace("\"metrics\": {}","\"metrics\": {\"total_ms\":{\"value\":-1,\"reason\":\"measured\"}}"))==null,"negative metric rejected");
Check(ParseEnvelope(workerJson.Replace("\"metrics\": {}","\"metrics\": {\"total_ms\":{\"value\":1e999,\"reason\":\"measured\"}}"))==null,"nonfinite metric rejected");
Check(ParseEnvelope(workerJson.Replace("\"metrics\": {}","\"metrics\": {\"unknown_ms\":{\"value\":1,\"reason\":\"measured\"}}"))==null,"unknown metric rejected");
Check(ParseEnvelope(workerJson.Replace("\"backend_version\": \"test-only\"","\"backend_version\": null"))==null,"missing runtime version rejected");
var waitPort=typeof(EvaluationService).GetMethod("WaitForOwnedProcessAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
Check(waitPort!=null,"owned process timeout port exists");
if(waitPort!=null) {
    var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false};
    // The dotnet host waits for input when its executable is a shell only; use the platform shell explicitly.
    start.FileName=OperatingSystem.IsWindows()?"C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe":"/bin/sh";
    if(OperatingSystem.IsWindows()){start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-Command");start.ArgumentList.Add("Start-Sleep -Seconds 30");}
    else {start.ArgumentList.Add("-c");start.ArgumentList.Add("sleep 30");}
    using var child=System.Diagnostics.Process.Start(start)!;
    using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    var cancelled=false;
    try {await (Task)waitPort.Invoke(null,[child,Task.CompletedTask,Task.CompletedTask,deadline.Token])!;}catch(OperationCanceledException){cancelled=true;}
    Check(cancelled&&child.WaitForExit(3000),"timeout cancels and terminates only owned child");
}
var processAgreement=typeof(EvaluationService).GetMethod("ProcessResultConsistent",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);
bool? Agrees(int code,bool failed)=>(bool?)processAgreement?.Invoke(null,[code,failed]);
Check(Agrees(0,false)==true && Agrees(1,true)==true,"successful and measured failed subprocess outcomes agree");
Check(Agrees(1,false)==false && Agrees(0,true)==false,"nonzero exit and check disagreement rejected");
if(failures.Count>0) throw new Exception(string.Join("; ",failures));
Console.WriteLine($"PASS {checks} evaluator assertions (synthetic unit observations only)");

} catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode=1; }
