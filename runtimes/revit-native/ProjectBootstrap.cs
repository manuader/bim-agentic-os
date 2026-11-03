using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Bim.Core;
using System.Diagnostics;
using System.Text.Json;
namespace Bim.Revit;

// Owner project mode bootstrap (BIM_AGENTIC_PROJECT_CONFIG). Like FixtureBootstrap it runs once on the first Idling of
// a dedicated child Revit, but it opens the owner's existing document (validated by ProjectConfig / OwnerProjectGuard
// against this add-in's repository enabled roots) instead of creating a fixture. It never copies, saves or closes a
// document. expose opens the add-in pipe only after the opened document passed ProjectConfig.CheckOpened.
// Revit 2025 API used: UIControlledApplication.Idling, UIApplication.OpenAndActivateDocument(string),
//   Application.Documents, Document.PathName / ProjectInformation / IsReadOnly / IsWorkshared / IsFamilyDocument.
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
internal sealed class ProjectBootstrap
{
    readonly UIControlledApplication host;
    readonly string configPath;
    readonly Action expose, withdraw;
    ProjectBootstrap(UIControlledApplication host, string path, Action expose, Action withdraw) { this.host = host; configPath = path; this.expose = expose; this.withdraw = withdraw; }

    internal static void Attach(UIControlledApplication host, string configPath, Action expose, Action withdraw)
    {
        if (!string.IsNullOrWhiteSpace(configPath)) host.Idling += new ProjectBootstrap(host, configPath, expose, withdraw).OnIdle;
    }

    void OnIdle(object? sender, IdlingEventArgs args)
    {
        host.Idling -= OnIdle;
        ProjectConfig? config = null;
        bool outputs = false, exposed = false;
        try {
            if (!Path.IsPathFullyQualified(configPath)) throw new ArgumentException("absolute_paths_required");
            config = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(configPath)) ?? throw new ArgumentException("invalid_project_config");
            config.ValidateOutputs(configPath); outputs = true;
            config.ValidateDocument(CommandExecutor.Policy(), RevitFileSystemProbe.Real);
            var app = sender as UIApplication ?? throw new InvalidOperationException("ui_application_required");
            // A dedicated child process must have no document yet. Never touch another document.
            if (app.Application.Documents.Size != 0) throw new InvalidOperationException("isolated_process_has_existing_documents");
            var opened = app.OpenAndActivateDocument(config.DocumentPath).Document;
            ProjectConfig.CheckOpened(config.DocumentPath, CommandExecutor.Facts(opened));
            exposed = true; expose();
            FixtureResultPublisher.Publish(config.ResultPath, new { status = "succeeded", mode = AddInGate.ProjectMode, documentPath = opened.PathName, documentUniqueId = opened.ProjectInformation.UniqueId, processId = Environment.ProcessId, pipeName = NativeEndpoint.ForProcess(Environment.ProcessId), revitVersion = app.Application.VersionNumber, revitBuild = app.Application.VersionBuild });
        } catch (Exception error) {
            if (exposed) withdraw();
            // A config whose outputs did not validate cannot authorize a result path. A document left open after a
            // refusal stays open without a pipe; the owner closes it.
            if (outputs && config != null) FixtureResultPublisher.TryPublish(config.ResultPath, new { status = "failed", mode = AddInGate.ProjectMode, code = error.Message, processId = Environment.ProcessId });
            else Trace.TraceError("BIM project bootstrap rejected: {0}", error);
        }
    }
}
