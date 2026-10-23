using System.Text.Json;
namespace Bim.Core;

// Typed explanation of an ExecutionPlan. Returned beside the plan, never inside plan JSON,
// so plan bytes, hashes and retry receipts are unaffected.
public sealed record GeometryChange(string Field, Length? Before, Length? After)
{
    public double? DeltaMetres => Before is Length before && After is Length after ? after.Metres - before.Metres : null;

    static string Metres(double value) => value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " m";

    // A nonzero delta is never shown as zero: below display precision use the round-trip form.
    static string Delta(double value)
    {
        var text = Metres(value);
        return value != 0 && text == Metres(0) ? value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " m" : text;
    }

    public string Describe()
    {
        if (Before is null && After is Length created) return "= " + Metres(created.Metres);
        if (After is null && Before is Length deleted) return "was " + Metres(deleted.Metres);
        var delta = DeltaMetres ?? 0;
        return (delta < 0 ? "-" : "+") + Delta(Math.Abs(delta));
    }
}

// GeometryProblem is the codec code when Validate-accepted geometry has no typed view
// (unknown kind or non-canonical arity); such changes are reported without typed fields.
public sealed record ElementChange(Guid Id, string Kind, string Action, GeometryChange[] Changes, string? GeometryProblem = null);

public sealed record SemanticDiff(Guid ProjectId, int ExpectedRevision, string BeforeHash, string AfterHash, ElementChange[] Elements)
{
    public static SemanticDiff Compute(Project before, Project after, ExecutionPlan plan)
    {
        // Same acceptance rule as ProjectStore.Commit: the plan must be the deterministic diff.
        if (JsonSerializer.Serialize(Compiler.Diff(before, after)) != JsonSerializer.Serialize(plan)) throw new InvalidOperationException("plan_mismatch");
        var prior = before.Elements.ToDictionary(element => element.Id);
        var elements = new List<ElementChange>();
        foreach (var operation in plan.Operations)
        {
            var element = operation.Element;
            var problem = operation.Action == "update" && prior.TryGetValue(element.Id, out var previous)
                ? GeometryCodec.Problem(element) ?? GeometryCodec.Problem(previous)
                : GeometryCodec.Problem(element);
            if (problem is not null)
            {
                elements.Add(new ElementChange(element.Id, element.Kind, operation.Action, Array.Empty<GeometryChange>(), problem));
                continue;
            }
            var changes = operation.Action switch
            {
                "create" => Fields(element).Select(field => new GeometryChange(field.Name, null, field.Value)).ToArray(),
                "delete" => Fields(element).Select(field => new GeometryChange(field.Name, field.Value, null)).ToArray(),
                "update" => Changed(prior[element.Id], element),
                _ => throw new InvalidOperationException("plan_mismatch"),
            };
            elements.Add(new ElementChange(element.Id, element.Kind, operation.Action, changes));
        }
        return new SemanticDiff(plan.ProjectId, plan.ExpectedRevision, plan.BeforeHash, plan.AfterHash, elements.ToArray());
    }

    static GeometryField[] Fields(Element element) => GeometryCodec.Decode(element).Fields();

    // Bit-exact comparison, matching Compiler.Diff, so a reported change is never rounded away.
    static GeometryChange[] Changed(Element prior, Element next)
    {
        var old = Fields(prior);
        var current = Fields(next);
        if (prior.Kind != next.Kind)
        {
            return old.Select(field => new GeometryChange(field.Name, field.Value, null))
                .Concat(current.Select(field => new GeometryChange(field.Name, null, field.Value))).ToArray();
        }
        return old.Zip(current)
            .Where(pair => BitConverter.DoubleToInt64Bits(pair.First.Value.Metres) != BitConverter.DoubleToInt64Bits(pair.Second.Value.Metres))
            .Select(pair => new GeometryChange(pair.First.Name, pair.First.Value, pair.Second.Value)).ToArray();
    }
}
