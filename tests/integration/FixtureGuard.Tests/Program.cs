using Bim.Revit;
var root = Path.Combine(Path.GetTempPath(), "fixture-guards-" + Guid.NewGuid());
Directory.CreateDirectory(root);
if(NativeEndpoint.ForProcess(1234)!="bim-agentic-os-2025-1234") throw new Exception("Wrong process endpoint");
try { NativeEndpoint.ForProcess(0); throw new Exception("Zero PID accepted"); } catch(ArgumentOutOfRangeException) {}
try {
 var template = Path.Combine(root, "template.rte"); File.WriteAllText(template, "test");
 var valid = new FixtureConfig(true, root, template, Path.Combine(root,"BIM-Agentic-Test.rvt"), Path.Combine(root,"fixture-result.json"));
 valid.Validate(Path.Combine(root,"config.json"));
 void Reject(FixtureConfig c) { try { c.Validate(Path.Combine(root,"config.json")); } catch (ArgumentException) { return; } throw new Exception("Unsafe fixture accepted"); }
 Reject(valid with { ResultPath = valid.FixturePath });
 Reject(valid with { ConfirmIsolatedTestProcess = false });
 Reject(valid with { FixturePath = Path.Combine(root,"production.rvt") });
 Reject(valid with { FixturePath = Path.Combine(Path.GetTempPath(),"BIM-Agentic-Test.rvt") });
 File.WriteAllText(valid.FixturePath,"existing"); Reject(valid); File.Delete(valid.FixturePath);
 File.WriteAllText(valid.ResultPath,"existing"); Reject(valid);
 var result = Path.Combine(root,"publication.json");
 var started=new ManualResetEventSlim(); var release=new ManualResetEventSlim();
 var publishing=Task.Run(()=>FixtureResultPublisher.Publish(result,new PausedResult(started,release)));
 if(!started.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Serialization did not start");
 try { if(File.Exists(result)) throw new Exception("Result visible before serialization complete"); }
 finally { release.Set(); }
 publishing.GetAwaiter().GetResult();
 using (var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(result))) {
   if(json.RootElement.GetProperty("payload").GetString()!.Length!=100000) throw new Exception("Incomplete result");
 }
 if(FixtureResultPublisher.TryPublish(result,new {status="failed"})) throw new Exception("Existing result overwritten");
 if(FixtureResultPublisher.TryPublish(Path.Combine(root,"missing","failure.json"),new {status="failed"})) throw new Exception("Invalid output accepted");
 if(Directory.GetFiles(root,"*.tmp").Length!=0) throw new Exception("Temporary result leaked");
 Console.WriteLine("PASS fixture isolation, filename, containment, overwrite guards");
} finally { var target=Path.GetFullPath(root); var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar; if(!target.StartsWith(parent,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("fixture-guards-",StringComparison.Ordinal)) throw new Exception("Unsafe cleanup target"); Directory.Delete(target,true); }

sealed class PausedResult(ManualResetEventSlim started, ManualResetEventSlim release)
{
 public string payload { get { started.Set(); if(!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Publication timed out"); return new string('x',100000); } }
}

