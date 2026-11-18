#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Gltf;

namespace Bim.Cli;

// P4-23: the write path of the P4-15 CLI verb doc-realized (the contract is in DocRealizedCommand.cs, the shared part of
// this partial class): the five flags, the never-overwrite output path (GltfExportCommand.OutputProblem, .json), then the
// shared Build, then the exclusive write (GltfExporter.WriteNew), the success line on stdout and the exit code. Compiled by
// the CLI (and tests/cli); never linked into the MCP server, whose read-only doc_realized tool runs DryRealized only.
internal static partial class DocRealizedCommand
{
    public const string Usage = "usage: doc-realized --record run.json --fixture ir.json --assignments assignments.json --document revit-unique-id --output realized.json (--output: a new file, never overwritten)";
    public const string UsageCode = "doc_realized_usage";

    static readonly string[] Flags = { "--record", "--fixture", "--assignments", "--document", "--output" };

    public static void Run(string[] args)
    {
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
    }

    static void Execute(string[] args)
    {
        if (!TryParse(args, out var values)) throw new Refusal(UsageCode);
        var output = values["--output"];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string badOutput) throw new Refusal(badOutput);
        var (realized, json) = Build(values["--record"], values["--fixture"], values["--assignments"], values["--document"]);

        var bytes = new UTF8Encoding(false).GetBytes(json);
        var line = JsonSerializer.Serialize(new
        {
            status = "succeeded",
            output,
            entries = realized.Count,
            bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        });
        try { GltfExporter.WriteNew(bytes, output); }
        catch (GltfExportException error) { throw new Refusal(error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", error.GetType().Name); }
        Console.WriteLine(line);
    }

    // The five flags, each exactly once, in any order, each with a non-empty value that is not flag-shaped ("--...").
    static bool TryParse(string[] args, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length != 1 + 2 * Flags.Length) return false;
        for (var i = 1; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (!Flags.Contains(args[i], StringComparer.Ordinal) || !values.TryAdd(args[i], value)) return false;
        }
        return Flags.All(values.ContainsKey);
    }

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(Line(code, detail));
        Environment.ExitCode = 1;
    }
}
