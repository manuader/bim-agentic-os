using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.DB;
using System.Diagnostics;
using System.Text.Json;
namespace Bim.Revit;

internal sealed class FixtureBootstrap
{
    readonly UIControlledApplication host;
    readonly string configPath;
    readonly Action expose, withdraw;
    FixtureBootstrap(UIControlledApplication host, string path, Action expose, Action withdraw) { this.host = host; configPath = path; this.expose = expose; this.withdraw = withdraw; }
    // expose opens the add-in pipe; it runs only after the config validated and the fixture opened.
    internal static void Attach(UIControlledApplication host, Action expose, Action withdraw)
    {
        var path = Environment.GetEnvironmentVariable("BIM_AGENTIC_TEST_CONFIG");
        if (!string.IsNullOrWhiteSpace(path)) host.Idling += new FixtureBootstrap(host, path, expose, withdraw).OnIdle;
    }
    void OnIdle(object? sender, IdlingEventArgs args)
    {
        host.Idling -= OnIdle;
        FixtureConfig? config = null;
        bool validated = false, exposed = false;
        try {
            config = JsonSerializer.Deserialize<FixtureConfig>(File.ReadAllText(configPath)) ?? throw new ArgumentException("invalid_fixture_config");
            config.Validate(configPath); validated = true;
            var app = sender as UIApplication ?? throw new InvalidOperationException("ui_application_required");
            // A dedicated child process must have no existing document. Never close or modify another document.
            if (app.Application.Documents.Size != 0) throw new InvalidOperationException("isolated_process_has_existing_documents");
            var document = app.Application.NewProjectDocument(config.TemplatePath);
            try {
                using var save = new SaveAsOptions { OverwriteExistingFile = false };
                document.SaveAs(config.FixturePath, save);
            } finally { document.Close(false); } // Only the document created by this invocation.
            var opened = app.OpenAndActivateDocument(config.FixturePath).Document;
            exposed = true; expose();
            FixtureResultPublisher.Publish(config.ResultPath, new { status = "succeeded", documentPath = opened.PathName, documentUniqueId = opened.ProjectInformation.UniqueId, processId = Environment.ProcessId, pipeName = NativeEndpoint.ForProcess(Environment.ProcessId), revitVersion = app.Application.VersionNumber, revitBuild = app.Application.VersionBuild });
        } catch (Exception error) {
            if (exposed) withdraw();
            // Invalid config cannot authorize an output path. Validated failures are durable and never masquerade as success.
            if (validated && config != null) FixtureResultPublisher.TryPublish(config.ResultPath, new { status = "failed", code = error.Message, processId = Environment.ProcessId });
            else Trace.TraceError("BIM fixture bootstrap rejected: {0}", error);
        }
    }
}


