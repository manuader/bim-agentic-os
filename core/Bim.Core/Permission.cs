namespace Bim.Core;

// P10-03 permission policy. Ordered levels: a grant of a higher level includes the lower ones.
public enum PermissionLevel
{
    Read = 1,
    WriteCopies = 2,
    WriteRealProject = 3,
}

public static class PermissionCodes
{
    // Allow reasons.
    public const string ReadAllowed = "read_allowed";
    public const string WriteCopiesAllowed = "write_copies_allowed";
    public const string RealProjectEnabled = "write_real_project_enabled";
    // Deny reasons.
    public const string ActorInvalid = "actor_invalid";
    public const string CommandUnknown = "command_unknown";
    public const string DocumentPathInvalid = "document_path_invalid";
    public const string ActorUnknown = "actor_unknown";
    public const string RealProjectNotEnabled = "real_project_not_enabled";
    public const string LevelInsufficient = "permission_level_insufficient";
    // Construction.
    public const string PolicyInvalid = "permission_policy_invalid";
}

public sealed record PermissionGrant(string Actor, PermissionLevel Level);

// Required is the level the request needs; null when it cannot be classified (unknown command or invalid path).
public sealed record PermissionDecision(bool Allowed, string Reason, PermissionLevel? Required);

public sealed class PermissionPolicyException : ArgumentException
{
    public PermissionPolicyException(string code, string? detail = null) : base(detail is null ? code : code + ": " + detail)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }
    public string? Detail { get; }
}

// Pure, deterministic decision over who may run which catalogue command on which document.
// Classification reuses the existing contracts: the catalogue entry of the command name says whether it writes (the
// envelope's Spec is not trusted), and RevitCopiesGuard.IsUnder against an explicit list of test-copy roots says
// whether a write targets a test copy. The copies roots are passed explicitly (DefaultCopiesRoots: only the
// repository .bim/native-runs) and are deliberately NOT taken from RevitCommandPolicy, whose EnabledRoots also hold
// the owner file lines meant for enabled real projects. Every other write is a real-project write: denied unless
// that exact document path is in the explicit enablement list (the owner rule "writes only to test copies until the
// owner enables real projects") and the actor holds WriteRealProject. The copies guard, link guard and service keep
// their own checks; this policy is an additional gate, never a replacement.
public sealed class PermissionPolicy
{
    public const int MaxActorChars = 128;

    readonly Dictionary<string, PermissionLevel> levels;

    PermissionPolicy(Dictionary<string, PermissionLevel> levels, IReadOnlyList<string> enablements, IReadOnlyList<string> copiesRoots)
    {
        this.levels = levels;
        RealProjectEnablements = enablements;
        CopiesRoots = copiesRoots;
    }

    public IReadOnlyList<string> CopiesRoots { get; }
    public IReadOnlyList<string> RealProjectEnablements { get; }

    // The only default test-copy root: <repository>/.bim/native-runs, where owner sessions place fixture copies.
    public static IReadOnlyList<string> DefaultCopiesRoots(string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        return new[] { Path.Combine(repositoryRoot, ".bim", "native-runs") };
    }

    // Fails closed on any malformed grant, enablement or copies root: duplicate or invalid actor (including the gate's
    // InvalidActor placeholder), undefined level, or a path that is not absolute and normalized or is too broad (a
    // drive root, "/" or a bare UNC share).
    public static PermissionPolicy Create(IEnumerable<PermissionGrant> grants, IEnumerable<string> realProjectEnablements, IEnumerable<string> copiesRoots)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(realProjectEnablements);
        ArgumentNullException.ThrowIfNull(copiesRoots);
        var levels = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (grant is null || !IsActor(grant.Actor)) throw new PermissionPolicyException(PermissionCodes.PolicyInvalid, "invalid actor id");
            if (!Enum.IsDefined(grant.Level)) throw new PermissionPolicyException(PermissionCodes.PolicyInvalid, "undefined level for " + grant.Actor);
            if (!levels.TryAdd(grant.Actor, grant.Level)) throw new PermissionPolicyException(PermissionCodes.PolicyInvalid, "duplicate grant for " + grant.Actor);
        }
        var enablements = new List<string>();
        foreach (var entry in realProjectEnablements)
        {
            if (!RevitCopiesGuard.IsAbsoluteNormalized(entry)) throw new PermissionPolicyException(PermissionCodes.PolicyInvalid, "enablement must be an absolute path without . or .. segments");
            if (RevitCopiesGuard.IsTooBroad(entry)) throw new PermissionPolicyException(PermissionCodes.PolicyInvalid, "enablement too broad");
            enablements.Add(entry);
        }
        var roots = new List<string>();
        foreach (var root in copiesRoots)
        {
            if (!RevitCopiesGuard.IsAbsoluteNormalized(root)) throw new PermissionPolicyException(PermissionCodes.PolicyInvalid, "copies root must be an absolute path without . or .. segments");
            if (RevitCopiesGuard.IsTooBroad(root)) throw new PermissionPolicyException(PermissionCodes.PolicyInvalid, "copies root too broad");
            roots.Add(root);
        }
        return new PermissionPolicy(levels, enablements.AsReadOnly(), roots.AsReadOnly());
    }

    // 1-128 characters, no control characters, no unpaired surrogates, not all whitespace.
    public static bool IsActor(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > MaxActorChars) return false;
        return IsPlainText(actor);
    }

    internal static bool IsPlainText(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsControl(c)) return false;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }

    public PermissionLevel? LevelOf(string actor) => actor is not null && levels.TryGetValue(actor, out var level) ? level : null;

    // Null when the command is not in the catalogue or the path is not an absolute normalized document path.
    public static PermissionLevel? RequiredLevel(string command, string documentPath, IReadOnlyList<string> copiesRoots)
    {
        ArgumentNullException.ThrowIfNull(copiesRoots);
        if (command is null || RevitCommandCatalogue.Find(command) is not { } spec) return null;
        if (!RevitCopiesGuard.IsAbsoluteNormalized(documentPath)) return null;
        if (!spec.Writes) return PermissionLevel.Read;
        return copiesRoots.Any(root => RevitCopiesGuard.IsUnder(documentPath, root)) ? PermissionLevel.WriteCopies : PermissionLevel.WriteRealProject;
    }

    public PermissionLevel? Classify(RevitCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return RequiredLevel(envelope.Command, envelope.Document.Path, CopiesRoots);
    }

    // Exact document match (same segments, compared like the copies guard); an enablement never acts as a root.
    public bool IsRealProjectEnabled(string documentPath) =>
        RevitCopiesGuard.IsAbsoluteNormalized(documentPath)
        && RealProjectEnablements.Any(entry => RevitCopiesGuard.IsUnder(documentPath, entry) && RevitCopiesGuard.IsUnder(entry, documentPath));

    public PermissionDecision Evaluate(string actor, RevitCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Evaluate(actor, envelope.Command, envelope.Document.Path);
    }

    // Order: actor id, command, path, grant, then by required level. For a real-project write the enablement is
    // checked before the level, so a document that is not enabled is always reported as real_project_not_enabled.
    public PermissionDecision Evaluate(string actor, string command, string documentPath)
    {
        if (!IsActor(actor)) return Deny(PermissionCodes.ActorInvalid, null);
        if (command is null || RevitCommandCatalogue.Find(command) is null) return Deny(PermissionCodes.CommandUnknown, null);
        if (RequiredLevel(command, documentPath, CopiesRoots) is not { } required) return Deny(PermissionCodes.DocumentPathInvalid, null);
        if (LevelOf(actor) is not { } granted) return Deny(PermissionCodes.ActorUnknown, required);
        switch (required)
        {
            case PermissionLevel.Read:
                return new PermissionDecision(true, PermissionCodes.ReadAllowed, required);
            case PermissionLevel.WriteCopies:
                return granted >= PermissionLevel.WriteCopies
                    ? new PermissionDecision(true, PermissionCodes.WriteCopiesAllowed, required)
                    : Deny(PermissionCodes.LevelInsufficient, required);
            default:
                if (!IsRealProjectEnabled(documentPath)) return Deny(PermissionCodes.RealProjectNotEnabled, required);
                return granted >= PermissionLevel.WriteRealProject
                    ? new PermissionDecision(true, PermissionCodes.RealProjectEnabled, required)
                    : Deny(PermissionCodes.LevelInsufficient, required);
        }
    }

    static PermissionDecision Deny(string reason, PermissionLevel? required) => new(false, reason, required);
}
