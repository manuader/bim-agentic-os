namespace Bim.Generative;

// Stub (TDD red): the canonical form is not implemented yet.
public static class PrototypePipelineSerializer
{
    public static string Serialize(PrototypePipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return "{}";
    }
}
