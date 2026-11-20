using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// P4-27: the MCP server binary carries the generate-prototypes request-to-document path (GenerateCommand.Document and the
// shared Generate / Parse / pack load the read-only tool generate_prototypes runs) but never the verb's write path. Run,
// Execute (the output-path rule, the exclusive GltfExporter.WriteNew write, the P7-26 removal of the just-written file, the
// P7-27 report of a failed removal, the success line), the P7-26 seam PostWriteStageProbe, the P7-27 seam
// PostWriteDeleteProbe and its PartialOutputLeft line (both used only inside Execute) and Fail live, moved verbatim, in
// interfaces/cli/GenerateWrite.cs, a part of the same internal static partial class Bim.Cli.GenerateCommand compiled by the
// CLI (its default glob) and not by interfaces/mcp/Bim.Mcp.csproj. It was the last server-compiled writer.
// Oracles (the P4-25 / P4-26 pattern, tests/mcp/PlanLayoutsWriteLinkTests.cs, tests/mcp/GenerateTypologyWriteLinkTests.cs):
//   1. Bim.Mcp.csproj (every Compile item through DocUpdateLinkTests.Compiled: each ';' entry, '\' or '/', any case,
//      wildcards expanded, Link) compiles interfaces/cli/GenerateCommand.cs and no item names GenerateWrite.cs; no source
//      it compiles (linked items and interfaces/mcp/**/*.cs outside bin/obj) uses GenerateCommand.Run / Execute / Fail /
//      PostWriteStageProbe / PostWriteDeleteProbe / PartialOutputLeft or imports the class with using static, in code; and
//      no source it compiles calls GltfExporter.WriteNew at all (the list is empty).
//   2. The real Release Bim.Mcp.dll (its metadata) defines Bim.Cli.GenerateCommand with Document and Generate and without
//      Run, Execute, Fail, the seam accessors or the PartialOutputLeft field, and holds no member reference to
//      Bim.Gltf.GltfExporter.WriteNew; the Release Bim.Cli.dll defines all of them and references WriteNew (control).
//   3. Static split: both files are parts of the class; GenerateWrite.cs holds the original Run, Execute, the two seams
//      (each with its comment), PartialOutputLeft (with its comment) and Fail text byte for byte (LF-normalized, pinned
//      SHA-256 of the pre-split blocks, origin/main b3a8986) and the only GltfExporter.WriteNew call; the shared part defines
//      none of them and has no exporter, stdout line, exit code, output hash, file access or using Bim.Gltf in code;
//      Execute still judges the output path, then generates through the shared Generate(ReadRequest(...)), then writes.
//   4. A self-test of the link and use checkers on synthetic csproj and source texts, and a fail-closed Block self-test.
// The generate_prototypes MCP bytes and the generate-prototypes CLI bytes are pinned by tests/mcp/GeneratePrototypesToolTests.cs
// and tests/cli/GeneratePrototypesTests.cs (expectations unchanged; their static pins read the moved code where it now is).
// P7-28 changed Run after the move (its catch-all writes the diagnostic guarded, then the typed line): Run is compared
// with its pre-split pin after reversing the documented delta RunDeltaP728, and the current Run block is pinned too.
// P7-29 changed Run again (the catch-all sets the exit code, then prints the typed line guarded): the second documented
// delta RunDeltaP729 is reversed first and must give the P7-28 Run pin, whose reversal gives the pre-split pin.
// P7-30 changed Run's catch (Refusal) (the exit code is set, then Fail runs guarded): the third documented delta
// RunDeltaP730 is reversed first and must give the P7-29 Run pin, whose reversals give the P7-28 and pre-split pins.
internal static class GenerateWriteLinkTests
{
    static readonly Regex ForbiddenUse = new(@"\bGenerateCommand\s*\.\s*(?:Run|Execute|Fail|PostWriteStageProbe|PostWriteDeleteProbe|PartialOutputLeft)\b|\busing\s+static\s+(?:global\s*::\s*)?(?:Bim\s*\.\s*Cli\s*\.\s*)?GenerateCommand\b",
        RegexOptions.CultureInvariant);

    // SHA-256 and length of the pre-split text (LF line ends) of Run, Execute, the P7-26 seam and the P7-27 seam (each from
    // its comment's first line through the declaration line), PartialOutputLeft (comment through declaration) and Fail
    // (origin/main b3a8986).
    static readonly (string Name, string Start, string? End, int Length, string Sha256)[] Moved =
    {
        ("Run", "    public static void Run(string[] args)\n", null, 927, "8147c177f2669b8d93a3a0278a99e3937307079e7012cae993b8dedeb3a9ea7e"),
        ("Execute", "    static void Execute(string[] args)\n", null, 3084, "53774c6868b80db0d77900b3e3db00206becf23623dd49fadf47de3086299ebd"),
        ("PostWriteStageProbe", "    // P7-26 test seam: null in production.", "    internal static Action? PostWriteStageProbe { get; set; }\n", 449, "d099596cb2b913a30eee88187c2ad4defa71562d398aae5de232d5302a8d4ae4"),
        ("PostWriteDeleteProbe", "    // P7-27 test seam: null in production.", "    internal static Action<string>? PostWriteDeleteProbe { get; set; }\n", 542, "920e09afe15cbd2360992524f15c010a60d742da9f04783f360722e86d84f26f"),
        ("PartialOutputLeft", "    // P7-27: the stderr line written before", "    internal const string PartialOutputLeft = \"generate-prototypes: could not delete partial output: \";\n", 323, "986cba92e626235e73589627fc907b9b14dd60cea03f5906dd4dc3ad83f15d58"),
        ("Fail", "    static void Fail(string code, string? detail)\n", null, 150, "0fbbe9bc83a0d8728fa0cb1aa93b4de75881bdd39d99fe7a49ebd5103c7ceed1"),
    };

    // P7-28 changed Run after the P4-27 move: the last line of its P7-25 comment paragraph and the catch-all. The documented
    // delta below, as exact (new, old) pairs, is applied in reverse to the current Run block (each new text must occur exactly
    // once, or the reversal fails closed with null); the result must be the pre-split Run pinned in Moved, so everything
    // outside the delta is still verbatim. The current block is pinned too ("Run after P7-28").
    static readonly (string New, string Old)[] RunDeltaP728 =
    {
        ("        // Refusal(InternalError) with their diagnostic written once. P7-28: the diagnostic is written on its own, guarded, so a\n"
            + "        // stderr that throws (any exception) drops only that line: the typed line still reaches stdout, exit 1, nothing escapes.\n"
            + "        catch (Exception error)\n"
            + "        {\n"
            + "            var internalError = IntentPlan.Internal(\"generate-prototypes\", error);\n"
            + "            try { Console.Error.WriteLine(internalError.Diagnostic); } catch (Exception) { }\n"
            + "            (internalError with { Diagnostic = null }).Print();\n"
            + "        }\n",
            "        // Refusal(InternalError) with their diagnostic written once.\n"
            + "        catch (Exception error) { IntentPlan.Internal(\"generate-prototypes\", error).Print(); }\n"),
    };
    const int RunAfterP728Length = 1313;
    const string RunAfterP728Sha256 = "a90fec3c9296cb1ab5f10b7ff7359be6959dddb223b56bb0f40408d86e99ce9d";

    // P7-29 changed Run's catch-all again: two more comment lines after the P7-28 ones, and the exit code set before the typed
    // line, which is printed guarded. This second documented delta is reversed first (fail closed as above); the result must
    // be the P7-28 Run pinned just above, and reversing RunDeltaP728 on that gives the pre-split Run. The current block is
    // pinned too ("Run after P7-29").
    static readonly (string New, string Old)[] RunDeltaP729 =
    {
        ("        // P7-29: the exit code is set first and the typed line is printed guarded too, so a stdout that throws (any\n"
            + "        // exception) drops only that line: exit 1, nothing escapes Run.\n",
            ""),
        ("            Environment.ExitCode = internalError.Exit;\n"
            + "            try { (internalError with { Diagnostic = null }).Print(); } catch (Exception) { }\n",
            "            (internalError with { Diagnostic = null }).Print();\n"),
    };
    const int RunAfterP729Length = 1588;
    const string RunAfterP729Sha256 = "93c83e7f5055d5a44bee6b71a41e47520572221e72b6efec5b6eec8f9b3c2b72";

    // P7-30 changed Run's catch (Refusal): three comment lines before it, and the one-line handler replaced by a block that
    // sets the exit code, then calls Fail (unchanged, pinned in Moved) guarded. This third documented delta is reversed first
    // (fail closed as above); the result must be the P7-29 Run pinned just above, and reversing RunDeltaP729 then
    // RunDeltaP728 on that gives the pre-split Run. The current block is pinned too ("Run after P7-30").
    static readonly (string New, string Old)[] RunDeltaP730 =
    {
        ("        // P7-30: a typed refusal sets the exit code first and Fail runs guarded, so a stdout that throws (any exception) while\n"
            + "        // Fail writes the typed line drops only that line: exit 1, nothing escapes Run (stderr unchanged). A pipeline refusal\n"
            + "        // (Execute's RefusedLine, not a Refusal) whose stdout write throws reaches the catch-all below: the internal error.\n"
            + "        catch (Refusal refusal)\n"
            + "        {\n"
            + "            Environment.ExitCode = 1;\n"
            + "            try { Fail(refusal.Code, refusal.Detail); } catch (Exception) { }\n"
            + "        }\n",
            "        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }\n"),
    };
    const int RunAfterP730Length = 2064;
    const string RunAfterP730Sha256 = "09c9c571b8523f3f908868d99d10e1061b074eb0d0d5d03220823f243bad5733";

    // The current Run with every documented delta reversed (P7-30, P7-29, then P7-28), or null when any reversal fails.
    internal static string? ReverseRun(string block) =>
        ReverseDelta(block, RunDeltaP730) is { } afterP729 && ReverseDelta(afterP729, RunDeltaP729) is { } afterP728 ? ReverseDelta(afterP728, RunDeltaP728) : null;

    // The block with each (new, old) pair replaced back (new -> old), or null when a new text does not occur exactly once.
    internal static string? ReverseDelta(string block, IReadOnlyList<(string New, string Old)> delta)
    {
        foreach (var (newText, oldText) in delta)
        {
            var at = block.IndexOf(newText, StringComparison.Ordinal);
            if (at < 0 || block.IndexOf(newText, at + 1, StringComparison.Ordinal) >= 0) return null;
            block = block[..at] + oldText + block[(at + newText.Length)..];
        }
        return block;
    }

    static bool IsWrite(string? path) => path is not null
        && string.Equals(path.Replace('\\', '/').Split('/')[^1], "GenerateWrite.cs", StringComparison.OrdinalIgnoreCase);

    // The Compile entries of a project text that compile the write part (by Include, a matched file or Link).
    internal static string[] WriteLinks(string projectXml, string projectDir) => DocUpdateLinkTests.Compiled(projectXml, projectDir)
        .Where(c => IsWrite(c.Include) || IsWrite(c.Link) || c.Files.Any(IsWrite))
        .Select(c => c.Include + (c.Link is null ? "" : " (Link " + c.Link + ")")).ToArray();

    // The uses of GenerateCommand.Run / .Execute / .Fail / the seams / PartialOutputLeft (or a using static of the class) in code.
    internal static string[] WriteUses(string source) =>
        ForbiddenUse.Matches(DocUpdateLinkTests.CodeOf(source)).Select(m => Regex.Replace(m.Value, @"\s+", " ")).ToArray();

    // The member references (MemberRef rows) of an assembly whose parent is the type reference ns.type and whose name is member.
    // Assumes ns.type is defined in another assembly (Bim.Gltf), so a call to it is a MemberRef whose parent is a TypeRef.
    internal static int MemberRefs(string assemblyPath, string ns, string type, string member)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var count = 0;
        foreach (var handle in metadata.MemberReferences)
        {
            var reference = metadata.GetMemberReference(handle);
            if (reference.Parent.Kind != HandleKind.TypeReference || metadata.GetString(reference.Name) != member) continue;
            var parent = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
            if (metadata.GetString(parent.Namespace) == ns && metadata.GetString(parent.Name) == type) count++;
        }
        return count;
    }

    // The field names of the top-level type ns.name, or null when the assembly does not define it.
    static string[]? FieldsOf(string assemblyPath, string ns, string name)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (!type.GetDeclaringType().IsNil || metadata.GetString(type.Namespace) != ns || metadata.GetString(type.Name) != name) continue;
            return type.GetFields().Select(f => metadata.GetString(metadata.GetFieldDefinition(f).Name)).Distinct().Order(StringComparer.Ordinal).ToArray();
        }
        return null;
    }

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
        void Check(bool condition, string name) => check(condition, "generate-prototypes write link: " + name);
        var mcpDir = Path.Combine(root, "interfaces", "mcp");
        var cliDir = Path.Combine(root, "interfaces", "cli");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // ---- 4. self-test of the checkers ----
        // Wildcards are resolved against a scratch tree holding both parts, so the self-test does not depend on the real split.
        var scratch = Path.Combine(Path.GetTempPath(), "p4-27-link-" + Guid.NewGuid().ToString("N"));
        var scratchMcp = Path.Combine(scratch, "mcp");
        Directory.CreateDirectory(scratchMcp);
        Directory.CreateDirectory(Path.Combine(scratch, "cli"));
        File.WriteAllText(Path.Combine(scratch, "cli", "GenerateCommand.cs"), "");
        File.WriteAllText(Path.Combine(scratch, "cli", "GenerateWrite.cs"), "");
        try
        {
            string Synthetic(string items) => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>";
            foreach (var (label, items, flagged) in new (string, string, bool)[]
            {
                ("a multi-line item with '\\' separators", "    <Compile\n      Include=\"..\\cli\\GenerateWrite.cs\"\n      Link=\"GenerateWrite.cs\" />", true),
                ("an item in an XML comment", "    <!-- <Compile Include=\"../cli/GenerateWrite.cs\" /> -->\n    <Compile Include=\"../cli/GenerateCommand.cs\" />", false),
                ("the second of two ';' entries", "<Compile Include=\"../cli/GenerateCommand.cs;  ../cli/GenerateWrite.cs\" />", true),
                ("another case", "<Compile Include=\"../CLI/generatewrite.CS\" />", true),
                ("a Link only", "<Compile Include=\"../cli/Other.cs\" Link=\"Sub/GenerateWrite.cs\" />", true),
                ("a wildcard matching the CLI sources", "<Compile Include=\"../cli/Generate*.cs\" />", true),
                ("a recursive wildcard", "<Compile Include=\"../**/*.cs\" />", true),
                // Negative wildcards: a resolver that flagged every wildcard would fail these.
                ("a wildcard matching only the shared part", "<Compile Include=\"../cli/GenerateC*.cs\" />", false),
                ("a wildcard matching no file", "<Compile Include=\"../cli/Nothing*.cs\" />", false),
                ("the shared part only", "<Compile Include=\"../cli/GenerateCommand.cs\" Link=\"GenerateCommand.cs\" />", false),
                ("the generate-typology write part", "<Compile Include=\"../cli/GenerateTypologyWrite.cs\" />", false),
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
            const string FailText = "    static void Fail(string code, string? detail)\n    {\n"
                + "        Console.WriteLine(FailureLine(code, detail));\n        Environment.ExitCode = 1;\n    }\n";
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
            ("a call", "GenerateCommand.Run(args);", true),
            ("a call split over lines", "Bim.Cli.GenerateCommand\n        .Run(args);", true),
            ("a method group", "Action<string, string?> f = GenerateCommand . Fail;", true),
            ("Execute by name", "GenerateCommand.Execute(args);", true),
            ("the post-write seam", "GenerateCommand.PostWriteStageProbe = null;", true),
            ("the post-write delete seam", "GenerateCommand.PostWriteDeleteProbe = null;", true),
            ("the partial-output line", "var s = GenerateCommand.PartialOutputLeft;", true),
            ("a using static", "using static Bim.Cli.GenerateCommand;\nRun(args);", true),
            ("a global using static", "using static global::Bim.Cli.GenerateCommand;", true),
            ("a line comment", "// GenerateCommand.Run(args)\nvar x = 1;", false),
            ("a block comment", "/* GenerateCommand.Run(\n */ var x = 1;", false),
            ("code after '//' inside a string", "var url = \"http://host\"; GenerateCommand.Run(args);", true),
            ("a longer member", "GenerateCommand.RunTests(); GenerateCommand.Failed(); GenerateCommand.ExecuteAll();", false),
            ("the shared document path", "var (ok, text) = GenerateCommand.Document(requestPath);", false),
            ("the other seams", "GenerateCommand.GuardedWorkProbe = null; GenerateCommand.UnguardedStageProbe = null;", false),
            ("another class's Run", "GenerateTypologyCommand.Run(args); PlanLayoutsCommand.Run(args);", false),
            // A using alias is a known blind spot of this textual check (not flagged, by design); the metadata checks of the
            // real Release Bim.Mcp.dll below cover it.
            ("a using alias (covered by the binary metadata check)", "using G = Bim.Cli.GenerateCommand;\nG.Run(args);", false),
        })
        {
            var found = WriteUses(source);
            Check(found.Length > 0 == flagged, "use checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }

        // ---- 1. the server project file and every source it compiles ----
        var sharedPath = Path.GetFullPath(Path.Combine(cliDir, "GenerateCommand.cs"));
        var writePath = Path.GetFullPath(Path.Combine(cliDir, "GenerateWrite.cs"));
        static bool Built(string path) => path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");
        var testsDir = Path.GetFullPath(Path.Combine(root, "tests")) + Path.DirectorySeparatorChar;
        var project = Path.Combine(mcpDir, "Bim.Mcp.csproj");
        var projectXml = File.ReadAllText(project);
        var compiledItems = DocUpdateLinkTests.Compiled(projectXml, mcpDir);
        var links = WriteLinks(projectXml, mcpDir);
        Check(links.Length == 0, "Bim.Mcp.csproj does not compile the generate-prototypes write part (interfaces/cli/GenerateWrite.cs) [" + string.Join(", ", links) + "]");
        Check(compiledItems.Any(c => c.Files.Any(path => string.Equals(path, sharedPath, comparison))),
            "Bim.Mcp.csproj compiles interfaces/cli/GenerateCommand.cs (the document path the generate_prototypes tool runs)");
        var compiledSources = compiledItems.SelectMany(c => c.Files)
            .Concat(Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories).Where(path => !Built(Path.GetRelativePath(mcpDir, path))))
            .Select(path => Path.GetFullPath(path)).Where(path => !path.StartsWith(testsDir, comparison) && File.Exists(path)).Distinct().ToArray();
        var users = compiledSources.Select(path => (Name: Path.GetFileName(path), Found: WriteUses(File.ReadAllText(path)))).Where(u => u.Found.Length > 0)
            .Select(u => u.Name + " " + string.Join(" ", u.Found)).ToArray();
        Check(users.Length == 0, "Bim.Mcp.csproj: no compiled source uses GenerateCommand.Run / Execute / Fail / PostWriteStageProbe / PostWriteDeleteProbe / PartialOutputLeft ["
            + string.Join(", ", users) + "]");
        var remaining = compiledSources.Where(path => Regex.IsMatch(DocUpdateLinkTests.CodeOf(File.ReadAllText(path)), @"\bGltfExporter\s*\.\s*WriteNew\s*\("))
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
        Check(compiledSources.Length > 0 && remaining.Length == 0,
            "no source compiled by Bim.Mcp.csproj calls GltfExporter.WriteNew (the server-compiled writer list is empty) [" + string.Join(", ", remaining) + "]");

        // ---- 2. the real Release binaries ----
        var verbMembers = new[] { "Run", "Execute", "Fail", "get_PostWriteStageProbe", "set_PostWriteStageProbe", "get_PostWriteDeleteProbe", "set_PostWriteDeleteProbe" };
        var shared = new[] { "Document", "Generate" };
        var mcpMethods = File.Exists(mcpDll) ? PlanLayoutsWriteLinkTests.MethodsOf(mcpDll, "Bim.Cli", "GenerateCommand") : null;
        var cliMethods = File.Exists(cliDll) ? PlanLayoutsWriteLinkTests.MethodsOf(cliDll, "Bim.Cli", "GenerateCommand") : null;
        var cliNames = cliMethods ?? Array.Empty<string>();
        var mcpNames = mcpMethods ?? Array.Empty<string>();
        Check(cliMethods is not null && verbMembers.Concat(shared).All(member => cliNames.Contains(member)),
            "the Release Bim.Cli.dll defines Bim.Cli.GenerateCommand with Run, Execute, Fail, PostWriteStageProbe, PostWriteDeleteProbe, Document and Generate (reader control) ["
            + string.Join(", ", cliNames) + "]");
        Check(mcpMethods is not null && shared.All(member => mcpNames.Contains(member)) && !verbMembers.Any(member => mcpNames.Contains(member)),
            "the Release Bim.Mcp.dll defines Bim.Cli.GenerateCommand with Document and Generate and without Run, Execute, Fail, PostWriteStageProbe or PostWriteDeleteProbe ["
            + string.Join(", ", mcpNames) + "]");
        var cliFields = File.Exists(cliDll) ? FieldsOf(cliDll, "Bim.Cli", "GenerateCommand") : null;
        var mcpFields = File.Exists(mcpDll) ? FieldsOf(mcpDll, "Bim.Cli", "GenerateCommand") : null;
        Check(cliFields is not null && cliFields.Contains("PartialOutputLeft") && cliFields.Contains("InternalError"),
            "the Release Bim.Cli.dll defines the GenerateCommand fields PartialOutputLeft and InternalError (reader control) [" + string.Join(", ", cliFields ?? Array.Empty<string>()) + "]");
        Check(mcpFields is not null && mcpFields.Contains("InternalError") && !mcpFields.Contains("PartialOutputLeft"),
            "the Release Bim.Mcp.dll defines the GenerateCommand field InternalError and not PartialOutputLeft [" + string.Join(", ", mcpFields ?? Array.Empty<string>()) + "]");
        var cliWriteRefs = File.Exists(cliDll) ? MemberRefs(cliDll, "Bim.Gltf", "GltfExporter", "WriteNew") : -1;
        var mcpWriteRefs = File.Exists(mcpDll) ? MemberRefs(mcpDll, "Bim.Gltf", "GltfExporter", "WriteNew") : -1;
        Check(cliWriteRefs >= 1, "the Release Bim.Cli.dll holds a member reference to Bim.Gltf.GltfExporter.WriteNew (reader control) [" + cliWriteRefs + "]");
        Check(mcpWriteRefs == 0, "the Release Bim.Mcp.dll holds no member reference to Bim.Gltf.GltfExporter.WriteNew [" + mcpWriteRefs + " (-1: missing dll)]");

        // ---- 3. the split ----
        Check(File.Exists(writePath), "interfaces/cli/GenerateWrite.cs holds the generate-prototypes write path (split from GenerateCommand.cs)");
        var writeText = File.Exists(writePath) ? File.ReadAllText(writePath).Replace("\r\n", "\n", StringComparison.Ordinal) : "";
        var sharedText = File.ReadAllText(sharedPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var (name, start, end, length, sha256) in Moved)
        {
            var current = Block(writeText, start, end);
            // P7-28 / P7-29 / P7-30: Run is compared after reversing its documented deltas (RunDeltaP730, RunDeltaP729, then RunDeltaP728).
            var block = name == "Run" && current is not null ? ReverseRun(current) : current;
            Check(block is not null && block.Length == length && Sha256(block) == sha256,
                "GenerateWrite.cs holds " + name + " moved verbatim" + (name == "Run" ? " (the documented P7-30, P7-29 and P7-28 deltas reversed)" : "")
                + " (" + (block?.Length ?? -1) + " chars, sha256 " + (block is null ? "-" : Sha256(block)) + ")");
            Check(!sharedText.Contains(start, StringComparison.Ordinal), "GenerateCommand.cs no longer holds " + name);
        }
        // P7-28 / P7-29 / P7-30: the current Run block is pinned, the P7-30 reversal gives the P7-29 pin, the P7-29 reversal
        // the P7-28 pin, and each delta reversal fails closed (self-tests on the real block).
        var runPin = Moved.Single(m => m.Name == "Run");
        var runNow = Block(writeText, runPin.Start, runPin.End);
        Check(runNow is not null && runNow.Length == RunAfterP730Length && Sha256(runNow) == RunAfterP730Sha256,
            "GenerateWrite.cs holds Run after P7-30 (" + (runNow?.Length ?? -1) + " chars, sha256 " + (runNow is null ? "-" : Sha256(runNow)) + ")");
        var runP729 = runNow is null ? null : ReverseDelta(runNow, RunDeltaP730);
        Check(runP729 is not null && runP729.Length == RunAfterP729Length && Sha256(runP729) == RunAfterP729Sha256,
            "GenerateWrite.cs Run with the documented P7-30 delta reversed is Run after P7-29 (" + (runP729?.Length ?? -1) + " chars, sha256 " + (runP729 is null ? "-" : Sha256(runP729)) + ")");
        var runP728 = runP729 is null ? null : ReverseDelta(runP729, RunDeltaP729);
        Check(runP728 is not null && runP728.Length == RunAfterP728Length && Sha256(runP728) == RunAfterP728Sha256,
            "GenerateWrite.cs Run with the documented P7-30 and P7-29 deltas reversed is Run after P7-28 (" + (runP728?.Length ?? -1) + " chars, sha256 " + (runP728 is null ? "-" : Sha256(runP728)) + ")");
        if (runNow is not null && runP729 is not null && runP728 is not null)
        {
            bool Pinned(string? text) => text is not null && text.Length == runPin.Length && Sha256(text) == runPin.Sha256;
            const string Outside = "        try { Execute(args); }\n";
            Check(runNow.Contains(Outside, StringComparison.Ordinal)
                && !Pinned(ReverseRun(runNow.Replace(Outside, "        try { Execute(argz); }\n", StringComparison.Ordinal))),
                "delta self-test: a one-character change outside the P7-28 / P7-29 / P7-30 deltas no longer reverses to the pre-split Run pin");
            foreach (var (label, stage, delta) in new (string, string, (string New, string Old)[])[] { ("P7-30", runNow, RunDeltaP730), ("P7-29", runP729, RunDeltaP729), ("P7-28", runP728, RunDeltaP728) })
            {
                foreach (var (index, pair) in delta.Select((p, i) => (i, p)))
                {
                    Check(stage.Contains(pair.New, StringComparison.Ordinal) && ReverseDelta(stage.Replace(pair.New, "", StringComparison.Ordinal), delta) is null,
                        "delta self-test: " + label + " delta text " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + " missing fails closed (null)");
                    Check(ReverseDelta(stage + pair.New, delta) is null,
                        "delta self-test: " + label + " delta text " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + " appearing twice fails closed (null)");
                }
            }
            Check(ReverseRun(runNow.Replace(RunDeltaP729[1].New, "", StringComparison.Ordinal)) is null && ReverseRun(runNow.Replace(RunDeltaP730[0].New, "", StringComparison.Ordinal)) is null
                && ReverseRun(runP729) is null && ReverseRun(runP728) is null,
                "delta self-test: the whole-Run reversal fails closed when a P7-29 or P7-30 delta text is missing or P7-30 / P7-29 is not applied");
            Check(Pinned(ReverseDelta(runP728, RunDeltaP728)) && Pinned(ReverseRun(runNow)), "delta self-test: the unchanged block reverses to the pre-split Run pin (positive control)");
        }
        var write = Compact(DocUpdateLinkTests.CodeOf(writeText));
        var sharedCode = Compact(DocUpdateLinkTests.CodeOf(sharedText));
        Check(Count(write, "namespaceBim.Cli;") == 1 && Count(write, "internalstaticpartialclassGenerateCommand") == 1
            && Count(sharedCode, "namespaceBim.Cli;") == 1 && Count(sharedCode, "internalstaticpartialclassGenerateCommand") == 1,
            "both files are parts of internal static partial class Bim.Cli.GenerateCommand (callers unchanged)");
        var definitions = new[] { "publicstaticvoidRun(string[]args)", "staticvoidExecute(string[]args)", "staticvoidFail(stringcode,string?detail)",
            "internalstaticAction?PostWriteStageProbe{get;set;}", "internalstaticAction<string>?PostWriteDeleteProbe{get;set;}", "internalconststringPartialOutputLeft=" };
        var misplaced = definitions.Where(d => Count(write, d) != 1 || Count(sharedCode, d) != 0).ToArray();
        Check(misplaced.Length == 0, "GenerateWrite.cs defines Run, Execute, Fail, PostWriteStageProbe, PostWriteDeleteProbe and PartialOutputLeft once and GenerateCommand.cs none of them ["
            + string.Join(", ", misplaced) + "]");
        Check(Count(write, "GltfExporter.WriteNew(bytes,output)") == 1 && Count(write, "WriteNew") == 1,
            "GenerateWrite.cs holds the verb's single exclusive write (GltfExporter.WriteNew)");
        // SHA256 itself stays in the shared part (the derived project id hashes the canonical request); the output hash does not.
        var writerApis = new[] { "GltfExporter", "WriteNew", "Console.WriteLine(", "Console.Out", "Environment.ExitCode", "SHA256.HashData(bytes)", "File.", "FileStream",
                "usingBim.Gltf;", "PostWriteStageProbe", "PostWriteDeleteProbe", "PartialOutputLeft" }
            .Where(api => sharedCode.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(sharedCode.Length > 0 && writerApis.Length == 0,
            "GenerateCommand.cs (compiled by the server) holds no writer: no exporter, stdout line, exit code, output hash, file access, post-write seam or using Bim.Gltf in code ["
            + string.Join(", ", writerApis) + "]");
        Check(Count(sharedCode, "Generate(ReadRequest(requestPath))") == 1 && Count(sharedCode, "Generate(ReadRequest(") == 1 && Count(write, "Generate(ReadRequest(args[1]))") == 1,
            "Document (shared) and Execute (write part) each call Generate(ReadRequest(...)) once");
        var order = new[] { "GltfExportCommand.OutputProblem(output,[\".json\"],out_)", "Generate(ReadRequest(args[1]))", "GltfExporter.WriteNew(bytes,output)",
                "PostWriteStageProbe?.Invoke();", "PostWriteDeleteProbe?.Invoke(output);File.Delete(output);", "Console.Error.WriteLine(PartialOutputLeft+output);" }
            .Select(fragment => write.IndexOf(fragment, StringComparison.Ordinal)).ToArray();
        Check(order.All(at => at >= 0) && order.Zip(order.Skip(1)).All(pair => pair.First < pair.Second),
            "Execute still judges the output path, generates through the shared Generate, writes exclusively, runs the post-write seam, then the removing and reporting catch ["
            + string.Join(", ", order) + "]");
    }
}
