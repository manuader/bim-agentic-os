using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;

// P4-28: after P4-24..P4-27 the MCP server compiles no Bim.Gltf writer (no source it compiles calls GltfExporter at all),
// so the Release Bim.Mcp.dll must reference no Bim.Gltf.GltfExporter member, and nothing in it needs the Bim.Gltf assembly.
// Measured on the real Release binaries (System.Reflection.Metadata, the P4-27 MemberRef approach):
//   1. Bim.Mcp.dll holds zero MemberRefs whose parent is the TypeRef Bim.Gltf.GltfExporter; Bim.Cli.dll holds >= 1 (control).
//   2. Bim.Mcp.dll holds no AssemblyRef named Bim.Gltf, no TypeRef in namespace Bim.Gltf or resolved through that
//      AssemblyRef, and no MemberRef on such a TypeRef; Bim.Cli.dll holds the AssemblyRef and TypeRefs (control).
//   3. So the ProjectReference to adapters/gltf/Bim.Gltf/Bim.Gltf.csproj was unused: it is removed from
//      interfaces/mcp/Bim.Mcp.csproj (any '\' or '/', any case), and the Release Bim.Mcp.deps.json no longer names
//      Bim.Gltf while Bim.Cli.deps.json still does (control). That the linked sources compile without it is shown by the
//      verify.ps1 Release --warnaserror build of interfaces/mcp itself.
//   4. A self-test of the ProjectReference reader on synthetic csproj texts.
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

        // ---- 3. the unused ProjectReference is gone ----
        var project = Path.Combine(root, "interfaces", "mcp", "Bim.Mcp.csproj");
        var gltfRefs = GltfProjectReferences(File.ReadAllText(project));
        Check(gltfRefs.Length == 0, "Bim.Mcp.csproj has no ProjectReference to Bim.Gltf.csproj (unused: " + Show(mcp) + ") [" + string.Join(", ", gltfRefs) + "]");
        var mcpDeps = Path.Combine(Path.GetDirectoryName(mcpDll)!, "Bim.Mcp.deps.json");
        var cliDeps = Path.Combine(Path.GetDirectoryName(cliDll)!, "Bim.Cli.deps.json");
        var mcpLibraries = File.Exists(mcpDeps) ? DepsLibraries(mcpDeps) : null;
        var cliLibraries = File.Exists(cliDeps) ? DepsLibraries(cliDeps) : null;
        Check(cliLibraries is not null && cliLibraries.Contains("Bim.Gltf"), "the Release Bim.Cli.deps.json names Bim.Gltf (reader control) [" + string.Join(", ", cliLibraries ?? Array.Empty<string>()) + "]");
        Check(mcpLibraries is not null && !mcpLibraries.Contains("Bim.Gltf") && mcpLibraries.Contains("Bim.Core") && mcpLibraries.Contains("Bim.Generative"),
            "the Release Bim.Mcp.deps.json names Bim.Core and Bim.Generative and not Bim.Gltf [" + string.Join(", ", mcpLibraries ?? Array.Empty<string>()) + "]");

        // ---- 4. self-test of the ProjectReference reader ----
        Check(GltfProjectReferences("""<Project><ItemGroup><ProjectReference Include="../../adapters/gltf/Bim.Gltf/Bim.Gltf.csproj" /></ItemGroup></Project>""").Length == 1
            && GltfProjectReferences("""<Project><ItemGroup><ProjectReference Include="..\..\adapters\gltf\Bim.Gltf\BIM.GLTF.CSPROJ" /></ItemGroup></Project>""").Length == 1
            && GltfProjectReferences("""<Project><ItemGroup><ProjectReference Include="../a/Bim.Core.csproj; ../b/Bim.Gltf.csproj" /></ItemGroup></Project>""").Length == 1
            && GltfProjectReferences("""<Project><ItemGroup><ProjectReference Include="../../core/Bim.Core/Bim.Core.csproj" /><ProjectReference Include="../x/Bim.Gltf.Tests.csproj" /></ItemGroup></Project>""").Length == 0,
            "the ProjectReference reader finds Bim.Gltf.csproj by file name ('\\' or '/', any case, ';' lists) and nothing else");
    }
}
