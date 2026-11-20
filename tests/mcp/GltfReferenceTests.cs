using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;

// P4-28: after P4-24..P4-27 the MCP server compiles no Bim.Gltf writer (no source it compiles calls GltfExporter at all),
// so the Release Bim.Mcp.dll must reference no Bim.Gltf.GltfExporter member, and nothing in it needs the Bim.Gltf assembly.
// Measured on the real Release binaries (System.Reflection.Metadata, the P4-27 MemberRef approach):
//   1. Bim.Mcp.dll holds zero MemberRefs whose parent is the TypeRef Bim.Gltf.GltfExporter; Bim.Cli.dll holds >= 1 (control).
//   2. Bim.Mcp.dll holds no AssemblyRef named Bim.Gltf, no TypeRef in namespace Bim.Gltf or resolved through that
//      AssemblyRef, and no MemberRef on such a TypeRef; Bim.Cli.dll holds the AssemblyRef and TypeRefs (control).
//   3. So no code needs the ProjectReference to adapters/gltf/Bim.Gltf/Bim.Gltf.csproj in interfaces/mcp/Bim.Mcp.csproj.
//      It is KEPT, for a measured packaging reason: removing it (relay247 run 44673074883, every other gate and the
//      Release --warnaserror build of interfaces/mcp green without it) drops mcp/Bim.Gltf.dll from the package, and the
//      P10-18 package layout pin (tests/cli/PackageLayoutPackTests.cs, requiredFirstParty) requires mcp/Bim.Gltf.dll.
//      Asserted: the reference is present only while that pin names mcp/Bim.Gltf.dll (dropping both is one follow-up),
//      and the Release Bim.Mcp.deps.json names Bim.Gltf exactly when the csproj references it (Bim.Cli.deps.json control).
//      The pin is read from the requiredFirstParty initializer itself (comments stripped), fail closed if it is not found.
//   4. A self-test of the ProjectReference reader on synthetic csproj texts.
//   5. A self-test of the requiredFirstParty reader (comment-only name unpinned, initializer name pinned, missing -> error).
//   6. No #US user string of Bim.Mcp.dll contains Bim.Gltf (no reflection by name); reader control: "tools/list" is found.
// tools/list and every MCP tool's bytes are pinned by the existing sections (tests/mcp/Program.cs and the *ToolTests.cs
// files), unchanged.
internal static class GltfReferenceTests
{
    internal sealed record Measured(string[] AssemblyRefs, string[] TypeRefs, string[] MemberRefs, int ExporterMemberRefs);

    // The Bim.Gltf references of an assembly: AssemblyRefs named Bim.Gltf, TypeRefs in namespace Bim.Gltf (or nested in one,
    // or resolved through a Bim.Gltf AssemblyRef), MemberRefs whose parent is such a TypeRef, and the MemberRefs on
    // Bim.Gltf.GltfExporter.
    internal static Measured Measure(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var assemblies = metadata.AssemblyReferences
            .Where(h => metadata.GetString(metadata.GetAssemblyReference(h).Name) == "Bim.Gltf").ToHashSet();
        string Name(TypeReferenceHandle handle)
        {
            var type = metadata.GetTypeReference(handle);
            var name = metadata.GetString(type.Name);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? Name((TypeReferenceHandle)type.ResolutionScope) + "+" + name
                : metadata.GetString(type.Namespace) + "." + name;
        }
        bool InGltf(TypeReferenceHandle handle)
        {
            var type = metadata.GetTypeReference(handle);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference) return InGltf((TypeReferenceHandle)type.ResolutionScope);
            var ns = metadata.GetString(type.Namespace);
            return ns == "Bim.Gltf" || ns.StartsWith("Bim.Gltf.", StringComparison.Ordinal)
                || (type.ResolutionScope.Kind == HandleKind.AssemblyReference && assemblies.Contains((AssemblyReferenceHandle)type.ResolutionScope));
        }
        var typeRefs = metadata.TypeReferences.Where(InGltf).Select(Name).Distinct().Order(StringComparer.Ordinal).ToArray();
        var memberRefs = new List<string>();
        var exporter = 0;
        foreach (var handle in metadata.MemberReferences)
        {
            var reference = metadata.GetMemberReference(handle);
            // Only TypeRef parents are counted here (Bim.Gltf has no generic types, so no TypeSpec parent exists today); the
            // exporter-only count is the P4-28 headline, and the complete gate is the zero AssemblyRef / TypeRef / MemberRef
            // check in Run (no AssemblyRef to Bim.Gltf means no TypeSpec over a Bim.Gltf type can exist either).
            if (reference.Parent.Kind != HandleKind.TypeReference) continue;
            var parent = (TypeReferenceHandle)reference.Parent;
            if (!InGltf(parent)) continue;
            var full = Name(parent);
            memberRefs.Add(full + "::" + metadata.GetString(reference.Name));
            if (full == "Bim.Gltf.GltfExporter") exporter++;
        }
        return new Measured(assemblies.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray(), typeRefs,
            memberRefs.Distinct().Order(StringComparer.Ordinal).ToArray(), exporter);
    }

    static string Show(Measured m) => "assemblyRefs=[" + string.Join(", ", m.AssemblyRefs) + "] typeRefs=[" + string.Join(", ", m.TypeRefs)
        + "] memberRefs=[" + string.Join(", ", m.MemberRefs) + "] GltfExporter memberRefs=" + m.ExporterMemberRefs;

    // The ProjectReference Include values of a project text that name Bim.Gltf.csproj ('\' or '/', any case).
    internal static string[] GltfProjectReferences(string projectXml) => XDocument.Parse(projectXml).Descendants()
        .Where(e => e.Name.LocalName == "ProjectReference")
        .Select(e => (string?)e.Attribute("Include") ?? "")
        .SelectMany(include => include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(include => string.Equals(include.Replace('\\', '/').Split('/')[^1], "Bim.Gltf.csproj", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    static readonly System.Text.RegularExpressions.Regex RequiredFirstParty = new(
        @"\brequiredFirstParty\s*=\s*new\s*(?:string\s*)?\[\s*\]\s*\{(?<items>[^{}]*)\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex StringItem = new(@"""(?<value>[^""\\\r\n]*)""", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The entries of the P10-18 `requiredFirstParty = new[] { ... }` initializer in tests/cli/PackageLayoutPackTests.cs
    // (comments stripped, literals kept), or null when there is not exactly one such initializer (fail closed).
    internal static string[]? RequiredFirstPartyOf(string source)
    {
        var matches = RequiredFirstParty.Matches(DocUpdateLinkTests.CodeOf(source));
        return matches.Count != 1 ? null
            : StringItem.Matches(matches[0].Groups["items"].Value).Select(m => m.Groups["value"].Value).ToArray();
    }

    // The #US (user string heap) entries of an assembly: ldstr literals, which reflection-by-name would need.
    internal static string[] UserStrings(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var strings = new List<string>();
        var handle = default(UserStringHandle);
        while (true)
        {
            handle = metadata.GetNextHandle(handle);
            if (handle.IsNil) break;
            strings.Add(metadata.GetUserString(handle));
        }
        return strings.ToArray();
    }

    // The library names of a deps.json ("Bim.Gltf/1.0.0" -> "Bim.Gltf").
    static string[] DepsLibraries(string depsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        return document.RootElement.TryGetProperty("libraries", out var libraries)
            ? libraries.EnumerateObject().Select(p => p.Name.Split('/')[0]).ToArray()
            : Array.Empty<string>();
    }

    public static void Run(string root, string mcpDll, string cliDll, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P4-28 " + name);

        // ---- 1/2. the real Release binaries ----
        Check(File.Exists(mcpDll) && File.Exists(cliDll), "the Release Bim.Mcp.dll and Bim.Cli.dll exist [" + mcpDll + ", " + cliDll + "]");
        var cli = Measure(cliDll);
        var mcp = Measure(mcpDll);
        Console.WriteLine("NOTE P4-28 Bim.Cli.dll Bim.Gltf references: " + Show(cli));
        Console.WriteLine("NOTE P4-28 Bim.Mcp.dll Bim.Gltf references: " + Show(mcp));
        Check(cli.ExporterMemberRefs >= 1, "the Release Bim.Cli.dll holds a member reference to Bim.Gltf.GltfExporter (reader control) [" + Show(cli) + "]");
        Check(cli.AssemblyRefs.Length == 1 && cli.TypeRefs.Contains("Bim.Gltf.GltfExporter"),
            "the Release Bim.Cli.dll references the Bim.Gltf assembly and the TypeRef Bim.Gltf.GltfExporter (reader control) [" + Show(cli) + "]");
        Check(mcp.ExporterMemberRefs == 0, "the Release Bim.Mcp.dll holds no member reference to any Bim.Gltf.GltfExporter member [" + Show(mcp) + "]");
        Check(mcp.AssemblyRefs.Length == 0 && mcp.TypeRefs.Length == 0 && mcp.MemberRefs.Length == 0,
            "the Release Bim.Mcp.dll holds no AssemblyRef, TypeRef or MemberRef into Bim.Gltf [" + Show(mcp) + "]");

        // ---- 3. the code-unused ProjectReference is kept only for the package layout pin ----
        var project = Path.Combine(root, "interfaces", "mcp", "Bim.Mcp.csproj");
        var gltfRefs = GltfProjectReferences(File.ReadAllText(project));
        var layoutPin = Path.Combine(root, "tests", "cli", "PackageLayoutPackTests.cs");
        var required = File.Exists(layoutPin) ? RequiredFirstPartyOf(File.ReadAllText(layoutPin)) : null;
        Check(required is not null && required.Contains("mcp/Bim.Mcp.dll"),
            "tests/cli/PackageLayoutPackTests.cs holds exactly one P10-18 requiredFirstParty initializer (read fail-closed; it names mcp/Bim.Mcp.dll) ["
            + string.Join(", ", required ?? Array.Empty<string>()) + "]");
        var pinned = required is not null && required.Contains("mcp/Bim.Gltf.dll");
        Console.WriteLine("NOTE P4-28 Bim.Mcp.csproj ProjectReference to Bim.Gltf.csproj: [" + string.Join(", ", gltfRefs) + "]; unused by the binary ("
            + Show(mcp) + "); kept because the P10-18 package layout pin requires mcp/Bim.Gltf.dll: " + pinned);
        Check(gltfRefs.Length <= 1 && (gltfRefs.Length == 0 || pinned),
            "Bim.Mcp.csproj references Bim.Gltf.csproj at most once, and only while tests/cli/PackageLayoutPackTests.cs pins mcp/Bim.Gltf.dll in the package ["
            + string.Join(", ", gltfRefs) + "; pinned " + pinned + "]");
        var mcpDeps = Path.Combine(Path.GetDirectoryName(mcpDll)!, "Bim.Mcp.deps.json");
        var cliDeps = Path.Combine(Path.GetDirectoryName(cliDll)!, "Bim.Cli.deps.json");
        var mcpLibraries = File.Exists(mcpDeps) ? DepsLibraries(mcpDeps) : null;
        var cliLibraries = File.Exists(cliDeps) ? DepsLibraries(cliDeps) : null;
        Check(cliLibraries is not null && cliLibraries.Contains("Bim.Gltf"), "the Release Bim.Cli.deps.json names Bim.Gltf (reader control) [" + string.Join(", ", cliLibraries ?? Array.Empty<string>()) + "]");
        Check(mcpLibraries is not null && mcpLibraries.Contains("Bim.Gltf") == (gltfRefs.Length == 1) && mcpLibraries.Contains("Bim.Core") && mcpLibraries.Contains("Bim.Generative"),
            "the Release Bim.Mcp.deps.json names Bim.Core and Bim.Generative, and Bim.Gltf exactly when Bim.Mcp.csproj references it ["
            + string.Join(", ", mcpLibraries ?? Array.Empty<string>()) + "]");

        // ---- 4. self-test of the ProjectReference reader ----
        Check(GltfProjectReferences("""<Project><ItemGroup><ProjectReference Include="../../adapters/gltf/Bim.Gltf/Bim.Gltf.csproj" /></ItemGroup></Project>""").Length == 1
            && GltfProjectReferences("""<Project><ItemGroup><ProjectReference Include="..\..\adapters\gltf\Bim.Gltf\BIM.GLTF.CSPROJ" /></ItemGroup></Project>""").Length == 1
            && GltfProjectReferences("""<Project><ItemGroup><ProjectReference Include="../a/Bim.Core.csproj; ../b/Bim.Gltf.csproj" /></ItemGroup></Project>""").Length == 1
            && GltfProjectReferences("""<Project><ItemGroup><ProjectReference Include="../../core/Bim.Core/Bim.Core.csproj" /><ProjectReference Include="../x/Bim.Gltf.Tests.csproj" /></ItemGroup></Project>""").Length == 0,
            "the ProjectReference reader finds Bim.Gltf.csproj by file name ('\\' or '/', any case, ';' lists) and nothing else");
        // ---- 5. self-test of the P10-18 initializer reader ----
        var inComment = "// \"mcp/Bim.Gltf.dll\" was pinned\nvar requiredFirstParty = new[] { \"cli/Bim.Gltf.dll\", /* \"mcp/Bim.Gltf.dll\" */ \"mcp/Bim.Mcp.dll\" };\n";
        var inInitializer = "var requiredFirstParty = new[] { \"cli/Bim.Gltf.dll\",\n    \"mcp/Bim.Gltf.dll\", \"mcp/Bim.Mcp.dll\" };\n";
        var commentedOnly = "// var requiredFirstParty = new[] { \"mcp/Bim.Gltf.dll\" };\nvar other = new[] { \"mcp/Bim.Gltf.dll\" };\n";
        Check(RequiredFirstPartyOf(inComment) is { } a && !a.Contains("mcp/Bim.Gltf.dll") && a.Contains("mcp/Bim.Mcp.dll")
            && RequiredFirstPartyOf(inInitializer) is { } b && b.Contains("mcp/Bim.Gltf.dll")
            && RequiredFirstPartyOf(commentedOnly) is null
            && RequiredFirstPartyOf(inInitializer + inInitializer) is null,
            "the requiredFirstParty reader: a name only in a comment is unpinned, in the initializer pinned; a missing (or commented-out) or duplicated initializer is an error (null)");

        // ---- 6. no reflection-by-name into Bim.Gltf: no #US user string of Bim.Mcp.dll mentions Bim.Gltf ----
        // Control: the same reader finds the server's own "tools/list" literal in Bim.Mcp.dll. There is no honest Bim.Gltf-side
        // positive control (no first-party binary loads Bim.Gltf by name), so the control proves the reader, not the pattern.
        var mcpStrings = UserStrings(mcpDll);
        var gltfStrings = mcpStrings.Where(s => s.Contains("Bim.Gltf", StringComparison.OrdinalIgnoreCase)).ToArray();
        Check(mcpStrings.Contains("tools/list"), "the #US reader finds the \"tools/list\" literal in the Release Bim.Mcp.dll (reader control) [" + mcpStrings.Length + " strings]");
        Check(gltfStrings.Length == 0, "no #US user string of the Release Bim.Mcp.dll contains Bim.Gltf (no reflection by name) [" + string.Join(", ", gltfStrings) + "]");
    }
}
