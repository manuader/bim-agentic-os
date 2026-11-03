using Bim.Core;
using Bim.Revit;
using System.Text.Json;
// Owner project mode, Autodesk-free half: which opt-in variable selects which bootstrap (AddInGate.StartupMode), the
// project config validation (runtimes/revit-native/ProjectConfig.cs) and the checks on the document Revit opened.
// ProjectBootstrap.cs (the Revit half) only compiles in CI; nothing here has run in Revit.
static class ProjectModeChecks
{
    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";

    static string? Refusal(Action action)
    {
        try { action(); return null; }
        catch (ArgumentException error) { return error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    public static void Run(Action<bool, string, string> report)
    {
        check = report;
        // ---- Exposure: exactly one opt-in variable selects a bootstrap; none or both select nothing ----
        const string Test = @"C:\Repo\.bim\native-runs\run-1\fixture-config.json";
        const string Project = @"C:\Repo\.bim\open-project\sessions\s1\project-config.json";
        Check(AddInGate.ProjectConfigVariable == "BIM_AGENTIC_PROJECT_CONFIG" && AddInGate.ConfigVariable == "BIM_AGENTIC_TEST_CONFIG", "project mode has its own opt-in variable, distinct from the fixture one");
        Check(AddInGate.StartupMode(null, null) is null && AddInGate.StartupMode("", "  ") is null, "no opt-in variable: no bootstrap, no ExternalEvent, no pipe (production Revit)");
        Check(AddInGate.StartupMode(Test, null) == AddInGate.FixtureMode && AddInGate.StartupMode(Test, " ") == AddInGate.FixtureMode, "only the fixture variable selects the fixture bootstrap");
        Check(AddInGate.StartupMode(null, Project) == AddInGate.ProjectMode && AddInGate.StartupMode("", Project) == AddInGate.ProjectMode, "only the project variable selects the project bootstrap");
        Check(AddInGate.StartupMode(Test, Project) is null && AddInGate.ActiveConfig(Test, Project) is null && !AddInGate.PipeAllowed(AddInGate.ActiveConfig(Test, Project), true), "both variables at once are ambiguous: nothing is attached and no pipe may open");
        Check(AddInGate.ActiveConfig(Test, null) == Test && AddInGate.ActiveConfig(null, Project) == Project && AddInGate.ActiveConfig(null, null) is null, "the active config is the selected mode's variable");
        Check(!AddInGate.PipeAllowed(AddInGate.ActiveConfig(null, Project), false) && AddInGate.PipeAllowed(AddInGate.ActiveConfig(null, Project), true), "project mode opens the pipe only after its bootstrap completed");

        var work = Path.Combine(Path.GetTempPath(), "project-mode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = Directory.CreateDirectory(Path.Combine(work, "repo", ".bim", "open-project", "sessions", "s1")).FullName;
            var enabled = Directory.CreateDirectory(Path.Combine(work, "ARQ.ARG")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(work, "Production")).FullName;
            var document = Path.Combine(enabled, "BIM-Agentic-Test.rvt"); File.WriteAllText(document, "rvt");
            var production = Path.Combine(outside, "Tower.rvt"); File.WriteAllText(production, "rvt");
            var configPath = Path.Combine(session, "project-config.json");
            var resultPath = Path.Combine(session, "project-result.json");
            var policy = RevitCommandPolicy.FromAllowlist(enabled + "\n", null);
            var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
            var valid = new ProjectConfig(true, session, document, resultPath);

            Check(Refusal(() => valid.ValidateOutputs(configPath)) is null && Refusal(() => valid.ValidateDocument(policy, noLinks)) is null, "a confirmed config with its result inside the session directory and an enabled document validates");
            Check(Refusal(() => (valid with { ConfirmOwnerProject = false }).ValidateOutputs(configPath)) == "owner_project_confirmation_required", "the config must confirm the owner project mode explicitly");
            Check(Refusal(() => (valid with { SessionDirectory = "relative" }).ValidateOutputs(configPath)) == "absolute_existing_session_directory_required", "a relative session directory is refused");
            Check(Refusal(() => (valid with { SessionDirectory = Path.Combine(work, "missing") }).ValidateOutputs(configPath)) == "absolute_existing_session_directory_required", "a missing session directory is refused");
            Check(Refusal(() => (valid with { ResultPath = Path.Combine(enabled, "project-result.json") }).ValidateOutputs(configPath)) == "paths_must_be_direct_children_of_session_directory", "the result cannot be written next to the owner's project");
            Check(Refusal(() => valid.ValidateOutputs(Path.Combine(work, "project-config.json"))) == "paths_must_be_direct_children_of_session_directory", "a config outside the session directory is refused");
            Check(Refusal(() => (valid with { ResultPath = configPath }).ValidateOutputs(configPath)) == "result_cannot_replace_config", "the result cannot replace the config");
            Check(Refusal(() => (valid with { ResultPath = Path.Combine(session, "BIM-Agentic-Test.rvt"), DocumentPath = Path.Combine(session, "BIM-Agentic-Test.rvt") }).ValidateOutputs(configPath)) == "result_cannot_replace_document", "the result cannot replace the document");
            File.WriteAllText(resultPath, "{}");
            Check(Refusal(() => valid.ValidateOutputs(configPath)) == "result_already_exists", "an existing result is never overwritten");
            File.Delete(resultPath);
            Check(Refusal(() => (valid with { DocumentPath = production }).ValidateDocument(policy, noLinks)) == RevitCommandCodes.DocumentNotEnabled, "a document outside the enabled roots is refused before Revit opens it");
            Check(Refusal(() => (valid with { DocumentPath = Path.Combine(enabled, "Missing.rvt") }).ValidateDocument(policy, noLinks)) == OwnerProjectGuard.Missing, "a missing document is refused");
            Check(Refusal(() => valid.ValidateDocument(RevitCommandPolicy.None, noLinks)) == RevitCommandCodes.DocumentNotEnabled, "an add-in staged outside a repository (no policy) opens nothing");
            Check(Refusal(() => valid.ValidateDocument(policy, new RevitFileSystemProbe(_ => true, _ => null))) == RevitCommandCodes.DocumentLinkRefused, "a link between the enabled root and the document is refused");

            // The script writes the config with ConvertTo-Json: PascalCase members, as FixtureConfig.
            var json = JsonSerializer.Serialize(new { ConfirmOwnerProject = true, SessionDirectory = session, DocumentPath = document, ResultPath = resultPath });
            var parsed = JsonSerializer.Deserialize<ProjectConfig>(json);
            Check(parsed == valid, "the config JSON the script writes reads back as the same record", json);
            var unconfirmed = JsonSerializer.Deserialize<ProjectConfig>(JsonSerializer.Serialize(new { SessionDirectory = session, DocumentPath = document, ResultPath = resultPath }));
            Check(unconfirmed is not null && Refusal(() => unconfirmed.ValidateOutputs(configPath)) == "owner_project_confirmation_required", "a config without the confirmation member is refused");

            // ---- The document Revit actually opened ----
            CommandDocumentFacts Facts(string? path = null, string? uid = Uid, bool readOnly = false, bool family = false, bool workshared = false) => new(path ?? document, uid, readOnly, family, workshared, false);
            Check(Refusal(() => ProjectConfig.CheckOpened(document, Facts())) is null, "the opened document with the requested path and a ProjectInformation UniqueId is accepted");
            var upper = Refusal(() => ProjectConfig.CheckOpened(document, Facts(path: document.ToUpperInvariant())));
            Check(OperatingSystem.IsWindows() ? upper is null : upper == "opened_document_path_mismatch", "the opened path compares like the copies guard (drive paths ignore case, / paths are exact)", upper ?? "accepted");
            Check(Refusal(() => ProjectConfig.CheckOpened(document, Facts(path: production))) == "opened_document_path_mismatch", "Revit opening another path than requested is refused");
            Check(Refusal(() => ProjectConfig.CheckOpened(document, Facts(path: ""))) == "opened_document_path_mismatch", "an unsaved opened document is refused");
            Check(Refusal(() => ProjectConfig.CheckOpened(document, Facts(readOnly: true))) == "document_read_only", "a read-only opened document is refused");
            Check(Refusal(() => ProjectConfig.CheckOpened(document, Facts(family: true))) == "family_document_refused", "a family document is refused");
            Check(Refusal(() => ProjectConfig.CheckOpened(document, Facts(workshared: true))) == "workshared_document_refused", "a workshared document is refused");
            Check(Refusal(() => ProjectConfig.CheckOpened(document, Facts(uid: null))) == "project_identity_missing" && Refusal(() => ProjectConfig.CheckOpened(document, Facts(uid: "not-a-uid"))) == "project_identity_missing", "a document without a ProjectInformation UniqueId is refused");
        }
        catch (Exception error)
        {
            Check(false, "project mode checks completed without an unexpected exception", error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
