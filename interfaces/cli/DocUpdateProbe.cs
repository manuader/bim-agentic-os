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
// before anything reaches the pipe.

// Where the dry runs go: the enabled-roots policy (loaded only once a probe is about to run) and the one-line pipe
// client. Real is what the CLI uses; tests pass a fake executor here.
internal sealed record DocUpdateRevit(Func<RevitCommandPolicy> Policy, Func<string, string, string?> Pipe)
{
    public static DocUpdateRevit Real { get; } = new(() => new RevitCommandPolicySource(AppContext.BaseDirectory).Current(), RevitCommandService.PipeTransport);
}

internal static class DocUpdateProbe
{
    // The process pipe rule of revit-command, applied before any file or pipe I/O: null when accepted, else the refusal
    // code and field.
    public static (string Code, string? Field)? PipeProblem(string pipeName)
    {
        if (RevitCommandService.RejectPipe(pipeName) is not { } rejected) return null;
        var refusal = RevitCommandResponse.Parse(rejected.Line);
        return (refusal.Code, refusal.Field);
    }

    // DocUpdateDependents.Plan with every request sent through RevitCommandService.Run on pipeName. The policy is loaded
    // here, after the accepted UniqueIds are checked (a RevitCommandException from it propagates typed); DocUpdate.Plan's
    // refusals propagate unchanged.
    public static DocUpdateDependentsResult Plan(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized,
        string pipeName, IReadOnlyCollection<string> accepted, DocUpdateRevit revit)
    {
        // The library's accepted_invalid, checked before the policy is read.
        if (accepted.Any(uid => !RevitCommandProtocol.IsUniqueId(uid))) throw new ArgumentException(DocUpdateDependentsCodes.AcceptedInvalid);
        var policy = revit.Policy();
        return DocUpdateDependents.Plan(before, after, bindings, realized, new RevitServiceTransport(pipeName, policy, revit.Pipe), accepted);
    }
}
