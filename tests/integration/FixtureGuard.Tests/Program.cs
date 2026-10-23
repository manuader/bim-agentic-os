using Bim.Revit;
var root = Path.Combine(Path.GetTempPath(), "fixture-guards-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try {
 var template = Path.Combine(root, "template.rte"); File.WriteAllText(template, "test");
 var valid = new FixtureConfig(true, root, template, Path.Combine(root,"BIM-Agentic-Test.rvt"), Path.Combine(root,"fixture-result.json"));
 valid.Validate(Path.Combine(root,"config.json"));
 void Reject(FixtureConfig c) { try { c.Validate(Path.Combine(root,"config.json")); } catch (ArgumentException) { return; } throw new Exception("Unsafe fixture accepted"); }
 Reject(valid with { ConfirmIsolatedTestProcess = false });
 Reject(valid with { FixturePath = Path.Combine(root,"production.rvt") });
 Reject(valid with { FixturePath = Path.Combine(Path.GetTempPath(),"BIM-Agentic-Test.rvt") });
 File.WriteAllText(valid.FixturePath,"existing"); Reject(valid); File.Delete(valid.FixturePath);
 File.WriteAllText(valid.ResultPath,"existing"); Reject(valid);
 Console.WriteLine("PASS fixture isolation, filename, containment, overwrite guards");
} finally { Directory.Delete(root,true); }
