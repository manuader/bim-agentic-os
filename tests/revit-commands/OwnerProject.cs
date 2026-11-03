using Bim.Core;
// Owner project mode (scripts/owner-open-project.ps1): OwnerProjectGuard decides whether an owner's real .rvt may be
// opened in a new child Revit with the command pipe. Same enabled roots as the copies-only write guard (repository
// .bim/native-runs plus the owner file .bim/revit-enabled-roots.txt), same link guard, plus the file rules of the
// project mode (an existing .rvt that is not a Revit backup copy). Real temporary files; no Revit involved.
static class OwnerProjectChecks
{
    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    public static void Run(Action<bool, string, string> report)
    {
        check = report;
        var work = Path.Combine(Path.GetTempPath(), "owner-project-" + Guid.NewGuid().ToString("N"));
        try
        {
            var enabled = Directory.CreateDirectory(Path.Combine(work, "ARQ.ARG")).FullName;
            var runs = Directory.CreateDirectory(Path.Combine(work, "repo", ".bim", "native-runs")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(work, "Production")).FullName;
            var sibling = Directory.CreateDirectory(Path.Combine(work, "ARQ.ARG-old")).FullName;
            string Touch(string directory, string name) { var path = Path.Combine(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "rvt"); return path; }
            var project = Touch(enabled, "BIM-Agentic-Test.rvt");
            var nested = Touch(enabled, Path.Combine("Cabana", "Cabana-Zona-Sur.rvt"));
            var upper = Touch(enabled, "Upper.RVT");
            var fixture = Touch(runs, Path.Combine("run-1", "BIM-Agentic-Test.rvt"));
            var production = Touch(outside, "Tower.rvt");
            var siblingProject = Touch(sibling, "Old.rvt");
            var backup = Touch(enabled, "BIM-Agentic-Test.0001.rvt");
            var family = Touch(enabled, "Door.rfa");
            var template = Touch(enabled, "Template.rte");
            var text = Touch(enabled, "notes.txt");
            var folderNamedRvt = Directory.CreateDirectory(Path.Combine(enabled, "Folder.rvt")).FullName;
            var policy = RevitCommandPolicy.FromAllowlist("# owner\n" + enabled + "\n", runs);
            var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
            string? Code(string? path, RevitCommandPolicy? other = null, RevitFileSystemProbe? probe = null) => OwnerProjectGuard.Check(path, other ?? policy, probe ?? noLinks)?.Code;

            Check(Code(project) is null, "an existing .rvt directly under an owner-enabled root may be opened in project mode");
            Check(Code(nested) is null, "an existing .rvt in a folder below an owner-enabled root may be opened");
            Check(Code(upper) is null, "the .rvt extension is matched ignoring case");
            Check(Code(fixture) is null, "a fixture under the repository .bim/native-runs may be reopened in project mode");
            Check(Code(production) == RevitCommandCodes.DocumentNotEnabled, "a document outside every enabled root is document_not_enabled", Code(production) ?? "allowed");
            Check(Code(siblingProject) == RevitCommandCodes.DocumentNotEnabled, "a sibling folder sharing the root's name prefix is not under it (whole segments only)", Code(siblingProject) ?? "allowed");
            Check(Code(project, RevitCommandPolicy.None) == RevitCommandCodes.DocumentNotEnabled, "without any enabled root nothing may be opened");
            Check(Code(null) == RevitCommandCodes.DocumentPathInvalid && Code("") == RevitCommandCodes.DocumentPathInvalid, "a missing path is document_path_invalid");
            Check(Code(Path.Combine("ARQ.ARG", "BIM-Agentic-Test.rvt")) == RevitCommandCodes.DocumentPathInvalid, "a relative path is document_path_invalid");
            Check(Code(enabled + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "Production" + Path.DirectorySeparatorChar + "Tower.rvt") == RevitCommandCodes.DocumentPathInvalid, "a path with .. segments is document_path_invalid (no escape from the root)");
            Check(Code(family) == OwnerProjectGuard.NotRvt && Code(template) == OwnerProjectGuard.NotRvt && Code(text) == OwnerProjectGuard.NotRvt, "a family, a template or any non-.rvt file is project_not_rvt");
            Check(Code(backup) == OwnerProjectGuard.BackupFile, "a Revit backup copy (name.0001.rvt) is project_backup_file", Code(backup) ?? "allowed");
            Check(Code(Path.Combine(enabled, "Missing.rvt")) == OwnerProjectGuard.Missing, "a document that does not exist is project_missing");
            Check(Code(folderNamedRvt) == OwnerProjectGuard.Missing, "a folder named like a .rvt is project_missing");
            var probed = new List<string>();
            var linkedFolder = new RevitFileSystemProbe(path => { probed.Add(path); return path.EndsWith("Cabana", StringComparison.Ordinal); }, _ => null);
            Check(Code(nested, probe: linkedFolder) == RevitCommandCodes.DocumentLinkRefused && probed.Contains(enabled), "a symlink or junction between the enabled root and the document is document_link_refused", string.Join(" | ", probed));
            Check(Code(project, probe: new RevitFileSystemProbe(_ => false, _ => 2)) == RevitCommandCodes.DocumentLinkRefused, "a hard-linked document is document_link_refused");
            var refusal = OwnerProjectGuard.Check(production, policy, noLinks);
            Check(refusal is { Detail.Length: > 0 } && refusal.Detail.Contains(RevitCommandPolicy.EnabledRootsFile, StringComparison.Ordinal), "the not-enabled refusal names the owner file to edit", refusal?.Detail ?? "");
            Check(OwnerProjectGuard.Check(project, policy, noLinks) is null && RevitCopiesGuard.MatchingRoot(project, policy) == enabled, "the allowed document's enabled root is the owner root");
        }
        catch (Exception error)
        {
            Check(false, "owner project checks completed without an unexpected exception", error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
