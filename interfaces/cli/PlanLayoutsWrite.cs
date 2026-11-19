#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;
using Bim.Gltf;

namespace Bim.Cli;

// P4-25: the plan-layouts verb's write path, moved verbatim from PlanLayoutsCommand.cs (the contract is its header):
// Run, Execute (options, output path, the shared Plan, the exclusive GltfExporter.WriteNew write, the success line) and
// Fail. Compiled by the CLI (default glob), tests/cli and tests/mcp; interfaces/mcp/Bim.Mcp.csproj links only the other
// part (the dry plan of the read-only MCP tool plan_layouts), so the server binary carries no writer of this verb.
internal static partial class PlanLayoutsCommand
{
    public static void Run(string[] args)
    {
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
    }

    static void Execute(string[] args)
    {
        if (args.Length < 3 || args[1].Length == 0 || args[2].Length == 0 || args[1].StartsWith("--", StringComparison.Ordinal) || args[2].StartsWith("--", StringComparison.Ordinal))
            throw new Refusal(UsageCode, Usage);
        var (count, seed, packId) = ParseOptions(args);
        var output = args[2];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string badOutput) throw new Refusal(badOutput);
        var (set, json, rules) = Plan(args[1], count, seed, packId);
        if (set.Status == AlternativeSetStatus.Refused)
        {
            Console.WriteLine(RefusedLine(set));
            Environment.ExitCode = 1;
            return;
        }
        var bytes = new UTF8Encoding(false).GetBytes(json);
        try { GltfExporter.WriteNew(bytes, output); }
        catch (GltfExportException error) { throw new Refusal(error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", error.GetType().Name); }
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var setStatus = set.Status == AlternativeSetStatus.Ranked ? "ranked" : "infeasible";
        if (packId is not null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "succeeded",
                kind = "plan-layouts",
                scope = "proposal_only",
                persisted = false,
                set_status = setStatus,
                requested = count,
                seed,
                ranked = set.Ranked.Count,
                alternatives = set.Alternatives.Count,
                bytes = bytes.LongLength,
                sha256,
                pack = packId,
                pack_rules = rules!.Rules.Count,
            }));
            return;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            kind = "plan-layouts",
            scope = "proposal_only",
            persisted = false,
            set_status = setStatus,
            requested = count,
            seed,
            ranked = set.Ranked.Count,
            alternatives = set.Alternatives.Count,
            bytes = bytes.LongLength,
            sha256,
        }));
    }

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(FailureLine(code, detail));
        Environment.ExitCode = 1;
    }
}
