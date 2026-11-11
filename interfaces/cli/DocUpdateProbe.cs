#nullable enable
using Bim.Core;

namespace Bim.Cli;

// P4-10 `doc-update ... --probe-dependents process-pipe-name [--accept uid]...`: the transport half of the doc-update
// verb (interfaces/cli/DocUpdateCommand.cs keeps the dry-plan half free of any transport). The plan is
// DocUpdateDependents.Plan (P4-09, core/Bim.Core/DocUpdateDependents.cs) over the Revit process pipe: it plans once, sends
// only element.delete dry runs (dry_run true; the add-in rolls them back and stores no receipt) to collect and confirm
// the dependents, and replans; it never runs the plan (no update, run or resume of DocUpdate or DocUpdateDependents, no
// PlanRunner). Every dry run goes through RevitCommandService.Run exactly like `revit-command` (explicit process pipe,
// envelope validation, the copies-only and link guards of the enabled roots read from this binary's repository, wire
// size cap, a receipt answering the request), so a document outside an enabled test root is refused document_not_enabled
// before anything reaches the pipe. With --execute (P4-12) the probed plan is run by interfaces/cli/DocUpdateExecute.cs
// over the same Channel.

// Where the dry runs go: the enabled-roots policy (read lazily, once, just before the first dry run) and the one-line
// pipe client. Real is what the CLI uses; tests pass a fake executor here.
internal sealed record DocUpdateRevit(Func<RevitCommandPolicy> Policy, Func<string, string, string?> Pipe)
{
    public static DocUpdateRevit Real { get; } = new(() => new RevitCommandPolicySource(AppContext.BaseDirectory).Current(), RevitCommandService.PipeTransport);
}

internal static class DocUpdateProbe
{
    // The process pipe rule of revit-command, applied before any file or pipe I/O: null when accepted, else the refusal
    // code, field and detail.
    public static (string Code, string? Field, string? Detail)? PipeProblem(string pipeName)
    {
        if (RevitCommandService.RejectPipe(pipeName) is not { } rejected) return null;
        var refusal = RevitCommandResponse.Parse(rejected.Line);
        return (refusal.Code, refusal.Field, refusal.Detail);
    }

    // The channel every request of one doc-update run goes through (the dry runs of the probe and, with --execute, the
    // writes of the run, P4-12): RevitCommandService.Run on pipeName, the policy read once, at the first request.
    public static IRevitCommandTransport Channel(string pipeName, DocUpdateRevit revit) => new LazyPolicy(pipeName, revit);

    // DocUpdateDependents.Plan with every request sent through channel. The accepted UniqueIds are checked first;
    // DocUpdate.Plan's refusals of the first plan propagate unchanged (before the policy is read). The policy is read by
    // the first Send only (LazyPolicy), so a plan without deletes never reads it here.
    public static DocUpdateDependentsResult Plan(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized,
        IReadOnlyCollection<string> accepted, IRevitCommandTransport channel)
    {
        // The library's accepted_invalid, checked before the policy is read.
        if (accepted.Any(uid => !RevitCommandProtocol.IsUniqueId(uid))) throw new ArgumentException(DocUpdateDependentsCodes.AcceptedInvalid);
        return DocUpdateDependents.Plan(before, after, bindings, realized, channel, accepted);
    }

    // Reads the policy once, at the first request, then sends every request through RevitServiceTransport. A
    // RevitCommandException from the policy becomes that request's typed failure reply (code, field, detail), so
    // DocUpdateDependents reports it with the key and operation id of the dry run it stopped; nothing reaches the pipe.
    sealed class LazyPolicy(string pipeName, DocUpdateRevit revit) : IRevitCommandTransport
    {
        RevitServiceTransport? inner;
        RevitCommandException? refused;

        public string? Send(string envelopeJson)
        {
            if (inner is null && refused is null)
            {
                try { inner = new RevitServiceTransport(pipeName, revit.Policy(), revit.Pipe); }
                catch (RevitCommandException error) { refused = error; }
            }
            return refused is { } policyError ? RevitCommandResponse.Failure(policyError.Code, policyError.Field, policyError.Detail).ToJson() : inner!.Send(envelopeJson);
        }
    }
}
