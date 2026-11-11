namespace Bim.Generative;

// P5-14 typology pipeline options (opt-in; the default is the P5-12 behaviour). ComposedRules plans the typology
// candidates with the P5-13 composed TypologyRules of the skill resolution (TypologyRules.Generate: the error-severity
// minimum area/side rules of the composed packs, with per-rule pack/rule-id traces) instead of the fixed
// PrototypeTemplates.Rules of TypologyBrief, and records the rule set in the result and its canonical JSON. Off, the
// result and its JSON are byte-identical to TypologyPipeline.Run without options.
public sealed record TypologyPipelineOptions(bool ComposedRules = false)
{
    public static TypologyPipelineOptions Default { get; } = new();
}

public static class TypologyPipelineCodes
{
    // Composed-rules mode only: options.Pack (the code pack of the facade gate) is not, by id and version, one of the
    // skills of the resolution whose rules planned the candidates (subject pack).
    public const string PackMismatch = "typology_pipeline_pack_mismatch";
}
