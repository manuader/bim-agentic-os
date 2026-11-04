using Bim.Core;
// RevitCommandPolicySource (chat-export-save): the MCP server and the CLI re-read .bim/revit-enabled-roots.txt and
// config/revit-content-libraries.txt when either file's existence, size or last-write time changes, so a folder the
// owner enables is usable without restarting Claude. Exercised against a temporary repository folder.
static class PolicyReloadChecks
{
    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
    }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        var repo = Path.Combine(Path.GetTempPath(), "policy-reload-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(repo, "interfaces", "mcp", "bin", "Release", "net8.0");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(repo, ".bim"));
        Directory.CreateDirectory(Path.Combine(repo, "config"));
        File.WriteAllText(Path.Combine(repo, "global.json"), "{}");
        var roots = Path.Combine(repo, ".bim", "revit-enabled-roots.txt");
        var libraries = Path.Combine(repo, "config", "revit-content-libraries.txt");
        var project = Path.Combine(repo, "owner", "ARQ");
        var other = Path.Combine(repo, "owner", "OTR");
        var library = Path.Combine(repo, "libraries", "RVT 2025");
        var nativeRuns = Path.Combine(repo, ".bim", "native-runs");
        try
        {
            var source = new RevitCommandPolicySource(bin);
            Check(source.RepositoryDirectory == repo, "the policy source finds the repository from the binary folder (global.json)", source.RepositoryDirectory ?? "null");
            var initial = source.Current();
            Check(initial.EnabledRoots.SequenceEqual(new[] { nativeRuns }) && initial.RepositoryDirectory == repo && initial.ContentLibraryRoots.Count == 0 && source.Loads == 1,
                "without owner files only .bim/native-runs is enabled", string.Join(";", initial.EnabledRoots));
            Check(ReferenceEquals(source.Current(), initial) && source.Loads == 1, "an unchanged repository is not read again (stat only)");

            File.WriteAllText(roots, "# owner\n" + project + "\n");
            var enabled = source.Current();
            Check(enabled.EnabledRoots.SequenceEqual(new[] { nativeRuns, project }) && source.Loads == 2, "a new .bim/revit-enabled-roots.txt is picked up on the next request, without a restart", string.Join(";", enabled.EnabledRoots));
            Check(RevitCopiesGuard.Check(true, Path.Combine(project, "Cabana", "Cabana.rvt"), enabled) is null, "a document in the newly enabled folder may be written");

            var sameSize = "# owner\n" + other + "\n";
            Check(sameSize.Length == File.ReadAllText(roots).Length, "test setup: same-size edit");
            File.WriteAllText(roots, sameSize);
            File.SetLastWriteTimeUtc(roots, File.GetLastWriteTimeUtc(roots).AddSeconds(5));
            Check(source.Current().EnabledRoots.SequenceEqual(new[] { nativeRuns, other }), "a same-size edit with a new last-write time is picked up");
            var loads = source.Loads;
            File.WriteAllText(roots, "# owner\n" + project + "\n");
            File.SetLastWriteTimeUtc(roots, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check(source.Current().EnabledRoots.SequenceEqual(new[] { nativeRuns, project }) && source.Loads == loads + 1, "an older last-write time is a change too");

            File.WriteAllText(libraries, library + "\n");
            var withLibrary = source.Current();
            Check(withLibrary.ContentLibraryRoots.SequenceEqual(new[] { library }) && withLibrary.FamilySourceRoots.Contains(library), "a new config/revit-content-libraries.txt entry is picked up");

            File.WriteAllText(roots, "relative\\path\n");
            var invalid = Thrown(() => source.Current());
            Check(invalid is { Code: RevitCommandCodes.AllowlistInvalid, Field: "line 1" }, "a malformed owner file is allowlist_invalid (no write enabled)", invalid?.Code + " " + invalid?.Field);
            loads = source.Loads;
            Check(Thrown(() => source.Current()) is { Code: RevitCommandCodes.AllowlistInvalid } && source.Loads == loads, "the refusal is remembered while the file is unchanged");
            File.WriteAllText(roots, project + "\n");
            Check(source.Current().EnabledRoots.SequenceEqual(new[] { nativeRuns, project }), "fixing the owner file enables writes again without a restart");

            File.Delete(roots);
            Check(source.Current().EnabledRoots.SequenceEqual(new[] { nativeRuns }), "deleting the owner file disables the owner folders on the next request");
            File.WriteAllText(libraries, "C:\\\n");
            Check(Thrown(() => source.Current()) is { Code: RevitCommandCodes.AllowlistInvalid }, "a too broad content library entry is allowlist_invalid");
            File.Delete(libraries);
            Check(source.Current().ContentLibraryRoots.Count == 0, "deleting the libraries file is picked up");

            var outside = new RevitCommandPolicySource(Path.GetPathRoot(Path.GetTempPath())!);
            Check(outside.RepositoryDirectory is null && outside.Current().EnabledRoots.Count == 0, "outside a repository nothing is enabled");
        }
        finally { try { Directory.Delete(repo, true); } catch (IOException) { } }
    }
}
