using Bim.Core;
namespace Bim.Revit;

// Owner project mode config (BIM_AGENTIC_PROJECT_CONFIG, written by scripts/owner-open-project.ps1). Autodesk-free on
// purpose: tests/integration/RevitCommandGuard.Tests source-links this file; ProjectBootstrap.cs applies it. Unlike
// FixtureConfig it creates nothing: it names an existing owner document that OwnerProjectGuard allows (an enabled
// root of this add-in's repository), opened as is, never copied, saved or closed by the add-in.
public sealed record ProjectConfig(bool ConfirmOwnerProject, string SessionDirectory, string DocumentPath, string ResultPath)
{
    static void Require(bool condition, string code) { if (!condition) throw new ArgumentException(code); }

    // First: the outputs. Only a config that passes this may publish a (failed) result file.
    public void ValidateOutputs(string configPath)
    {
        Require(ConfirmOwnerProject, "owner_project_confirmation_required");
        Require(!string.IsNullOrEmpty(SessionDirectory) && Path.IsPathFullyQualified(SessionDirectory) && Directory.Exists(SessionDirectory), "absolute_existing_session_directory_required");
        string root = Path.GetFullPath(SessionDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string? path in new[] { configPath, ResultPath }) {
            Require(!string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path), "absolute_paths_required");
            Require(string.Equals(Path.GetDirectoryName(Path.GetFullPath(path!)) + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase), "paths_must_be_direct_children_of_session_directory");
        }
        Require(!string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(ResultPath), StringComparison.OrdinalIgnoreCase), "result_cannot_replace_config");
        Require(string.IsNullOrEmpty(DocumentPath) || !Path.IsPathFullyQualified(DocumentPath) || !string.Equals(Path.GetFullPath(DocumentPath), Path.GetFullPath(ResultPath), StringComparison.OrdinalIgnoreCase), "result_cannot_replace_document");
        Require(!File.Exists(ResultPath) && !Directory.Exists(ResultPath), "result_already_exists");
    }

    // Then: the document, with the same enabled roots and link guard as catalogue writes.
    public void ValidateDocument(RevitCommandPolicy policy, RevitFileSystemProbe probe)
    {
        if (OwnerProjectGuard.Check(DocumentPath, policy, probe) is { } refusal) throw new ArgumentException(refusal.Code);
    }

    // The document Revit actually opened must be the requested saved file, writable, a plain (non-workshared) project
    // with a ProjectInformation UniqueId: what the executor's write admission needs. Otherwise no pipe opens.
    public static void CheckOpened(string requestedPath, CommandDocumentFacts opened)
    {
        Require(opened.PathName is { Length: > 0 } path && CommandAdmission.SamePath(path, requestedPath), "opened_document_path_mismatch");
        Require(!opened.IsFamilyDocument, "family_document_refused");
        Require(!opened.IsWorkshared, "workshared_document_refused");
        Require(!opened.IsReadOnly, "document_read_only");
        Require(opened.ProjectUniqueId is { } uid && RevitCommandProtocol.IsUniqueId(uid), "project_identity_missing");
    }
}
