using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
namespace Bim.Core;

// P1-07 command.batch: stub API only (tests first); the implementation follows.
public sealed record RevitBatchStep(int Index, string OperationId, string Command, RevitCommandArgs Args, string ArgsJson);
public sealed record CommandBatchArgs(IReadOnlyList<RevitBatchStep> Steps) : RevitCommandArgs;

public static class RevitCommandBatch
{
    public const string Command = "command.batch";
    public const int MinSteps = 1;
    public const int MaxSteps = 50;
    public static IReadOnlyList<string> ExcludedWrites { get; } = new[] { Command };

    public static bool IsStepCommand([NotNullWhen(true)] RevitCommandSpec? spec) => spec is { Writes: true };
    public static string StepField(int index, string? field) => field ?? "";
    public static void CheckOperationIds(string batchOperationId, CommandBatchArgs batch) { }
    public static void CheckFreshSteps(CommandBatchArgs batch, Func<string, bool> alreadyUsed) { }
    public static RevitCommandEnvelope StepEnvelope(RevitCommandEnvelope batch, int index) => batch;
    public static IEnumerable<(string Prefix, RevitCommandArgs Args)> GuardedArgs(RevitCommandEnvelope envelope) { yield return ("", envelope.Args); }
    public static RevitCommandResponse StepFailure(RevitBatchStep step, string code, string? field, string? detail) => RevitCommandResponse.Failure(code, field, detail);
    public static RevitCommandResponse? CheckStepReply(RevitBatchStep step, RevitCommandEnvelope stepEnvelope, string stepRequestSha256, RevitCommandResponse reply) => null;
    public static RevitCommandResponse Success(RevitCommandReceipt receipt, CommandBatchArgs batch, IReadOnlyList<RevitCommandResponse> replies) =>
        RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>());
}

public static partial class RevitCommandCatalogue
{
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> BatchSpecs = new(BuildBatch);
    public static IReadOnlyList<RevitCommandSpec> Batch => BatchSpecs.Value;
    public static IReadOnlyList<RevitCommandSpec> BatchStepCommands => Array.Empty<RevitCommandSpec>();

    static IReadOnlyList<RevitCommandSpec> BuildBatch() => new[]
    {
        new RevitCommandSpec(RevitCommandBatch.Command, true, "Stub batch command, not implemented yet.", """{"type":"object","properties":{"steps":{"type":"array"}},"required":["steps"],"additionalProperties":false}""", BindBatch),
    };

    static RevitCommandArgs BindBatch(JsonElement args) => new CommandBatchArgs(Array.Empty<RevitBatchStep>());
}
