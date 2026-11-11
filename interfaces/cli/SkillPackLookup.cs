#nullable enable
using Bim.Core;

namespace Bim.Cli;

// The repository-only skill pack lookup shared by generate-prototypes (P7-09) and plan-layouts --pack (P7-13): the
// repository is RevitCommandPolicy.RepositoryRoot of the CLI binary (the first ancestor holding global.json); the pack
// manifest <repository>/skills/<id>/skill.json is read through BoundedInput (8 MiB cap, input_* codes) and parsed by
// SkillParser (its own 65536-character cap); a manifest naming another id is refused. Checks in that order, after a guard:
// an id that is not SkillParser.IsId (callers refuse those first) is Invalid "id" before any file system access, so the
// path never leaves skills/.
// P7-12: with packageFirst (generate-prototypes only) the release package root is tried before the repository walk: the
// parent directory of the CLI binary's directory (<package>/cli) when it holds a skills/ directory; then the manifest is
// <package>/skills/<id>/skill.json, read and checked exactly as above. Without skills/ there the repository walk follows
// (RepositoryNotFound when it finds none). Load(id) without packageFirst (plan-layouts --pack) is unchanged.
internal enum SkillPackProblem { RepositoryNotFound, Unavailable, Invalid }

// Detail: null for RepositoryNotFound, the BoundedInput input_* code for Unavailable, the SkillCodes code (or "id" for a
// manifest naming another pack) for Invalid.
internal sealed class SkillPackException(SkillPackProblem problem, string? detail) : Exception(problem.ToString())
{
    public SkillPackProblem Problem { get; } = problem;
    public string? Detail { get; } = detail;
}

internal static class SkillPackLookup
{
    public static SkillManifest Load(string packId) => Load(packId, packageFirst: false);

    public static SkillManifest Load(string packId, bool packageFirst)
    {
        if (!SkillParser.IsId(packId)) throw new SkillPackException(SkillPackProblem.Invalid, "id");
        var repository = (packageFirst ? PackageRoot(AppContext.BaseDirectory) : null)
            ?? RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory) ?? throw new SkillPackException(SkillPackProblem.RepositoryNotFound, null);
        string text;
        try { text = BoundedInput.ReadText(Path.Combine(repository, "skills", packId, SkillLoader.ManifestFileName)); }
        catch (InputRejectedException error) { throw new SkillPackException(SkillPackProblem.Unavailable, error.Message); }
        SkillManifest pack;
        try { pack = SkillParser.Parse(text); }
        catch (SkillException error) { throw new SkillPackException(SkillPackProblem.Invalid, error.Code); }
        if (pack.Id != packId) throw new SkillPackException(SkillPackProblem.Invalid, "id");
        return pack;
    }

    // The release package root of a CLI binary directory (<package>/cli): its parent when that holds skills/, else null.
    public static string? PackageRoot(string binaryDirectory)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(binaryDirectory)));
        return parent is not null && Directory.Exists(Path.Combine(parent, "skills")) ? parent : null;
    }
}
