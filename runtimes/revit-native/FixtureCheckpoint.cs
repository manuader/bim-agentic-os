using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Text.Json;
namespace Bim.Revit;

// Only App.Execute's serialized ExternalEvent calls this. No transaction is opened:
// Save/Open/Close require transaction-free documents. Failure preserves every file.
internal static class FixtureCheckpoint
{
    public static string Run(UIApplication app,Request request)
    {
        string stage="authorization";
        bool saved=false, fixtureClosed=false, reopened=false, parkingClosed=false;
        string? parkingPath=null;
        try {
            var configPath=Environment.GetEnvironmentVariable("BIM_AGENTIC_TEST_CONFIG");
            if(string.IsNullOrWhiteSpace(configPath)) throw new InvalidOperationException("checkpoint_test_opt_in_required");
            var config=JsonSerializer.Deserialize<FixtureConfig>(File.ReadAllText(configPath)) ?? throw new InvalidOperationException("invalid_fixture_config");
            var checkpoint=request.Checkpoint ?? throw new ArgumentException("checkpoint_required");
            var batch=request.Batch ?? throw new ArgumentException("batch_required");
            var fixture=app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("no_document");
            FixtureCheckpointGuard.Validate(config,configPath,configPath,checkpoint.ParkingPath,Environment.ProcessId,request.DocumentPath,batch.DocumentIdentity);
            if(!string.Equals(fixture.PathName,config.FixturePath,StringComparison.OrdinalIgnoreCase) || fixture.ProjectInformation.UniqueId!=batch.DocumentIdentity) throw new InvalidOperationException("checkpoint_document_identity_conflict");
            if(app.Application.Documents.Size!=1 || app.Application.Documents.Cast<Document>().Any(d=>!d.Equals(fixture))) throw new InvalidOperationException("checkpoint_dedicated_process_required");
            if(fixture.IsFamilyDocument || fixture.IsReadOnly || fixture.IsModifiable || fixture.IsWorkshared || !fixture.Title.StartsWith("BIM-Agentic-Test",StringComparison.Ordinal)) throw new InvalidOperationException("isolated_test_document_required");
            parkingPath=checkpoint.ParkingPath;
            stage="verify_before_save";
            var before=BatchExecutor.VerifyReceipt(fixture,batch,checkpoint);
            stage="save_fixture";
            fixture.Save(); saved=true;
            stage="create_parking";
            var createdParking=app.Application.NewProjectDocument(config.TemplatePath);
            // This reference was created by this invocation; never close another document.
            stage="save_parking";
            using(var options=new SaveAsOptions{OverwriteExistingFile=false}) createdParking.SaveAs(parkingPath,options);
            stage="close_created_parking";
            if(createdParking.IsModifiable || app.ActiveUIDocument?.Document.Equals(createdParking)==true || !createdParking.Close(false)) throw new InvalidOperationException("checkpoint_parking_close_failed");
            stage="activate_parking";
            var parking=app.OpenAndActivateDocument(parkingPath).Document;
            if(!string.Equals(parking.PathName,parkingPath,StringComparison.OrdinalIgnoreCase) || parking.IsModifiable) throw new InvalidOperationException("checkpoint_parking_activation_failed");
            stage="close_fixture";
            if(app.ActiveUIDocument?.Document.Equals(fixture)==true || fixture.IsModifiable || !fixture.Close(false)) throw new InvalidOperationException("checkpoint_fixture_close_failed");
            fixtureClosed=true;
            stage="reopen_fixture";
            var loaded=app.OpenAndActivateDocument(config.FixturePath).Document;
            reopened=true;
            if(!string.Equals(loaded.PathName,config.FixturePath,StringComparison.OrdinalIgnoreCase) || loaded.ProjectInformation.UniqueId!=batch.DocumentIdentity || loaded.IsReadOnly || loaded.IsWorkshared || loaded.IsModifiable) throw new InvalidOperationException("checkpoint_reopened_identity_or_editability_conflict");
            stage="close_owned_parking";
            if(app.ActiveUIDocument?.Document.Equals(parking)==true || parking.IsModifiable || !parking.Close(false)) throw new InvalidOperationException("checkpoint_parking_close_failed");
            parkingClosed=true;
            stage="verify_reopened_storage";
            // Read a fresh entity from the freshly reopened document. Never execute the batch.
            var persisted=BatchExecutor.VerifyReceipt(loaded,batch,checkpoint);
            if(before.Receipts.Count!=persisted.Receipts.Count || before.Receipts.Any(p=>!persisted.Receipts.TryGetValue(p.Key,out var value) || value!=p.Value)) throw new InvalidOperationException("checkpoint_persisted_receipts_changed");
            return JsonSerializer.Serialize(new {status="succeeded",stage="verified",verification="saved_reopened_native_geometry_readback",saved,reopened,fixtureClosed,parkingClosed,documentPath=loaded.PathName,documentUniqueId=loaded.ProjectInformation.UniqueId,parkingPath,project=persisted.Project,mapping=persisted.Map,receipt=new {key=batch.IdempotencyKey,hash=persisted.Receipts[batch.IdempotencyKey]},documentation="unsupported"});
        } catch(Exception error) {
            // Failure is not rollback: saving or closing may already have succeeded.
            // Leave all remaining documents/files for explicit inspection; no broad cleanup.
            return JsonSerializer.Serialize(new {status="failed",stage,code=error.Message,verification="unverified",saved,reopened,fixtureClosed,parkingClosed,parkingPath,documentation="unsupported"});
        }
    }
}
