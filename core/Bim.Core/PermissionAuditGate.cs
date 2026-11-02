namespace Bim.Core;

// Evaluates the permission policy for one envelope with the injected actor and appends the decision, allowed or
// denied, to the audit log before returning it. If the append fails the exception propagates and no decision is
// returned, so an unaudited command can never be treated as allowed. An actor id that cannot be recorded (see
// PermissionPolicy.IsActor) is denied actor_invalid and logged under InvalidActor.
public sealed class PermissionAuditGate
{
    public const string InvalidActor = "(invalid-actor)";

    readonly PermissionPolicy policy;
    readonly AuditLog log;
    readonly Func<string> actor;

    public PermissionAuditGate(PermissionPolicy policy, AuditLog log, Func<string> actor)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(actor);
        this.policy = policy;
        this.log = log;
        this.actor = actor;
    }

    public PermissionDecision Authorize(RevitCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var who = actor();
        var decision = policy.Evaluate(who, envelope);
        log.Append(PermissionPolicy.IsActor(who) ? who : InvalidActor, envelope.OperationId, envelope.Command, envelope.Document.Path, decision);
        return decision;
    }
}
