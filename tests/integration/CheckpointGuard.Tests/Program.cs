using Bim.Core;
using Bim.Revit;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "checkpoint-guards-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try {
    var fixture = Path.Combine(root, "BIM-Agentic-Test.rvt");
    var template = Path.Combine(root, "template.rte");
    var configPath = Path.Combine(root, "config.json");
    var resultPath = Path.Combine(root, "fixture-result.json");
    foreach (var path in new[] { fixture, template, configPath }) File.WriteAllText(path, "owned test file");
    var config = new FixtureConfig(true, root, template, fixture, resultPath);
    var checkpoint = new FixtureCheckpointRequest(Path.Combine(root, "BIM-Agentic-Test-parking.rvt"), 2, new());
    File.WriteAllText(resultPath, JsonSerializer.Serialize(new { status="succeeded", documentPath=fixture, documentUniqueId="fixture-uid", processId=1234, pipeName="bim-agentic-os-2025-1234" }));
    void Reject(Action action, string code) {
        try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) {
            if (error.Message != code) throw new Exception($"Expected {code}, got {error.Message}");
            Console.WriteLine("PASS " + code); return;
        }
        throw new Exception("Unsafe checkpoint accepted: " + code);
    }
    FixtureCheckpointGuard.Validate(config, configPath, configPath, checkpoint.ParkingPath, 1234, fixture, "fixture-uid");
    Console.WriteLine("PASS existing owned fixture accepted without bootstrap overwrite rule");
    Reject(() => FixtureCheckpointGuard.Validate(config, configPath, null, checkpoint.ParkingPath, 1234, fixture, "fixture-uid"), "checkpoint_test_opt_in_required");
    Reject(() => FixtureCheckpointGuard.Validate(config with { ConfirmIsolatedTestProcess=false }, configPath, configPath, checkpoint.ParkingPath, 1234, fixture, "fixture-uid"), "isolated_test_process_confirmation_required");
    Reject(() => FixtureCheckpointGuard.Validate(config, configPath, configPath, Path.Combine(root,"nested","BIM-Agentic-Test-parking.rvt"), 1234, fixture, "fixture-uid"), "checkpoint_paths_must_be_direct_children");
    Reject(() => FixtureCheckpointGuard.Validate(config, configPath, configPath, fixture, 1234, fixture, "fixture-uid"), "checkpoint_parking_already_exists");
    Reject(() => FixtureCheckpointGuard.Validate(config, configPath, configPath, checkpoint.ParkingPath, 4321, fixture, "fixture-uid"), "checkpoint_process_ownership_conflict");
    Reject(() => FixtureCheckpointGuard.Validate(config, configPath, configPath, checkpoint.ParkingPath, 1234, Path.Combine(root,"BIM-Agentic-Test-other.rvt"), "fixture-uid"), "checkpoint_document_identity_conflict");
    Reject(() => FixtureCheckpointGuard.Validate(config, configPath, configPath, checkpoint.ParkingPath, 1234, fixture, "other-uid"), "checkpoint_document_identity_conflict");
    File.WriteAllText(checkpoint.ParkingPath, "must survive");
    Reject(() => FixtureCheckpointGuard.Validate(config, configPath, configPath, checkpoint.ParkingPath, 1234, fixture, "fixture-uid"), "checkpoint_parking_already_exists");
    if (File.ReadAllText(checkpoint.ParkingPath) != "must survive") throw new Exception("Parking overwritten");

    var id=Guid.Parse("11111111-1111-1111-1111-111111111111");
    var element=new Bim.Core.Element(id,"level",[0],[],new("test","approved"));
    var before=new Project(Guid.NewGuid(),1,[]);
    var after=before with {Elements=[element]};
    var batch=new NativeBatchRequest("fixture-uid","fixture-resize",before,after,Compiler.Diff(before,after));
    var persisted=after with {Revision=2};
    var map=new Dictionary<Guid,string>{{id,"native-level"}};
    var receipts=new Dictionary<string,string>{{"fixture-resize",batch.Plan.AfterHash}};
    BatchReceiptGuard.Verify(batch,persisted,map,receipts,2,map);
    Console.WriteLine("PASS persisted receipt, exact revision and mapping verified read-only");
    Reject(() => BatchReceiptGuard.Verify(batch,persisted,map,new(),2,map), "checkpoint_receipt_missing");
    Reject(() => BatchReceiptGuard.Verify(batch,persisted,map,new(){{"fixture-resize","wrong"}},2,map), "checkpoint_receipt_conflict");
    Reject(() => BatchReceiptGuard.Verify(batch,persisted with {Revision=3},map,receipts,2,map), "checkpoint_revision_conflict");
    Reject(() => BatchReceiptGuard.Verify(batch,persisted with {Elements=[element with {GeometryMetres=[1]}]},map,receipts,2,map), "checkpoint_snapshot_conflict");
    Reject(() => BatchReceiptGuard.Verify(batch,persisted,map,receipts,2,new(){{id,"changed-uid"}}), "checkpoint_mapping_conflict");
    Reject(() => BatchReceiptGuard.Verify(batch,persisted,new(),receipts,2,new()), "checkpoint_mapping_conflict");
    Reject(() => BatchReceiptGuard.Verify(batch with {Plan=batch.Plan with {Operations=[]}},persisted,map,receipts,2,map), "checkpoint_invalid_batch");
    if(persisted.Revision!=2 || map[id]!="native-level" || receipts.Count!=1) throw new Exception("Read-only receipt check mutated inputs");
} finally {
    var target=Path.GetFullPath(root);
    if(Path.GetDirectoryName(target)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(target).StartsWith("checkpoint-guards-",StringComparison.Ordinal)) throw new Exception("Unsafe cleanup target");
    Directory.Delete(target,true);
}
