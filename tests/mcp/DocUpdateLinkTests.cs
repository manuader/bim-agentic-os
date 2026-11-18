using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// P4-22: the MCP server binary carries the doc-update dry plan only, never its transport or execute code. The read-only
// tool doc_update (interfaces/mcp/DocUpdateTool.cs) needs DocUpdateCommand.DryPlan, which lives in
// interfaces/cli/DocUpdateDryPlan.cs; the probe (DocUpdateProbe.cs: the Revit pipe channel, the enabled-roots policy) and
// the run (DocUpdateExecute.cs: the writes of --execute) stay in the CLI.
// Oracles (static, robust to formatting):
//   1. Bim.Mcp.csproj and tests/mcp/Bim.Mcp.Tests.csproj, parsed as XML (comments ignored), compile no item whose Include
//      (each ';' entry, '\' or '/' separators, any case, wildcards expanded against the disk) or Link names
//      DocUpdateProbe.cs or DocUpdateExecute.cs; they compile interfaces/cli/DocUpdateDryPlan.cs and not
//      interfaces/cli/DocUpdateCommand.cs (the verb with the pipe and the run); Bim.Mcp.csproj references no interfaces/cli
//      project.
//   2. No source Bim.Mcp.csproj compiles (its linked items and the default glob of interfaces/mcp) uses DocUpdateProbe.
//      or DocUpdateExecute. in code: comments are stripped by a small lexer that keeps string, verbatim, raw and char
//      literals intact, so "//" inside a string never hides code after it.
//   3. A self-test of the checker on synthetic csproj and source texts (formatting, comments, wildcards, literals).
internal static class DocUpdateLinkTests
{
    static readonly string[] ForbiddenFiles = ["DocUpdateProbe.cs", "DocUpdateExecute.cs"];
    static readonly Regex ForbiddenUse = new(@"\bDocUpdate(?:Probe|Execute)\s*\.", RegexOptions.CultureInvariant);
    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Every Compile item of a project text: (the Include entry, its Link, the files it resolves to against projectDir).
    internal static List<(string Include, string? Link, string[] Files)> Compiled(string projectXml, string projectDir) =>
        XDocument.Parse(projectXml).Descendants().Where(e => e.Name.LocalName == "Compile")
            .SelectMany(e => ((string?)e.Attribute("Include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(include => (Include: include, Link: (string?)e.Attribute("Link"), Files: Resolve(projectDir, include))))
            .ToList();

    // The files an Include entry names: the one full path, or the files a wildcard entry matches on disk.
    static string[] Resolve(string projectDir, string include)
    {
        var relative = include.Replace('\\', '/');
        if (relative.IndexOfAny(['*', '?']) < 0) return [Path.GetFullPath(Path.Combine(projectDir, relative.Replace('/', Path.DirectorySeparatorChar)))];
        var segments = relative.Split('/');
        var first = Array.FindIndex(segments, s => s.IndexOfAny(['*', '?']) >= 0);
        var baseDir = Path.GetFullPath(Path.Combine(projectDir, string.Join(Path.DirectorySeparatorChar, segments.Take(first))));
        var recursive = segments.Skip(first).Take(segments.Length - first - 1).Any() || segments[first] == "**";
        var pattern = segments[^1] == "**" ? "*" : segments[^1];
        return Directory.Exists(baseDir) ? Directory.GetFiles(baseDir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly) : [];
    }

    static bool IsForbidden(string? path) =>
        path is not null && ForbiddenFiles.Any(f => string.Equals(Path.GetFileName(path.Replace('\\', '/').Split('/')[^1]), f, StringComparison.OrdinalIgnoreCase));

    // The Compile entries of a project text that link the probe or the execute source (by Include, a matched file or Link).
    internal static string[] ForbiddenLinks(string projectXml, string projectDir) =>
        Compiled(projectXml, projectDir).Where(c => IsForbidden(c.Include) || IsForbidden(c.Link) || c.Files.Any(IsForbidden))
            .Select(c => c.Include + (c.Link is null ? "" : " (Link " + c.Link + ")")).ToArray();

    // C# source with comments removed and every literal kept: // and /* */ outside literals; "..." with \ escapes,
    // @"..." with "" escapes, raw """...""" (any run of three or more quotes), and '...' char literals. Interpolated
    // literals ($"...", $@"...", $"""...""") are kept whole, holes included, so code in a hole is still scanned.
    internal static string CodeOf(string text)
    {
        var output = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '/' && next == '*') { var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = close < 0 ? text.Length : close + 2; output.Append(' '); continue; }
            var start = i;
            if (c == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'' && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
                i = Math.Min(text.Length, i + 1);
            }
            else if (c == '"' && Verbatim(text, i))
            {
                i++;
                while (i < text.Length && !(text[i] == '"' && (i + 1 >= text.Length || text[i + 1] != '"'))) i += text[i] == '"' ? 2 : 1;
                i = Math.Min(text.Length, i + 1);
            }
            else if (c == '"')
            {
                var run = 0;
                while (i + run < text.Length && text[i + run] == '"') run++;
                if (run >= 3)
                {
                    var close = text.IndexOf(new string('"', run), i + run, StringComparison.Ordinal);
                    i = close < 0 ? text.Length : close + run;
                }
                else
                {
                    i++;
                    while (i < text.Length && text[i] != '"' && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
                    i = Math.Min(text.Length, i + 1);
                }
            }
            else i++;
            output.Append(text, start, Math.Min(i, text.Length) - start);
        }
        return output.ToString();
    }

    // A quote opens a verbatim literal when an '@' precedes it, directly or before its '$' run (@"", $@"", @$"").
    static bool Verbatim(string text, int quote)
    {
        var j = quote - 1;
        while (j >= 0 && text[j] == '$') j--;
        return j >= 0 && text[j] == '@';
    }

    // The uses of DocUpdateProbe. or DocUpdateExecute. in the code (not the comments) of a source.
    internal static string[] ForbiddenUses(string source) => ForbiddenUse.Matches(CodeOf(source)).Select(m => Regex.Replace(m.Value, @"\s+", "")).ToArray();

    public static void Run(string root, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "doc_update link: " + name);
        var mcpDir = Path.Combine(root, "interfaces", "mcp");
        var cliDir = Path.Combine(root, "interfaces", "cli");

        // ---- 3. self-test of the checker ----
        string Project(string items) => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>";
        foreach (var (label, items, expected) in new (string, string, bool)[]
        {
            ("a multi-line item with '\\' separators", "    <Compile\n      Include=\"..\\cli\\DocUpdateExecute.cs\"\n      Link=\"DocUpdateExecute.cs\" />", true),
            ("an item in an XML comment", "    <!-- <Compile Include=\"../cli/DocUpdateProbe.cs\" Link=\"DocUpdateProbe.cs\" /> -->\n    <Compile Include=\"../cli/DocUpdateDryPlan.cs\" />", false),
            ("the second of two ';' entries", "<Compile Include=\"../cli/BoundedInput.cs;  ../cli/DocUpdateProbe.cs\" />", true),
            ("another case", "<Compile Include=\"../CLI/docupdateexecute.CS\" />", true),
            ("a forbidden Link only", "<Compile Include=\"../cli/Other.cs\" Link=\"Sub/DocUpdateProbe.cs\" />", true),
            ("a wildcard matching the CLI sources", "<Compile Include=\"../cli/DocUpdate*.cs\" />", true),
            ("a recursive wildcard", "<Compile Include=\"../**/*.cs\" />", true),
            ("the dry-plan source only", "<Compile Include=\"../cli/DocUpdateDryPlan.cs\" Link=\"DocUpdateDryPlan.cs\" />", false),
        })
        {
            var found = ForbiddenLinks(Project(items), mcpDir);
            Check(found.Length > 0 == expected, "checker self-test: " + label + (expected ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }
        foreach (var (label, source, expected) in new (string, string, bool)[]
        {
            ("a call", "return DocUpdateExecute.Run(plan, channel, line, stdout);", true),
            ("a call split over lines", "var c = DocUpdateProbe\n        .Channel(pipe, revit);", true),
            ("a qualified call", "Bim.Cli.DocUpdateExecute.Run(plan);", true),
            ("a line comment", "// DocUpdateExecute.Run(plan)\nvar x = 1;", false),
            ("a block comment", "/* DocUpdateProbe.Plan(\n */ var x = 1; /* DocUpdateExecute.Run */", false),
            ("code after '//' inside a string", "var url = \"http://host\"; DocUpdateProbe.Channel(p, r);", true),
            ("code after '/*' inside a string", "var s = \"/*\"; DocUpdateExecute.Run(); var t = \"*/\";", true),
            ("code after a verbatim string ending in '\\'", "var s = @\"a\\\"; DocUpdateExecute.Run();", true),
            ("code after a char literal quote", "var c = '\"'; DocUpdateExecute.Run(); var d = '\"';", true),
            ("code after a raw string holding '\"' and '//'", "var r = \"\"\" a \" // b \"\"\"; DocUpdateProbe.PipeProblem(p);", true),
            ("code after an interpolated verbatim string", "var s = @$\"{x}\\\"; DocUpdateExecute.Run();", true),
            ("a longer identifier", "MyDocUpdateExecute.Run(); DocUpdateExecuteTests.Run();", false),
            ("the dry plan", "return DocUpdateCommand.DryPlan(before, after, realized, bindings, dependents);", false),
        })
        {
            var found = ForbiddenUses(source);
            Check(found.Length > 0 == expected, "checker self-test: " + label + (expected ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }

        // ---- 1. the project files ----
        var dryPlan = Path.GetFullPath(Path.Combine(cliDir, "DocUpdateDryPlan.cs"));
        var verb = Path.GetFullPath(Path.Combine(cliDir, "DocUpdateCommand.cs"));
        var mcpProject = Path.Combine(mcpDir, "Bim.Mcp.csproj");
        foreach (var project in new[] { mcpProject, Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj") })
        {
            var name = Path.GetFileName(project);
            var xml = File.ReadAllText(project);
            var projectDir = Path.GetDirectoryName(project)!;
            var forbidden = ForbiddenLinks(xml, projectDir);
            Check(forbidden.Length == 0, name + " links neither DocUpdateProbe.cs nor DocUpdateExecute.cs [" + string.Join(", ", forbidden) + "]");
            var files = Compiled(xml, projectDir).SelectMany(c => c.Files).ToArray();
            Check(files.Any(f => string.Equals(f, dryPlan, PathComparison)), name + " compiles interfaces/cli/DocUpdateDryPlan.cs (the shared dry plan, not a copy)");
            Check(!files.Any(f => string.Equals(f, verb, PathComparison)), name + " does not compile interfaces/cli/DocUpdateCommand.cs (the verb with the pipe and the run)");
        }
        var mcpXml = File.ReadAllText(mcpProject);
        var cliReferences = XDocument.Parse(mcpXml).Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .Where(include => Path.GetFullPath(Path.Combine(mcpDir, include.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))).StartsWith(cliDir + Path.DirectorySeparatorChar, PathComparison))
            .ToArray();
        Check(cliReferences.Length == 0, "Bim.Mcp.csproj references no interfaces/cli project [" + string.Join(", ", cliReferences) + "]");

        // ---- 2. every source the server compiles ----
        static bool Built(string path) => path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");
        var sources = Compiled(mcpXml, mcpDir).SelectMany(c => c.Files)
            .Concat(Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories).Where(f => !Built(Path.GetRelativePath(mcpDir, f))))
            .Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        Check(sources.Contains(dryPlan, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) && sources.Contains(Path.Combine(mcpDir, "DocUpdateTool.cs")),
            "the scanned server sources include DocUpdateTool.cs and DocUpdateDryPlan.cs (" + sources.Length + " files)");
        var missing = sources.Where(f => !File.Exists(f)).Select(f => Path.GetRelativePath(root, f)).ToArray();
        Check(missing.Length == 0, "every source Bim.Mcp.csproj compiles exists [" + string.Join(", ", missing) + "]");
        var uses = sources.Where(File.Exists).SelectMany(f => ForbiddenUses(File.ReadAllText(f)).Select(use => Path.GetRelativePath(root, f) + ": " + use)).ToArray();
        Check(uses.Length == 0, "no source of the MCP server uses DocUpdateProbe. or DocUpdateExecute. in code [" + string.Join(", ", uses) + "]");
    }
}
