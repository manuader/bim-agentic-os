using System.Text.RegularExpressions;

// P4-24: the MCP server binary carries the shared bounded output-path rule (GltfExportCommand.OutputProblem, used by the
// generate-typology, plan-layouts, doc-realized and generate-prototypes sources it links) but never the export-gltf verb
// itself (GltfExportCommand.Run / Usage / Fail: the bounded read, the exporter call and the exclusive write).
// OutputProblem, HasStreamColon and DeviceNamespace live in interfaces/cli/OutputProblem.cs (a part of the same
// internal static partial class Bim.Cli.GltfExportCommand, so every caller is unchanged); the verb stays in
// interfaces/cli/GltfExportCommand.cs, compiled by the CLI only.
// Oracles:
//   1. Bim.Mcp.csproj and tests/mcp/Bim.Mcp.Tests.csproj (every Compile item through DocUpdateLinkTests.Compiled: each ';'
//      entry, '\' or '/', any case, wildcards expanded, Link) compile interfaces/cli/OutputProblem.cs and no item names
//      GltfExportCommand.cs; no source they compile (linked items and interfaces/mcp/**/*.cs outside bin/obj) uses
//      GltfExportCommand.Run / Usage / Fail in code.
//   2. Static (comments stripped by DocUpdateLinkTests.CodeOf, literals kept, whitespace removed): OutputProblem.cs
//      defines both OutputProblem overloads, HasStreamColon and DeviceNamespace and holds no Run, Usage, Fail, exporter,
//      bounded read or stdout; GltfExportCommand.cs no longer defines them and still calls OutputProblem first.
//   3. A self-test of both checkers on synthetic csproj and source texts.
//   4. The real Release Bim.Mcp.dll answers tools/list, an unknown-tool and a missing-argument refusal with the
//      in-process bytes; both refusal lines are also pinned literally.
// Remaining (follow-up, not this task): Bim.Mcp.csproj still compiles the writer verb GenerateCommand.cs
// (generate-prototypes) in full, so its Run path and GltfExporter.WriteNew stay in the server binary. P4-25 moved the
// plan-layouts write path into PlanLayoutsWrite.cs and P4-26 the generate-typology write path into
// GenerateTypologyWrite.cs, neither compiled by the server (tests/mcp/PlanLayoutsWriteLinkTests.cs,
// tests/mcp/GenerateTypologyWriteLinkTests.cs).
internal static class GltfExportLinkTests
{
    static readonly Regex ForbiddenUse = new(@"\bGltfExportCommand\s*\.\s*(?:Run|Usage|Fail)\b", RegexOptions.CultureInvariant);

    static bool IsVerb(string? path) => path is not null
        && string.Equals(path.Replace('\\', '/').Split('/')[^1], "GltfExportCommand.cs", StringComparison.OrdinalIgnoreCase);

    // The Compile entries of a project text that compile the export-gltf verb source (by Include, a matched file or Link).
    internal static string[] VerbLinks(string projectXml, string projectDir) => DocUpdateLinkTests.Compiled(projectXml, projectDir)
        .Where(c => IsVerb(c.Include) || IsVerb(c.Link) || c.Files.Any(IsVerb))
        .Select(c => c.Include + (c.Link is null ? "" : " (Link " + c.Link + ")")).ToArray();

    // The uses of GltfExportCommand.Run / .Usage / .Fail in the code (not the comments) of a source.
    internal static string[] VerbUses(string source) =>
        ForbiddenUse.Matches(DocUpdateLinkTests.CodeOf(source)).Select(m => Regex.Replace(m.Value, @"\s+", "")).ToArray();

    static string Compact(string code) => Regex.Replace(code, @"\s+", "");

    static int Count(string compact, string fragment) => compact.Split(fragment).Length - 1;

    public static void Run(string root, Func<string, string?> handle, Func<string, string> mcpProcess, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "gltf-export link: " + name);
        var mcpDir = Path.Combine(root, "interfaces", "mcp");
        var cliDir = Path.Combine(root, "interfaces", "cli");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // ---- 3. self-test of the checkers ----
        string Synthetic(string items) => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>";
        foreach (var (label, items, flagged) in new (string, string, bool)[]
        {
            ("a multi-line item with '\\' separators", "    <Compile\n      Include=\"..\\cli\\GltfExportCommand.cs\"\n      Link=\"GltfExportCommand.cs\" />", true),
            ("an item in an XML comment", "    <!-- <Compile Include=\"../cli/GltfExportCommand.cs\" /> -->\n    <Compile Include=\"../cli/OutputProblem.cs\" />", false),
            ("the second of two ';' entries", "<Compile Include=\"../cli/BoundedInput.cs;  ../cli/GltfExportCommand.cs\" />", true),
            ("another case", "<Compile Include=\"../CLI/gltfexportcommand.CS\" />", true),
            ("a Link only", "<Compile Include=\"../cli/Other.cs\" Link=\"Sub/GltfExportCommand.cs\" />", true),
            ("a wildcard matching the CLI sources", "<Compile Include=\"../cli/Gltf*.cs\" />", true),
            ("a recursive wildcard", "<Compile Include=\"../**/*.cs\" />", true),
            ("the shared output rule only", "<Compile Include=\"../cli/OutputProblem.cs\" Link=\"OutputProblem.cs\" />", false),
        })
        {
            var found = VerbLinks(Synthetic(items), mcpDir);
            Check(found.Length > 0 == flagged, "link checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }
        foreach (var (label, source, flagged) in new (string, string, bool)[]
        {
            ("a call", "GltfExportCommand.Run(args);", true),
            ("a call split over lines", "GltfExportCommand\n        .Run(args);", true),
            ("the usage text", "throw new ArgumentException(Bim.Cli.GltfExportCommand.Usage);", true),
            ("a method group", "Action<string, string?> f = GltfExportCommand . Fail;", true),
            ("a line comment", "// GltfExportCommand.Run(args)\nvar x = 1;", false),
            ("a block comment", "/* GltfExportCommand.Run(\n */ var x = 1;", false),
            ("code after '//' inside a string", "var url = \"http://host\"; GltfExportCommand.Run(args);", true),
            ("a longer member", "GltfExportCommand.RunTests(); GltfExportCommand.Failed(); GltfExportCommand.UsageText();", false),
            ("the shared output rule", "if (GltfExportCommand.OutputProblem(output, [\".json\"], out _) is string refused) throw new Refusal(refused);", false),
        })
        {
            var found = VerbUses(source);
            Check(found.Length > 0 == flagged, "use checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }

        // ---- 1. the MCP project files and every source they compile ----
        var rulePath = Path.Combine(cliDir, "OutputProblem.cs");
        var verbPath = Path.Combine(cliDir, "GltfExportCommand.cs");
        static bool Built(string path) => path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");
        var testsDir = Path.GetFullPath(Path.Combine(root, "tests")) + Path.DirectorySeparatorChar;
        foreach (var project in new[] { Path.Combine(mcpDir, "Bim.Mcp.csproj"), Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj") })
        {
            var name = Path.GetFileName(project);
            var projectDir = Path.GetDirectoryName(project)!;
            var projectXml = File.ReadAllText(project);
            var items = DocUpdateLinkTests.Compiled(projectXml, projectDir);
            var links = VerbLinks(projectXml, projectDir);
            Check(links.Length == 0, name + " does not compile the export-gltf verb (interfaces/cli/GltfExportCommand.cs) [" + string.Join(", ", links) + "]");
            Check(items.Any(c => c.Files.Any(path => string.Equals(path, Path.GetFullPath(rulePath), comparison))),
                name + " compiles interfaces/cli/OutputProblem.cs (shared source, not a copy)");
            var compiledSources = items.SelectMany(c => c.Files)
                .Concat(Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories).Where(path => !Built(Path.GetRelativePath(mcpDir, path))))
                .Select(path => Path.GetFullPath(path)).Where(path => !path.StartsWith(testsDir, comparison) && File.Exists(path)).Distinct().ToArray();
            var users = compiledSources.Select(path => (Name: Path.GetFileName(path), Found: VerbUses(File.ReadAllText(path)))).Where(u => u.Found.Length > 0)
                .Select(u => u.Name + " " + string.Join(" ", u.Found)).ToArray();
            Check(users.Length == 0, name + ": no compiled source uses GltfExportCommand.Run / Usage / Fail [" + string.Join(", ", users) + "]");
        }

        // ---- 2. the split ----
        Check(File.Exists(rulePath), "interfaces/cli/OutputProblem.cs holds the bounded output-path rule (split from GltfExportCommand.cs)");
        var rule = File.Exists(rulePath) ? Compact(DocUpdateLinkTests.CodeOf(File.ReadAllText(rulePath))) : "";
        var verb = Compact(DocUpdateLinkTests.CodeOf(File.ReadAllText(verbPath)));
        Check(Count(rule, "namespaceBim.Cli;") == 1 && Count(rule, "internalstaticpartialclassGltfExportCommand") == 1
            && Count(verb, "namespaceBim.Cli;") == 1 && Count(verb, "internalstaticpartialclassGltfExportCommand") == 1,
            "both files are parts of internal static partial class Bim.Cli.GltfExportCommand (callers unchanged)");
        var definitions = new[]
        {
            "internalstaticstring?OutputProblem(stringpath,outstringformat)",
            "internalstaticstring?OutputProblem(stringpath,IReadOnlyCollection<string>extensions,outstringformat)",
            "internalstaticboolHasStreamColon(stringpath)",
            "staticboolDeviceNamespace(stringpath)",
        };
        var misplaced = definitions.Where(d => Count(rule, d) != 1 || Count(verb, d) != 0).ToArray();
        Check(misplaced.Length == 0, "OutputProblem.cs defines both OutputProblem overloads, HasStreamColon and DeviceNamespace once and GltfExportCommand.cs none of them [" + string.Join(", ", misplaced) + "]");
        var verbApis = new[] { "Run(", "Usage", "Fail(", "GltfExporter", "BoundedInput", "JsonSerializer", "Console.", "Environment.ExitCode", "usingBim.Gltf;" }
            .Where(api => rule.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(rule.Length > 0 && verbApis.Length == 0, "OutputProblem.cs holds no verb code: no Run, Usage, Fail, exporter, bounded read, JSON or stdout [" + string.Join(", ", verbApis) + "]");
        var order = new[] { "OutputProblem(args[2],outvarformat)", "BoundedInput.ReadText(args[1])", "GltfExporter.WriteNew(written,args[2])" }
            .Select(fragment => verb.IndexOf(fragment, StringComparison.Ordinal)).ToArray();
        Check(order.All(at => at >= 0) && order.Zip(order.Skip(1)).All(pair => pair.First < pair.Second),
            "GltfExportCommand.Run still judges the output path first, then reads the input, then writes exclusively [" + string.Join(", ", order) + "]");

        // ---- 4. the real server binary serves the in-process bytes ----
        foreach (var (label, line, pinned) in new (string, string, string?)[]
        {
            ("tools/list", """{"jsonrpc":"2.0","id":241,"method":"tools/list"}""", null),
            ("an unknown tool refusal", """{"jsonrpc":"2.0","id":242,"method":"tools/call","params":{"name":"export_gltf","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":242,"result":{"content":[{"type":"text","text":"{\"status\":\"failed\",\"code\":\"unknown_tool\"}"}],"isError":true}}"""),
            ("a missing-argument refusal", """{"jsonrpc":"2.0","id":243,"method":"tools/call","params":{"name":"generate_prototypes","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":243,"error":{"code":-32602,"message":"invalid_params: request must be string"}}"""),
        })
        {
            var expected = handle(line) ?? "";
            var served = mcpProcess(line + "\n").Split('\n')[0].TrimEnd('\r');
            Check(expected.Length > 0 && served == expected, "the real Bim.Mcp.dll answers " + label + " with the in-process bytes (" + served.Length + " vs " + expected.Length + " chars)");
            if (pinned is not null) Check(served == pinned, "the real Bim.Mcp.dll answers " + label + " with the pinned line | " + served);
        }
    }
}
