namespace Bim.Core;

// P4-04 default documentation set derived from a BIM-IR project (contract: docs/contracts/module-contracts.md, "P4-04").
public static class DocDefaultsCodes
{
    public const string InvalidIr = "invalid_ir";
    public const string LevelInvalid = "doc_defaults_level_invalid";
    public const string NoLevel = "doc_defaults_no_level";
}

public static class DocDefaults
{
    public static DocumentationSet From(Project project) => throw new NotImplementedException();
}
