using Bim.Evaluation;
var root=Path.Combine(Path.GetTempPath(),"native-oracle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try {
 var options=new RevitExecutionOptions(root,"bim-agentic-os-2025-123",Path.Combine(root,"BIM-Agentic-Test.rvt"),"identity",123,30);
 var missing=await EvaluationService.ExecuteAsync("revit",options);
 if(missing.ProductAcceptance||missing.VerificationState!="blocked"||!missing.Gaps.Any(x=>x.Contains("fixture")))throw new Exception("missing fixture must block");
 var invalid=await EvaluationService.ExecuteAsync("revit",options with {TimeoutSeconds=901});
 if(!invalid.Gaps.Any(x=>x.Contains("timeout")))throw new Exception("bounded timeout required");
 File.WriteAllText(options.DocumentPath,"fixture");
 File.WriteAllText(Path.Combine(root,"fixture-config.json"),"{}");File.WriteAllText(Path.Combine(root,"fixture-result.json"),"{}");
 var malformed=await EvaluationService.ExecuteAsync("revit",options);
 if(malformed.ProductAcceptance||malformed.ExitCode!=2||!malformed.Gaps.Any(x=>x.Contains("prerequisite rejected")))throw new Exception("malformed bootstrap must block");
 File.WriteAllText(Path.Combine(root,"fixture-config.json"),System.Text.Json.JsonSerializer.Serialize(new{ConfirmIsolatedTestProcess=false,RunDirectory=root,FixturePath=options.DocumentPath,ResultPath=Path.Combine(root,"fixture-result.json")}));
 var optout=await EvaluationService.ExecuteAsync("revit",options);
 if(!optout.Gaps.Any(x=>x.Contains("provenance mismatch")))throw new Exception("optout bootstrap accepted");
 var foreign=await EvaluationService.ExecuteAsync("revit",options with {DocumentPath=Path.Combine(Path.GetTempPath(),"BIM-Agentic-Test.rvt")});
 if(!foreign.Gaps.Any(x=>x.Contains("isolated fixture")))throw new Exception("foreign model path accepted");
 var wrongpipe=await EvaluationService.ExecuteAsync("revit",options with {PipeName="bim-agentic-os-2025-124"});
 if(!wrongpipe.Gaps.Any(x=>x.Contains("PID pipe")))throw new Exception("foreign pipe accepted");
 var offline=EvaluationService.ParseOffline("""{"backend":"revit","source":"native","test_a":null,"test_b":null}""");
 if(offline.ProductAcceptance||offline.VerificationState!="unit-tested")throw new Exception("offline promoted");
 var configPath=Path.Combine(root,"fixture-config.json");var resultPath=Path.Combine(root,"fixture-result.json");
 File.WriteAllText(configPath,System.Text.Json.JsonSerializer.Serialize(new{ConfirmIsolatedTestProcess=true,RunDirectory=root,FixturePath=options.DocumentPath,ResultPath=resultPath}));
 var bootstrap=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new{status="succeeded",processId=123,pipeName=options.PipeName,documentPath=options.DocumentPath,documentUniqueId="identity",revitVersion="2025",revitBuild="build"}))!;
 foreach(var field in new[]{"processId","pipeName","documentPath","documentUniqueId","revitVersion","status"}) {
  var changed=bootstrap.DeepClone();changed[field]=field=="processId"?System.Text.Json.Nodes.JsonValue.Create(124):System.Text.Json.Nodes.JsonValue.Create("foreign");
  File.WriteAllText(resultPath,changed.ToJsonString());var rejected=await EvaluationService.ExecuteAsync("revit",options);
  if(rejected.ProductAcceptance||!rejected.Gaps.Any(x=>x.Contains("provenance mismatch")))throw new Exception("bootstrap provenance accepted: "+field);
 }
 var noLinks=typeof(EvaluationService).GetMethod("NoLinks",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
 noLinks.Invoke(null,new object[]{Path.GetFullPath(".")}); // OneDrive cloud ancestors are not links.
 var parse=typeof(EvaluationService).GetMethod("ParseNativeEnvelope",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
 var room=new RoomObservation(1,1,1,1,1,"mm",[],null,null,true,true,options.DocumentPath);
 var revision=new RevisionObservation(room,room,room,room,"",[],[],[],"","",false,false,"","","","");
 var ids=new[]{"native_hosting","native_geometry","native_documentation","native_save_reopen","native_idempotency","native_rollback","native_incremental","native_receipts"};
 var envelope=new{protocol_version=1,backend="revit",backend_version="build",source="actual isolated Revit authoring and reopened readback",observations=new BackendObservations("revit","actual isolated Revit authoring and reopened readback",room,revision),metrics=new Dictionary<string,Metric>(),checks=ids.Select(x=>new CriterionResult(x,"pass","native readback")).ToArray(),artifacts=new[]{"BIM-Agentic-Test.rvt"},errors=Array.Empty<string>()};
 var node=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(envelope,EvaluationService.JsonOptions))!;
 bool Accepted(System.Text.Json.Nodes.JsonNode value,string build="build")=>parse.Invoke(null,new object[]{value.ToJsonString(),build})!=null;
 if(!Accepted(node))throw new Exception("valid protocol parse failed");
 if(Accepted(node,"foreign-build"))throw new Exception("foreign build accepted");
 foreach(var key in new[]{"protocol_version","backend","backend_version","source","observations","checks","metrics","artifacts","errors"}) {
  var clone=node.DeepClone();clone.AsObject().Remove(key);if(Accepted(clone))throw new Exception("missing envelope metadata accepted: "+key);
 }
 var forgedSource=node.DeepClone();forgedSource["source"]="offline mock";forgedSource["observations"]!["source"]="offline mock";if(Accepted(forgedSource))throw new Exception("forged source accepted");
 var duplicate=node.DeepClone();duplicate["checks"]![1]=duplicate["checks"]![0]!.DeepClone();if(Accepted(duplicate))throw new Exception("duplicate native check accepted");
 var unknown=node.DeepClone();unknown["checks"]![0]!["outcome"]="maybe";if(Accepted(unknown))throw new Exception("unknown check outcome accepted");
 var negative=node.DeepClone();negative["metrics"]!["total_ms"]=new System.Text.Json.Nodes.JsonObject{["value"]=-1,["reason"]="measured"};if(Accepted(negative))throw new Exception("negative metric accepted");
 var extra=node.DeepClone();extra["mock_pass"]=true;if(Accepted(extra))throw new Exception("unknown envelope field accepted");
 Console.WriteLine("Native evaluator prerequisite and offline guards passed");
}finally {Directory.Delete(root,true);}





