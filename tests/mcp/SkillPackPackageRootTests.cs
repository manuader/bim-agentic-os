using Bim.Cli;

// P5-18: SkillPackLookup.PackageRoot (interfaces/cli/SkillPackLookup.cs, linked into the MCP server and this harness)
// recognises the release package root from either packaged binary directory: <package>/cli (unchanged since P7-12) and
// <package>/mcp (the packaged MCP server), both named ordinally, when the parent holds VERSION, manifest.json and skills/.
// Any other directory name, a differently cased one, or a parent missing a marker or skills/ is no package root (null).
// The real package built by scripts/package.ps1 is exercised end to end in tests/cli/PackageLayoutPackTests.cs.
internal static class SkillPackPackageRootTests
{
    public static void Run(string work, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "skill pack package root: " + name);
        var scratch = Path.Combine(work, "skill-pack-package-root");
        var counter = 0;
        string Package(bool version = true, bool manifest = true, bool skills = true)
        {
            var package = Path.Combine(scratch, (++counter).ToString(System.Globalization.CultureInfo.InvariantCulture), "pkg");
            Directory.CreateDirectory(package);
            if (version) File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0\n");
            if (manifest) File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
            if (skills) Directory.CreateDirectory(Path.Combine(package, "skills"));
            foreach (var binary in new[] { "cli", "mcp" }) Directory.CreateDirectory(Path.Combine(package, binary));
            return package;
        }
        string Show(string? value) => value ?? "<null>";

        var full = Path.GetFullPath(Package());
        foreach (var binary in new[] { "cli", "mcp" })
        {
            var directory = Path.Combine(full, binary);
            Check(SkillPackLookup.PackageRoot(directory) == full, binary + "/ beside VERSION, manifest.json and skills/ is the package root (" + Show(SkillPackLookup.PackageRoot(directory)) + ")");
            Check(SkillPackLookup.PackageRoot(directory + Path.DirectorySeparatorChar) == full, binary + "/ with a trailing separator (AppContext.BaseDirectory form) is the package root (" + Show(SkillPackLookup.PackageRoot(directory + Path.DirectorySeparatorChar)) + ")");
        }
        foreach (var other in new[] { "MCP", "Mcp", "CLI", "mcp2", "xmcp", "tools", "revit", "skills" })
        {
            var root = SkillPackLookup.PackageRoot(Path.Combine(full, other));
            Check(root is null, "a binary directory named " + other + " is no package root (ordinal cli or mcp only) (" + Show(root) + ")");
        }
        foreach (var (label, package) in new[] { ("VERSION", Package(version: false)), ("manifest.json", Package(manifest: false)), ("skills/", Package(skills: false)) })
            foreach (var binary in new[] { "cli", "mcp" })
            {
                var root = SkillPackLookup.PackageRoot(Path.Combine(package, binary));
                Check(root is null, binary + "/ without " + label + " in the parent is no package root (" + Show(root) + ")");
            }
    }
}
