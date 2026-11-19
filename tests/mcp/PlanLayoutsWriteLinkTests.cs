using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// P4-25: the MCP server binary carries the plan-layouts dry plan (PlanLayoutsCommand.DryPlan and the shared Plan path the
// read-only tool plan_layouts runs) but never the verb's write path. Run, Execute (the option loop call, the output-path
// rule, the exclusive GltfExporter.WriteNew write, the success line) and Fail live, moved verbatim, in
// interfaces/cli/PlanLayoutsWrite.cs, a part of the same internal static partial class Bim.Cli.PlanLayoutsCommand compiled
// by the CLI (its default glob), tests/cli and tests/mcp (whose P7-22 seam tests run the verb's real Run in process), and
// not by interfaces/mcp/Bim.Mcp.csproj.
// Oracles:
//   1. Bim.Mcp.csproj (every Compile item through DocUpdateLinkTests.Compiled: each ';' entry, '\' or '/', any case,
//      wildcards expanded, Link) compiles interfaces/cli/PlanLayoutsCommand.cs and no item names PlanLayoutsWrite.cs; no
//      source it compiles (linked items and interfaces/mcp/**/*.cs outside bin/obj) uses PlanLayoutsCommand.Run / Execute
//      / Fail or imports the class with using static, in code (DocUpdateLinkTests.CodeOf strips comments, keeps literals).
//   2. The real Release Bim.Mcp.dll (its metadata) defines Bim.Cli.PlanLayoutsCommand with DryPlan and Plan and without
//      Run, Execute or Fail; the Release Bim.Cli.dll defines all five (the reader's positive control).
//   3. Static split: both files are parts of the class; PlanLayoutsWrite.cs holds the original Run+Execute and Fail text
//      byte for byte (LF-normalized, pinned SHA-256 of the pre-split blocks) and the only GltfExporter.WriteNew call; the
//      shared part defines none of them and has no exporter, stdout line, exit code or using Bim.Gltf in code; Execute
//      still judges the output path, then plans, then writes.
//   4. A self-test of the link and use checkers on synthetic csproj and source texts.
// The plan_layouts MCP bytes and the plan-layouts CLI bytes are pinned by tests/mcp/PlanLayoutsToolTests.cs and
// tests/cli/PlanLayoutsTests.cs (unchanged).
// Remaining (follow-up, not this task): Bim.Mcp.csproj still compiles the writer verbs GenerateCommand.cs
// (generate-prototypes) and GenerateTypologyCommand.cs (generate-typology) in full, so their Run paths and
// GltfExporter.WriteNew stay in the server binary; the NOTE line below lists the compiled sources still calling it.
internal static class PlanLayoutsWriteLinkTests
{
    static readonly Regex ForbiddenUse = new(@"\bPlanLayoutsCommand\s*\.\s*(?:Run|Execute|Fail)\b|\busing\s+static\s+(?:global\s*::\s*)?(?:Bim\s*\.\s*Cli\s*\.\s*)?PlanLayoutsCommand\b",
        RegexOptions.CultureInvariant);

    // SHA-256 of the pre-split text (LF line ends) of Run through the end of Execute, and of Fail (origin/main 343194a).
    const string RunExecuteSha256 = "844a585f31a65ba3ca41496c0692f19b4fb3cc0437f062cb5c16bd03865151b9";
    const int RunExecuteLength = 2528;
    const string FailSha256 = "0fbbe9bc83a0d8728fa0cb1aa93b4de75881bdd39d99fe7a49ebd5103c7ceed1";
    const int FailLength = 150;

    static bool IsWrite(string? path) => path is not null
        && string.Equals(path.Replace('\\', '/').Split('/')[^1], "PlanLayoutsWrite.cs", StringComparison.OrdinalIgnoreCase);

    // The Compile entries of a project text that compile the write part (by Include, a matched file or Link).
    internal static string[] WriteLinks(string projectXml, string projectDir) => DocUpdateLinkTests.Compiled(projectXml, projectDir)
        .Where(c => IsWrite(c.Include) || IsWrite(c.Link) || c.Files.Any(IsWrite))
        .Select(c => c.Include + (c.Link is null ? "" : " (Link " + c.Link + ")")).ToArray();

    // The uses of PlanLayoutsCommand.Run / .Execute / .Fail (or a using static of the class) in the code of a source.
    internal static string[] WriteUses(string source) =>
        ForbiddenUse.Matches(DocUpdateLinkTests.CodeOf(source)).Select(m => Regex.Replace(m.Value, @"\s+", " ")).ToArray();

    // The method names of the top-level type ns.name in an assembly's metadata, or null when it has no such type.
    internal static string[]? MethodsOf(string assemblyPath, string ns, string name)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (!type.GetDeclaringType().IsNil || metadata.GetString(type.Namespace) != ns || metadata.GetString(type.Name) != name) continue;
            return type.GetMethods().Select(m => metadata.GetString(metadata.GetMethodDefinition(m).Name)).Distinct().Order(StringComparer.Ordinal).ToArray();
        }
        return null;
    }

    // The text from start through the first class-member closing line ("\n    }\n") after from (or after start).
    static string? Block(string text, string start, string? from = null)
    {
        var at = text.IndexOf(start, StringComparison.Ordinal);
        if (at < 0) return null;
        var after = from is null ? at : text.IndexOf(from, at, StringComparison.Ordinal);
        if (after < 0) return null;
        var end = text.IndexOf("\n    }\n", after, StringComparison.Ordinal);
        return end < 0 ? null : text[at..(end + "\n    }\n".Length)];
    }

    static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();

    static string Compact(string code) => Regex.Replace(code, @"\s+", "");

    static int Count(string compact, string fragment) => compact.Split(fragment).Length - 1;

    public static void Run(string root, string mcpDll, string cliDll, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "plan-layouts write link: " + name);
        var mcpDir = Path.Combine(root, "interfaces", "mcp");
        var cliDir = Path.Combine(root, "interfaces", "cli");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // ---- 4. self-test of the checkers ----
        string Synthetic(string items) => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>";
        foreach (var (label, items, flagged) in new (string, string, bool)[]
        {
            ("a multi-line item with '\\' separators", "    <Compile\n      Include=\"..\\cli\\PlanLayoutsWrite.cs\"\n      Link=\"PlanLayoutsWrite.cs\" />", true),
            ("an item in an XML comment", "    <!-- <Compile Include=\"../cli/PlanLayoutsWrite.cs\" /> -->\n    <Compile Include=\"../cli/PlanLayoutsCommand.cs\" />", false),
            ("the second of two ';' entries", "<Compile Include=\"../cli/PlanLayoutsCommand.cs;  ../cli/PlanLayoutsWrite.cs\" />", true),
            ("another case", "<Compile Include=\"../CLI/planlayoutswrite.CS\" />", true),
            ("a Link only", "<Compile Include=\"../cli/Other.cs\" Link=\"Sub/PlanLayoutsWrite.cs\" />", true),
            ("a wildcard matching the CLI sources", "<Compile Include=\"../cli/PlanLayouts*.cs\" />", true),
            ("a recursive wildcard", "<Compile Include=\"../**/*.cs\" />", true),
            ("the shared part only", "<Compile Include=\"../cli/PlanLayoutsCommand.cs\" Link=\"PlanLayoutsCommand.cs\" />", false),
        })
        {
            var found = WriteLinks(Synthetic(items), mcpDir);
            Check(found.Length > 0 == flagged, "link checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }
        foreach (var (label, source, flagged) in new (string, string, bool)[]
        {
            ("a call", "PlanLayoutsCommand.Run(args);", true),
            ("a call split over lines", "Bim.Cli.PlanLayoutsCommand\n        .Run(args);", true),
            ("a method group", "Action<string, string?> f = PlanLayoutsCommand . Fail;", true),
            ("Execute by name", "PlanLayoutsCommand.Execute(args);", true),
            ("a using static", "using static Bim.Cli.PlanLayoutsCommand;\nRun(args);", true),
            ("a line comment", "// PlanLayoutsCommand.Run(args)\nvar x = 1;", false),
            ("a block comment", "/* PlanLayoutsCommand.Run(\n */ var x = 1;", false),
            ("code after '//' inside a string", "var url = \"http://host\"; PlanLayoutsCommand.Run(args);", true),
            ("a longer member", "PlanLayoutsCommand.RunTests(); PlanLayoutsCommand.Failed(); PlanLayoutsCommand.ExecuteAll();", false),
            ("the dry plan", "var (ok, text) = PlanLayoutsCommand.DryPlan(brief, null, null, null);", false),
        })
        {
            var found = WriteUses(source);
            Check(found.Length > 0 == flagged, "use checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }

        // ---- 1. the server project file and every source it compiles ----
        var sharedPath = Path.GetFullPath(Path.Combine(cliDir, "PlanLayoutsCommand.cs"));
        var writePath = Path.GetFullPath(Path.Combine(cliDir, "PlanLayoutsWrite.cs"));
        static bool Built(string path) => path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");
        var testsDir = Path.GetFullPath(Path.Combine(root, "tests")) + Path.DirectorySeparatorChar;
        var project = Path.Combine(mcpDir, "Bim.Mcp.csproj");
        var projectXml = File.ReadAllText(project);
        var compiledItems = DocUpdateLinkTests.Compiled(projectXml, mcpDir);
        var links = WriteLinks(projectXml, mcpDir);
        Check(links.Length == 0, "Bim.Mcp.csproj does not compile the plan-layouts write part (interfaces/cli/PlanLayoutsWrite.cs) [" + string.Join(", ", links) + "]");
        Check(compiledItems.Any(c => c.Files.Any(path => string.Equals(path, sharedPath, comparison))),
            "Bim.Mcp.csproj compiles interfaces/cli/PlanLayoutsCommand.cs (the dry plan the plan_layouts tool runs)");
        var compiledSources = compiledItems.SelectMany(c => c.Files)
            .Concat(Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories).Where(path => !Built(Path.GetRelativePath(mcpDir, path))))
            .Select(path => Path.GetFullPath(path)).Where(path => !path.StartsWith(testsDir, comparison) && File.Exists(path)).Distinct().ToArray();
        var users = compiledSources.Select(path => (Name: Path.GetFileName(path), Found: WriteUses(File.ReadAllText(path)))).Where(u => u.Found.Length > 0)
            .Select(u => u.Name + " " + string.Join(" ", u.Found)).ToArray();
        Check(users.Length == 0, "Bim.Mcp.csproj: no compiled source uses PlanLayoutsCommand.Run / Execute / Fail [" + string.Join(", ", users) + "]");
        var remaining = compiledSources.Where(path => Regex.IsMatch(DocUpdateLinkTests.CodeOf(File.ReadAllText(path)), @"\bGltfExporter\s*\.\s*WriteNew\s*\("))
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
        Console.WriteLine("NOTE plan-layouts write link: Bim.Mcp.csproj still compiles GltfExporter.WriteNew in [" + string.Join(", ", remaining) + "] (follow-up rows)");
        Check(!remaining.Contains("PlanLayoutsCommand.cs", StringComparer.OrdinalIgnoreCase) && !remaining.Contains("PlanLayoutsWrite.cs", StringComparer.OrdinalIgnoreCase),
            "no plan-layouts source compiled by Bim.Mcp.csproj calls GltfExporter.WriteNew [" + string.Join(", ", remaining) + "]");

        // ---- 2. the real Release binaries ----
        var verbMembers = new[] { "Run", "Execute", "Fail" };
        var mcpMethods = File.Exists(mcpDll) ? MethodsOf(mcpDll, "Bim.Cli", "PlanLayoutsCommand") : null;
        var cliMethods = File.Exists(cliDll) ? MethodsOf(cliDll, "Bim.Cli", "PlanLayoutsCommand") : null;
        var cliNames = cliMethods ?? Array.Empty<string>();
        var mcpNames = mcpMethods ?? Array.Empty<string>();
        Check(cliMethods is not null && verbMembers.Concat(new[] { "DryPlan", "Plan" }).All(member => cliNames.Contains(member)),
            "the Release Bim.Cli.dll defines Bim.Cli.PlanLayoutsCommand with Run, Execute, Fail, DryPlan and Plan (reader control) [" + string.Join(", ", cliNames) + "]");
        Check(mcpMethods is not null && mcpNames.Contains("DryPlan") && mcpNames.Contains("Plan") && !verbMembers.Any(member => mcpNames.Contains(member)),
            "the Release Bim.Mcp.dll defines Bim.Cli.PlanLayoutsCommand with DryPlan and Plan and without Run, Execute or Fail [" + string.Join(", ", mcpNames) + "]");

        // ---- 3. the split ----
        Check(File.Exists(writePath), "interfaces/cli/PlanLayoutsWrite.cs holds the plan-layouts write path (split from PlanLayoutsCommand.cs)");
        var writeText = File.Exists(writePath) ? File.ReadAllText(writePath).Replace("\r\n", "\n", StringComparison.Ordinal) : "";
        var sharedText = File.ReadAllText(sharedPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        var runExecute = Block(writeText, "    public static void Run(string[] args)\n", "    static void Execute(string[] args)\n");
        var fail = Block(writeText, "    static void Fail(string code, string? detail)\n");
        Check(runExecute is not null && runExecute.Length == RunExecuteLength && Sha256(runExecute) == RunExecuteSha256,
            "PlanLayoutsWrite.cs holds Run and Execute moved verbatim (" + (runExecute?.Length ?? -1) + " chars, sha256 " + (runExecute is null ? "-" : Sha256(runExecute)) + ")");
        Check(fail is not null && fail.Length == FailLength && Sha256(fail) == FailSha256,
            "PlanLayoutsWrite.cs holds Fail moved verbatim (" + (fail?.Length ?? -1) + " chars, sha256 " + (fail is null ? "-" : Sha256(fail)) + ")");
        var write = Compact(DocUpdateLinkTests.CodeOf(writeText));
        var shared = Compact(DocUpdateLinkTests.CodeOf(sharedText));
        Check(Count(write, "namespaceBim.Cli;") == 1 && Count(write, "internalstaticpartialclassPlanLayoutsCommand") == 1
            && Count(shared, "namespaceBim.Cli;") == 1 && Count(shared, "internalstaticpartialclassPlanLayoutsCommand") == 1,
            "both files are parts of internal static partial class Bim.Cli.PlanLayoutsCommand (callers unchanged)");
        var definitions = new[] { "publicstaticvoidRun(string[]args)", "staticvoidExecute(string[]args)", "staticvoidFail(stringcode,string?detail)" };
        var misplaced = definitions.Where(d => Count(write, d) != 1 || Count(shared, d) != 0).ToArray();
        Check(misplaced.Length == 0, "PlanLayoutsWrite.cs defines Run, Execute and Fail once and PlanLayoutsCommand.cs none of them [" + string.Join(", ", misplaced) + "]");
        Check(Count(write, "GltfExporter.WriteNew(bytes,output)") == 1 && Count(write, "WriteNew") == 1,
            "PlanLayoutsWrite.cs holds the verb's single exclusive write (GltfExporter.WriteNew)");
        var writerApis = new[] { "GltfExporter", "WriteNew", "Console.WriteLine(", "Console.Out", "Environment.ExitCode", "SHA256", "File.", "FileStream", "usingBim.Gltf;" }
            .Where(api => shared.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(shared.Length > 0 && writerApis.Length == 0,
            "PlanLayoutsCommand.cs (compiled by the server) holds no writer: no exporter, stdout line, exit code, hash, file access or using Bim.Gltf in code [" + string.Join(", ", writerApis) + "]");
        var order = new[] { "ParseOptions(args)", "GltfExportCommand.OutputProblem(output,[\".json\"],out_)", "Plan(args[1],count,seed,packId)", "GltfExporter.WriteNew(bytes,output)" }
            .Select(fragment => write.IndexOf(fragment, StringComparison.Ordinal)).ToArray();
        Check(order.All(at => at >= 0) && order.Zip(order.Skip(1)).All(pair => pair.First < pair.Second),
            "Execute still parses the options, judges the output path, plans through the shared Plan, then writes exclusively [" + string.Join(", ", order) + "]");
    }
}
