#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Generative;
using Bim.Gltf;

namespace Bim.Cli;

// P4-26: the generate-typology verb's write path, moved verbatim from GenerateTypologyCommand.cs (the contract is its
// header): Run, Execute (output path, the shared Generate(ReadRequest(...)), the exclusive GltfExporter.WriteNew write, the
// P3-25 removal of the just-written file, the success line), the P3-25 seam PostWriteProbe and Fail. Compiled by the CLI
// (default glob); interfaces/mcp/Bim.Mcp.csproj links only GenerateTypologyCommand.cs, so the MCP server binary carries no
// generate-typology writer (tests/mcp/GenerateTypologyWriteLinkTests.cs).
internal static partial class GenerateTypologyCommand
{
    public static void Run(string[] args)
    {
        try { Execute(args); }
        // P3-30: a typed refusal sets the exit code first and Fail runs guarded, so a stdout that throws (any exception) while
        // Fail writes the typed line drops only that line: exit 1, nothing escapes Run (stderr unchanged). A pipeline refusal
        // (Execute's RefusedLine, not a Refusal) whose stdout write throws reaches the catch-all below: the internal error.
        catch (Refusal refusal)
        {
            Environment.ExitCode = 1;
            try { Fail(refusal.Code, refusal.Detail, refusal.Skill); } catch (Exception) { }
        }
        // P3-24: any other exception in Execute (for example in Generate's parse / pack-load stretch, outside its guarded
        // pipeline block; forced in tests through the P3-23 seam) prints the typed {"status":"failed","code":InternalError}
        // line with IntentPlan's stderr diagnostic once, exit 1, instead of escaping to Program's generic catch. Before the
        // write no output file exists; P3-25: Execute removes the file it just wrote before an exception after
        // GltfExporter.WriteNew succeeded reaches this catch. The guarded block's own failures already arrive above as Refusal(InternalError) with their
        // diagnostic written once. P3-28: the diagnostic is written on its own, guarded, so a stderr that throws (any
        // exception) drops only that line: the typed line still reaches stdout, exit 1, nothing escapes.
        // P3-30: the exit code is set first and the typed line is printed guarded too, so a stdout that throws (any
        // exception) drops only that line: exit 1, nothing escapes Run.
        catch (Exception error)
        {
            var internalError = IntentPlan.Internal("generate-typology", error);
            try { Console.Error.WriteLine(internalError.Diagnostic); } catch (Exception) { }
            Environment.ExitCode = internalError.Exit;
            try { (internalError with { Diagnostic = null }).Print(); } catch (Exception) { }
        }
    }

    static void Execute(string[] args)
    {
        if (args.Length != 3 || args[1].Length == 0 || args[2].Length == 0) throw new Refusal(UsageCode, Usage);
        var output = args[2];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string badOutput) throw new Refusal(badOutput);
        var generated = Generate(ReadRequest(args[1]));
        if (generated.RefusedLine is { } refused)
        {
            Console.WriteLine(refused);
            Environment.ExitCode = 1;
            return;
        }
        var parsed = generated.Parsed;
        var result = generated.Result;
        var bytes = new UTF8Encoding(false).GetBytes(generated.Json);
        try { GltfExporter.WriteNew(bytes, output); }
        catch (GltfExportException error) { throw new Refusal(error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", error.GetType().Name); }
        // P3-25: from here on the file exists and is this run's own (WriteNew created it exclusively, so a pre-existing
        // output was already refused as output_exists above and is not reached here; only a concurrent process replacing
        // the file between WriteNew and the Delete below, a theoretical race, could make the Delete remove another's file).
        // Any exception before the success line is complete removes the just-written file again, then propagates unchanged
        // to Run's catch-all (the typed InternalError line with its diagnostic once). P3-26: a failed removal (any exception,
        // IO or not) writes one PartialOutputLeft line naming the file left behind to stderr (a failure writing it is
        // swallowed too) and is otherwise swallowed, so the original exception is the one rethrown and reported. If stdout
        // itself throws part-way through the success line, that partial line may precede the typed line on stdout.
        try
        {
            PostWriteProbe?.Invoke();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var pipelineStatus = result.Status == PrototypePipelineStatus.Ranked ? "ranked" : "infeasible";
            var projectId = parsed.ProjectId.ToString("D", CultureInfo.InvariantCulture);
            var acceptedCount = result.Alternatives.Count(alternative => alternative.Accepted);
            if (parsed.Composed)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "succeeded",
                    scope = "proposal_only",
                    persisted = false,
                    pipeline_status = pipelineStatus,
                    rules = "composed",
                    pack = parsed.Pack,
                    code_pack = parsed.CodePack,
                    skills = parsed.Skills ?? Array.Empty<string>(),
                    project_id = projectId,
                    alternatives = result.Alternatives.Count,
                    accepted = acceptedCount,
                    bytes = bytes.LongLength,
                    sha256,
                }));
                return;
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "succeeded",
                scope = "proposal_only",
                persisted = false,
                pipeline_status = pipelineStatus,
                pack = parsed.Pack,
                code_pack = parsed.CodePack,
                project_id = projectId,
                alternatives = result.Alternatives.Count,
                accepted = acceptedCount,
                bytes = bytes.LongLength,
                sha256,
            }));
        }
        catch (Exception)
        {
            try { PostWriteRemovalProbe?.Invoke(output); File.Delete(output); }
            catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } }
            throw;
        }
    }

    // P3-25 test seam: null in production. tests/cli/GenerateTypologyTests.cs section 10 sets it (by reflection) to an
    // Action that throws, so a non-refusal exception arises in Execute right after GltfExporter.WriteNew succeeded; it is
    // reset after the test.
    internal static Action? PostWriteProbe { get; set; }

    // P3-26 test seam: null in production. tests/cli/GenerateTypologyTests.cs section 11 sets it (by reflection) to an
    // Action<string> that throws, so the post-write cleanup's File.Delete(output) is never reached (as if the delete
    // failed); Execute calls it with the output path right before that delete. It is reset after the test. Not named
    // PostWriteProbe (P3-25's pin) or PostWriteDeleteProbe (GenerateCommand's P7-27 seam, whose pin counts that name over
    // interfaces/).
    internal static Action<string>? PostWriteRemovalProbe { get; set; }

    // P3-26: the stderr line written before the internal-error diagnostic when the post-write cleanup could not delete the
    // just-written output (followed by the output path exactly as given on the command line).
    internal const string PartialOutputLeft = "generate-typology: could not delete partial output: ";

    static void Fail(string code, string? detail, string? skill = null)
    {
        Console.WriteLine(FailureLine(code, detail, skill));
        Environment.ExitCode = 1;
    }
}
