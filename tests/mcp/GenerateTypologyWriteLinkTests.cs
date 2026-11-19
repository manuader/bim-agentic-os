using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// P4-26: the MCP server binary carries the generate-typology request-to-document path (GenerateTypologyCommand.Document and
// the shared Generate / Parse / pack loads the read-only tool generate_typology runs) but never the verb's write path. Run,
// Execute (the output-path rule, the exclusive GltfExporter.WriteNew write, the P3-25 removal of the just-written file, the
// success line), the P3-25 seam PostWriteProbe (invoked only inside Execute) and Fail live, moved verbatim, in
// interfaces/cli/GenerateTypologyWrite.cs, a part of the same internal static partial class Bim.Cli.GenerateTypologyCommand
// compiled by the CLI (its default glob) and not by interfaces/mcp/Bim.Mcp.csproj.
// Oracles (the P4-25 pattern, tests/mcp/PlanLayoutsWriteLinkTests.cs):
//   1. Bim.Mcp.csproj (every Compile item through DocUpdateLinkTests.Compiled: each ';' entry, '\' or '/', any case,
//      wildcards expanded, Link) compiles interfaces/cli/GenerateTypologyCommand.cs and no item names
//      GenerateTypologyWrite.cs; no source it compiles (linked items and interfaces/mcp/**/*.cs outside bin/obj) uses
//      GenerateTypologyCommand.Run / Execute / Fail / PostWriteProbe or imports the class with using static, in code.
//   2. The real Release Bim.Mcp.dll (its metadata) defines Bim.Cli.GenerateTypologyCommand with Document and Generate and
//      without Run, Execute, Fail or the PostWriteProbe accessors; the Release Bim.Cli.dll defines all of them (control).
//   3. Static split: both files are parts of the class; GenerateTypologyWrite.cs holds the original Run, Execute,
//      PostWriteProbe (with its comment) and Fail text byte for byte (LF-normalized, pinned SHA-256 of the pre-split
//      blocks, origin/main 895c6bc) and the only GltfExporter.WriteNew call; the shared part defines none of them and has
//      no exporter, stdout line, exit code, hash, file access or using Bim.Gltf in code; Execute still judges the output
//      path, then generates through the shared Generate(ReadRequest(...)), then writes.
//   4. A self-test of the link and use checkers on synthetic csproj and source texts.
// The generate_typology MCP bytes and the generate-typology CLI bytes are pinned by tests/mcp/GenerateTypologyToolTests.cs
// and tests/cli/GenerateTypologyTests.cs (unchanged).
// P4-27 then moved the generate-prototypes write path into interfaces/cli/GenerateWrite.cs, so the list of server-compiled
// GltfExporter.WriteNew callers checked below is empty (tests/mcp/GenerateWriteLinkTests.cs).
internal static class GenerateTypologyWriteLinkTests
{
    static readonly Regex ForbiddenUse = new(@"\bGenerateTypologyCommand\s*\.\s*(?:Run|Execute|Fail|PostWriteProbe)\b|\busing\s+static\s+(?:global\s*::\s*)?(?:Bim\s*\.\s*Cli\s*\.\s*)?GenerateTypologyCommand\b",
        RegexOptions.CultureInvariant);

    // SHA-256 and length of the pre-split text (LF line ends) of Run, Execute, the PostWriteProbe seam (from its comment's
    // first line through the declaration line) and Fail (origin/main 895c6bc).
    static readonly (string Name, string Start, string? End, int Length, string Sha256)[] Moved =
    {
        ("Run", "    public static void Run(string[] args)\n", null, 940, "276bcd59f46d30af29780c5e66b623cb0648f4665defcfd1b6fe049b5bc9d6b0"),
        ("Execute", "    static void Execute(string[] args)\n", null, 3785, "2736244ade44c149e18c2fa0f7ac409542b47ce9415d40b9511e71a7f2fc35c6"),
        ("PostWriteProbe", "    // P3-25 test seam: null in production.", "    internal static Action? PostWriteProbe { get; set; }\n", 329, "ab3fb49b7fcd00c4667f689724421a5851b78d2ecb590dbc7a4ec28978957e01"),
        ("Fail", "    static void Fail(string code, string? detail, string? skill = null)\n", null, 179, "88cfa4e44ffd48a4d8fc3cd9316af95aea3e8c64675c0142776c69a099609660"),
    };

    static bool IsWrite(string? path) => path is not null
        && string.Equals(path.Replace('\\', '/').Split('/')[^1], "GenerateTypologyWrite.cs", StringComparison.OrdinalIgnoreCase);

    // The Compile entries of a project text that compile the write part (by Include, a matched file or Link).
    internal static string[] WriteLinks(string projectXml, string projectDir) => DocUpdateLinkTests.Compiled(projectXml, projectDir)
        .Where(c => IsWrite(c.Include) || IsWrite(c.Link) || c.Files.Any(IsWrite))
        .Select(c => c.Include + (c.Link is null ? "" : " (Link " + c.Link + ")")).ToArray();

    // The uses of GenerateTypologyCommand.Run / .Execute / .Fail / .PostWriteProbe (or a using static of the class) in code.
    internal static string[] WriteUses(string source) =>
        ForbiddenUse.Matches(DocUpdateLinkTests.CodeOf(source)).Select(m => Regex.Replace(m.Value, @"\s+", " ")).ToArray();

    // The text from start through the first class-member closing line ("\n    }\n") after it, or through end when given.
    static string? Block(string text, string start, string? end)
    {
        var at = text.IndexOf(start, StringComparison.Ordinal);
        if (at < 0) return null;
        var close = end ?? "\n    }\n";
        var stop = text.IndexOf(close, at, StringComparison.Ordinal);
        return stop < 0 ? null : text[at..(stop + close.Length)];
    }

    static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();

    static string Compact(string code) => Regex.Replace(code, @"\s+", "");

    static int Count(string compact, string fragment) => compact.Split(fragment).Length - 1;

    public static void Run(string root, string mcpDll, string cliDll, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "generate-typology write link: " + name);
        var mcpDir = Path.Combine(root, "interfaces", "mcp");
        var cliDir = Path.Combine(root, "interfaces", "cli");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // ---- 4. self-test of the checkers ----
        // Wildcards are resolved against a scratch tree holding both parts, so the self-test does not depend on the real split.
        var scratch = Path.Combine(Path.GetTempPath(), "p4-26-link-" + Guid.NewGuid().ToString("N"));
        var scratchMcp = Path.Combine(scratch, "mcp");
        Directory.CreateDirectory(scratchMcp);
        Directory.CreateDirectory(Path.Combine(scratch, "cli"));
        File.WriteAllText(Path.Combine(scratch, "cli", "GenerateTypologyCommand.cs"), "");
        File.WriteAllText(Path.Combine(scratch, "cli", "GenerateTypologyWrite.cs"), "");
        try
        {
            string Synthetic(string items) => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>";
            foreach (var (label, items, flagged) in new (string, string, bool)[]
            {
                ("a multi-line item with '\\' separators", "    <Compile\n      Include=\"..\\cli\\GenerateTypologyWrite.cs\"\n      Link=\"GenerateTypologyWrite.cs\" />", true),
                ("an item in an XML comment", "    <!-- <Compile Include=\"../cli/GenerateTypologyWrite.cs\" /> -->\n    <Compile Include=\"../cli/GenerateTypologyCommand.cs\" />", false),
                ("the second of two ';' entries", "<Compile Include=\"../cli/GenerateTypologyCommand.cs;  ../cli/GenerateTypologyWrite.cs\" />", true),
                ("another case", "<Compile Include=\"../CLI/generatetypologywrite.CS\" />", true),
                ("a Link only", "<Compile Include=\"../cli/Other.cs\" Link=\"Sub/GenerateTypologyWrite.cs\" />", true),
                ("a wildcard matching the CLI sources", "<Compile Include=\"../cli/GenerateTypology*.cs\" />", true),
                ("a recursive wildcard", "<Compile Include=\"../**/*.cs\" />", true),
                // Negative wildcards: a resolver that flagged every wildcard would fail these.
                ("a wildcard matching only the shared part", "<Compile Include=\"../cli/GenerateTypologyC*.cs\" />", false),
                ("a wildcard matching no file", "<Compile Include=\"../cli/Nothing*.cs\" />", false),
                ("the shared part only", "<Compile Include=\"../cli/GenerateTypologyCommand.cs\" Link=\"GenerateTypologyCommand.cs\" />", false),
            })
            {
                var found = WriteLinks(Synthetic(items), scratchMcp);
                Check(found.Length > 0 == flagged, "link checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
            }
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        // Fail-closed self-test of Block and the Moved pins on a synthetic file holding the pinned Fail text.
        {
            var fail = Moved.Single(m => m.Name == "Fail");
            const string FailText = "    static void Fail(string code, string? detail, string? skill = null)\n    {\n"
                + "        Console.WriteLine(FailureLine(code, detail, skill));\n        Environment.ExitCode = 1;\n    }\n";
            string Wrap(string body) => "class C\n{\n" + body + "\n    static void Next() { }\n}\n";
            var exact = Block(Wrap(FailText), fail.Start, fail.End);
            Check(exact is not null && exact.Length == fail.Length && Sha256(exact) == fail.Sha256,
                "block self-test: the exact pre-split Fail text matches its pin (" + (exact?.Length ?? -1) + " chars)");
            var changed = Block(Wrap(FailText.Replace("ExitCode = 1", "ExitCode = 2", StringComparison.Ordinal)), fail.Start, fail.End);
            Check(changed is not null && changed.Length == fail.Length && Sha256(changed) != fail.Sha256,
                "block self-test: a one-character change in the Fail block gives another hash (same length)");
            Check(Block("class C\n{\n" + FailText.Replace("\n    }\n", "\n", StringComparison.Ordinal) + "}", fail.Start, fail.End) is null,
                "block self-test: a Fail block without its closing line is not found (null)");
            Check(Block(Wrap(FailText), "    static void Missing(", null) is null, "block self-test: a missing start is not found (null)");
        }
        foreach (var (label, source, flagged) in new (string, string, bool)[]
        {
            ("a call", "GenerateTypologyCommand.Run(args);", true),
            ("a call split over lines", "Bim.Cli.GenerateTypologyCommand\n        .Run(args);", true),
            ("a method group", "Action<string, string?, string?> f = GenerateTypologyCommand . Fail;", true),
            ("Execute by name", "GenerateTypologyCommand.Execute(args);", true),
            ("the post-write seam", "GenerateTypologyCommand.PostWriteProbe = null;", true),
            ("a using static", "using static Bim.Cli.GenerateTypologyCommand;\nRun(args);", true),
            ("a line comment", "// GenerateTypologyCommand.Run(args)\nvar x = 1;", false),
            ("a block comment", "/* GenerateTypologyCommand.Run(\n */ var x = 1;", false),
            ("code after '//' inside a string", "var url = \"http://host\"; GenerateTypologyCommand.Run(args);", true),
            ("a longer member", "GenerateTypologyCommand.RunTests(); GenerateTypologyCommand.Failed(); GenerateTypologyCommand.ExecuteAll();", false),
            ("the shared document path", "var (ok, text) = GenerateTypologyCommand.Document(requestPath);", false),
            ("the other seams", "GenerateTypologyCommand.GuardedWorkProbe = null; GenerateTypologyCommand.UnguardedWorkProbe = null;", false),
            // A using alias is a known blind spot of this textual check (not flagged, by design); the metadata check of the
            // real Release Bim.Mcp.dll below covers it.
            ("a using alias (covered by the binary metadata check)", "using G = Bim.Cli.GenerateTypologyCommand;\nG.Run(args);", false),
        })
        {
            var found = WriteUses(source);
            Check(found.Length > 0 == flagged, "use checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }

        // ---- 1. the server project file and every source it compiles ----
        var sharedPath = Path.GetFullPath(Path.Combine(cliDir, "GenerateTypologyCommand.cs"));
        var writePath = Path.GetFullPath(Path.Combine(cliDir, "GenerateTypologyWrite.cs"));
        static bool Built(string path) => path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");
        var testsDir = Path.GetFullPath(Path.Combine(root, "tests")) + Path.DirectorySeparatorChar;
        var project = Path.Combine(mcpDir, "Bim.Mcp.csproj");
        var projectXml = File.ReadAllText(project);
        var compiledItems = DocUpdateLinkTests.Compiled(projectXml, mcpDir);
        var links = WriteLinks(projectXml, mcpDir);
        Check(links.Length == 0, "Bim.Mcp.csproj does not compile the generate-typology write part (interfaces/cli/GenerateTypologyWrite.cs) [" + string.Join(", ", links) + "]");
        Check(compiledItems.Any(c => c.Files.Any(path => string.Equals(path, sharedPath, comparison))),
            "Bim.Mcp.csproj compiles interfaces/cli/GenerateTypologyCommand.cs (the document path the generate_typology tool runs)");
        var compiledSources = compiledItems.SelectMany(c => c.Files)
            .Concat(Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories).Where(path => !Built(Path.GetRelativePath(mcpDir, path))))
            .Select(path => Path.GetFullPath(path)).Where(path => !path.StartsWith(testsDir, comparison) && File.Exists(path)).Distinct().ToArray();
        var users = compiledSources.Select(path => (Name: Path.GetFileName(path), Found: WriteUses(File.ReadAllText(path)))).Where(u => u.Found.Length > 0)
            .Select(u => u.Name + " " + string.Join(" ", u.Found)).ToArray();
        Check(users.Length == 0, "Bim.Mcp.csproj: no compiled source uses GenerateTypologyCommand.Run / Execute / Fail / PostWriteProbe [" + string.Join(", ", users) + "]");
        var remaining = compiledSources.Where(path => Regex.IsMatch(DocUpdateLinkTests.CodeOf(File.ReadAllText(path)), @"\bGltfExporter\s*\.\s*WriteNew\s*\("))
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
        // After P4-27 (generate-prototypes moved its write path into GenerateWrite.cs) no server-compiled writer is left.
        Check(remaining.Length == 0,
            "no source compiled by Bim.Mcp.csproj still calls GltfExporter.WriteNew (P4-27: generate-prototypes was the last remaining writer) ["
            + string.Join(", ", remaining) + "]");
        Check(!remaining.Contains("GenerateTypologyCommand.cs", StringComparer.OrdinalIgnoreCase) && !remaining.Contains("GenerateTypologyWrite.cs", StringComparer.OrdinalIgnoreCase),
            "no generate-typology source compiled by Bim.Mcp.csproj calls GltfExporter.WriteNew [" + string.Join(", ", remaining) + "]");

        // ---- 2. the real Release binaries ----
        var verbMembers = new[] { "Run", "Execute", "Fail", "get_PostWriteProbe", "set_PostWriteProbe" };
        var shared = new[] { "Document", "Generate" };
        var mcpMethods = File.Exists(mcpDll) ? PlanLayoutsWriteLinkTests.MethodsOf(mcpDll, "Bim.Cli", "GenerateTypologyCommand") : null;
        var cliMethods = File.Exists(cliDll) ? PlanLayoutsWriteLinkTests.MethodsOf(cliDll, "Bim.Cli", "GenerateTypologyCommand") : null;
        var cliNames = cliMethods ?? Array.Empty<string>();
        var mcpNames = mcpMethods ?? Array.Empty<string>();
        Check(cliMethods is not null && verbMembers.Concat(shared).All(member => cliNames.Contains(member)),
            "the Release Bim.Cli.dll defines Bim.Cli.GenerateTypologyCommand with Run, Execute, Fail, PostWriteProbe, Document and Generate (reader control) [" + string.Join(", ", cliNames) + "]");
        Check(mcpMethods is not null && shared.All(member => mcpNames.Contains(member)) && !verbMembers.Any(member => mcpNames.Contains(member)),
            "the Release Bim.Mcp.dll defines Bim.Cli.GenerateTypologyCommand with Document and Generate and without Run, Execute, Fail or PostWriteProbe [" + string.Join(", ", mcpNames) + "]");

        // ---- 3. the split ----
        Check(File.Exists(writePath), "interfaces/cli/GenerateTypologyWrite.cs holds the generate-typology write path (split from GenerateTypologyCommand.cs)");
        var writeText = File.Exists(writePath) ? File.ReadAllText(writePath).Replace("\r\n", "\n", StringComparison.Ordinal) : "";
        var sharedText = File.ReadAllText(sharedPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var (name, start, end, length, sha256) in Moved)
        {
            var block = Block(writeText, start, end);
            Check(block is not null && block.Length == length && Sha256(block) == sha256,
                "GenerateTypologyWrite.cs holds " + name + " moved verbatim (" + (block?.Length ?? -1) + " chars, sha256 " + (block is null ? "-" : Sha256(block)) + ")");
            Check(!sharedText.Contains(start, StringComparison.Ordinal), "GenerateTypologyCommand.cs no longer holds " + name);
        }
        var write = Compact(DocUpdateLinkTests.CodeOf(writeText));
        var sharedCode = Compact(DocUpdateLinkTests.CodeOf(sharedText));
        Check(Count(write, "namespaceBim.Cli;") == 1 && Count(write, "internalstaticpartialclassGenerateTypologyCommand") == 1
            && Count(sharedCode, "namespaceBim.Cli;") == 1 && Count(sharedCode, "internalstaticpartialclassGenerateTypologyCommand") == 1,
            "both files are parts of internal static partial class Bim.Cli.GenerateTypologyCommand (callers unchanged)");
        var definitions = new[] { "publicstaticvoidRun(string[]args)", "staticvoidExecute(string[]args)", "staticvoidFail(stringcode,string?detail,string?skill=null)",
            "internalstaticAction?PostWriteProbe{get;set;}" };
        var misplaced = definitions.Where(d => Count(write, d) != 1 || Count(sharedCode, d) != 0).ToArray();
        Check(misplaced.Length == 0, "GenerateTypologyWrite.cs defines Run, Execute, Fail and PostWriteProbe once and GenerateTypologyCommand.cs none of them [" + string.Join(", ", misplaced) + "]");
        Check(Count(write, "GltfExporter.WriteNew(bytes,output)") == 1 && Count(write, "WriteNew") == 1,
            "GenerateTypologyWrite.cs holds the verb's single exclusive write (GltfExporter.WriteNew)");
        var writerApis = new[] { "GltfExporter", "WriteNew", "Console.WriteLine(", "Console.Out", "Environment.ExitCode", "SHA256", "File.", "FileStream", "usingBim.Gltf;", "PostWriteProbe" }
            .Where(api => sharedCode.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(sharedCode.Length > 0 && writerApis.Length == 0,
            "GenerateTypologyCommand.cs (compiled by the server) holds no writer: no exporter, stdout line, exit code, hash, file access, post-write seam or using Bim.Gltf in code ["
            + string.Join(", ", writerApis) + "]");
        Check(Count(sharedCode, "Generate(ReadRequest(requestPath))") == 1 && Count(sharedCode, "Generate(ReadRequest(") == 1 && Count(write, "Generate(ReadRequest(args[1]))") == 1,
            "Document (shared) and Execute (write part) each call Generate(ReadRequest(...)) once");
        var order = new[] { "GltfExportCommand.OutputProblem(output,[\".json\"],out_)", "Generate(ReadRequest(args[1]))", "GltfExporter.WriteNew(bytes,output)", "PostWriteProbe?.Invoke();", "File.Delete(output);" }
            .Select(fragment => write.IndexOf(fragment, StringComparison.Ordinal)).ToArray();
        Check(order.All(at => at >= 0) && order.Zip(order.Skip(1)).All(pair => pair.First < pair.Second),
            "Execute still judges the output path, generates through the shared Generate, writes exclusively, then runs the post-write seam and the removing catch [" + string.Join(", ", order) + "]");
    }
}
