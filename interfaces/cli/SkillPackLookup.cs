#nullable enable
using Bim.Core;

namespace Bim.Cli;

// The skill pack lookup shared by every production caller: generate-prototypes (P7-09/P7-12), generate-typology (P7-14)
// and plan-layouts --pack (P7-13/P7-15), all through Load(id, packageFirst: true). Load(id) without packageFirst (the
// repository walk alone) is a legacy overload kept for tests; no production caller uses it.
// Checks in order, after a guard: an id that is not SkillParser.IsId (callers refuse those first) is Invalid "id" before
// any file system access, on either path, so the path never leaves skills/.
// With packageFirst the release package root is tried before the repository walk: the parent directory of the CLI
// binary's directory when that directory is named "cli" (ordinal) and the parent holds both markers scripts/package.ps1
// writes (the files VERSION and manifest.json) and a skills/ directory (a stray skills/ with one or no marker is not a
// package); then the manifest is <package>/skills/<id>/skill.json, with no fallback to the repository once the package
// root is chosen. Otherwise (no package root, or packageFirst false) the repository is RevitCommandPolicy.RepositoryRoot of
// the CLI binary (the first ancestor holding global.json), RepositoryNotFound when there is none, and the manifest is
// <repository>/skills/<id>/skill.json. The manifest is read through BoundedInput (8 MiB cap, input_* codes) and parsed by
// SkillParser (its own 65536-character cap); a manifest naming another id is refused.
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

    // The release package root of a CLI binary directory <package>/cli: the parent when the directory is named "cli" and
    // the parent holds VERSION, manifest.json and skills/ (a stray skills/ beside any other folder is not a package), else null.
    public static string? PackageRoot(string binaryDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binaryDirectory));
        if (!string.Equals(Path.GetFileName(directory), "cli", StringComparison.Ordinal)) return null;
        var parent = Path.GetDirectoryName(directory);
        return parent is not null && File.Exists(Path.Combine(parent, "VERSION")) && File.Exists(Path.Combine(parent, "manifest.json"))
            && Directory.Exists(Path.Combine(parent, "skills")) ? parent : null;
    }
}
