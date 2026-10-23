namespace Bim.Revit;

public sealed record FixtureConfig(bool ConfirmIsolatedTestProcess, string RunDirectory, string TemplatePath, string FixturePath, string ResultPath)
{
    public void Validate(string configPath)
    {
        void Require(bool condition, string code) { if (!condition) throw new ArgumentException(code); }
        Require(ConfirmIsolatedTestProcess, "isolated_test_process_confirmation_required");
        Require(Path.IsPathFullyQualified(RunDirectory) && Directory.Exists(RunDirectory), "absolute_existing_run_directory_required");
        string root = Path.GetFullPath(RunDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string path in new[] { configPath, FixturePath, ResultPath }) {
            Require(Path.IsPathFullyQualified(path), "absolute_paths_required");
            Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)) + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase), "paths_must_be_direct_children_of_run_directory");
        }
        Require(Path.IsPathFullyQualified(TemplatePath) && File.Exists(TemplatePath) && Path.GetExtension(TemplatePath).Equals(".rte", StringComparison.OrdinalIgnoreCase), "existing_template_required");
        Require(Path.GetFileName(FixturePath).StartsWith("BIM-Agentic-Test", StringComparison.Ordinal) && Path.GetExtension(FixturePath).Equals(".rvt", StringComparison.OrdinalIgnoreCase), "test_fixture_filename_required");
        Require(!File.Exists(FixturePath) && !Directory.Exists(FixturePath), "fixture_already_exists");
        Require(!File.Exists(ResultPath) && !Directory.Exists(ResultPath), "result_already_exists");
        Require(!string.Equals(configPath, ResultPath, StringComparison.OrdinalIgnoreCase), "result_cannot_replace_config");
    }
}
