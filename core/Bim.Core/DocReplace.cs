using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Bim.Core;

// P4-05 stale documentation replacement. A documentation regeneration (DocRegeneration.Affected, or Full) says which
// item keys are regenerated; their earlier realization in the document is stale. Given what is realized now (source ->
// Revit UniqueId, the sources DocRegeneration uses: "doc:<key>" for a realized view, schedule, sheet, tag, dimension
// or placement, "element:<BIM-IR id>" for a realized model element), Plan builds one ordered catalogue RevitCommandPlan
// that first deletes the stale instance of every regenerated key (one element.delete per key, with the dependents a
// dry run reported confirmed), children (placements, dimensions, tags) before parents (sheets, schedules, views), and
// then re-creates every regenerated key with a step, parents before children. An input on a regenerated item is the
// created[0] of its re-creation step; any other input is filled from the realized map at plan time, so no stale or
// placeholder UniqueId is ever sent. A regenerated key whose new result is unsupported is deleted and not re-created.
// The plan runs through PlanRunner (no level preconditions); a failed run resumes with PlanRunner.Resume, sending only
// pending steps (operation ids are deterministic, so a resent step that already ran replays). Realized gives the
// realized map after a completed run. Vendor-free, BCL only; no BIM-IR or persisted format change.

// One regenerated key: its stale realization (null when it had none), the element.delete step that removes it (null
// without a stale instance) and the step that re-creates it (null when the new result is unsupported).
public sealed record DocReplacement(string Key, string ItemKind, string? StaleUniqueId, string? DeleteOperation, string? CreateOperation);

// Commands: deletes then creates, in run order. Replacements: one per regenerated key, by key (ordinal). Unsupported:
// the regeneration's unsupported entries (their keys are deleted, never re-created).
public sealed record DocReplacementPlan(RevitCommandPlan Commands, IReadOnlyList<DocReplacement> Replacements, IReadOnlyList<DocUnsupported> Unsupported);

public static class DocReplaceCodes
{
    // ArgumentException messages.
    public const string RegenerationInvalid = "regeneration_invalid";
    public const string RealizedInvalid = "realized_invalid";
    // A stale UniqueId that is also realized for another source: deleting it would remove something that is kept.
    public const string StaleShared = "stale_shared";
    public const string DependentsUnknownKey = "dependents_unknown_key";
    public const string DependentsWithoutStale = "dependents_without_stale";
    public const string DependentsInvalid = "dependents_invalid";
    // A confirmed dependent that is realized for a kept source (an item not regenerated, or a model element).
    public const string DependentKept = "dependent_kept";
    // A confirmed dependent that is the stale instance of another regenerated key (deleted at its own step).
    public const string DependentPlanned = "dependent_planned";
    public const string InputFieldUnsupported = "input_field_unsupported";
    public const string InputUnresolved = "input_unresolved";
    public const string ReportIncomplete = "report_incomplete";
    public const string CreatedMissing = "created_missing";
}

public static class DocReplace
{
    public const int Version = 1;
    public const string DeleteCommand = "element.delete";

    // Red stub (P4-05): the behaviour lands with the implementation commit.
    public static DocReplacementPlan Plan(DocRegenerationPlan regeneration, IReadOnlyDictionary<string, string> realized, IReadOnlyDictionary<string, IReadOnlyList<string>>? dependents = null) =>
        throw new NotImplementedException("P4-05");

    public static RevitPlanRunReport Run(DocReplacementPlan plan, IRevitCommandTransport transport) => throw new NotImplementedException("P4-05");

    public static RevitPlanRunReport Resume(DocReplacementPlan plan, RevitPlanRunReport report, IRevitCommandTransport transport) => throw new NotImplementedException("P4-05");

    public static IReadOnlyDictionary<string, string> Realized(DocReplacementPlan plan, IReadOnlyDictionary<string, string> realized, RevitPlanRunReport report) =>
        throw new NotImplementedException("P4-05");
}
