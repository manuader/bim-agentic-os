using System.Text.Json;
namespace Bim.Revit;

public sealed record FixtureCheckpointRequest(string ParkingPath, int ExpectedRevision, Dictionary<Guid,string> ExpectedMapping);

// Pure authorization checks shared with host tests. Bootstrap's no-existing-fixture rule
// intentionally does not apply: checkpoint saves the exact fixture owned by this child.
public static class FixtureCheckpointGuard
{
    public static void Validate(FixtureConfig config, string configPath, string? optedInConfigPath, string parkingPath, int processId, string documentPath, string documentIdentity)
    {
        void Require(bool condition, string code) { if(!condition) throw new InvalidOperationException(code); }
        bool Same(string? left,string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right) && string.Equals(Path.GetFullPath(left),Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);
        Require(Same(configPath,optedInConfigPath),"checkpoint_test_opt_in_required");
        Require(config.ConfirmIsolatedTestProcess,"isolated_test_process_confirmation_required");
        Require(Path.IsPathFullyQualified(config.RunDirectory) && Directory.Exists(config.RunDirectory),"absolute_existing_run_directory_required");
        var root=Path.GetFullPath(config.RunDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach(var path in new[]{configPath,config.FixturePath,config.ResultPath,parkingPath})
            Require(Path.IsPathFullyQualified(path) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),root,StringComparison.OrdinalIgnoreCase),"checkpoint_paths_must_be_direct_children");
        // Reject directory junctions/symlinks at the owned run and input/output paths.
        Require((File.GetAttributes(root)&FileAttributes.ReparsePoint)==0,"checkpoint_reparse_path_rejected");
        foreach(var path in new[]{configPath,config.FixturePath,config.ResultPath}) {
            Require(File.Exists(path),"checkpoint_existing_owned_inputs_required");
            Require((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"checkpoint_reparse_path_rejected");
        }
        bool TestRvt(string path) => Path.GetFileName(path).StartsWith("BIM-Agentic-Test",StringComparison.Ordinal) && Path.GetExtension(path).Equals(".rvt",StringComparison.OrdinalIgnoreCase);
        Require(TestRvt(config.FixturePath) && TestRvt(parkingPath),"checkpoint_test_fixture_filename_required");
        Require(!File.Exists(parkingPath) && !Directory.Exists(parkingPath),"checkpoint_parking_already_exists");
        Require(!Same(parkingPath,configPath) && !Same(parkingPath,config.ResultPath),"checkpoint_parking_path_conflict");
        Require(Path.IsPathFullyQualified(config.TemplatePath) && File.Exists(config.TemplatePath) && Path.GetExtension(config.TemplatePath).Equals(".rte",StringComparison.OrdinalIgnoreCase),"existing_template_required");
        using var result=JsonDocument.Parse(File.ReadAllText(config.ResultPath));
        var owned=result.RootElement;
        Require(owned.GetProperty("status").GetString()=="succeeded" && owned.GetProperty("processId").GetInt32()==processId && owned.GetProperty("pipeName").GetString()==NativeEndpoint.ForProcess(processId),"checkpoint_process_ownership_conflict");
        Require(Same(documentPath,config.FixturePath) && Same(owned.GetProperty("documentPath").GetString(),config.FixturePath) && !string.IsNullOrWhiteSpace(documentIdentity) && owned.GetProperty("documentUniqueId").GetString()==documentIdentity,"checkpoint_document_identity_conflict");
    }
}
