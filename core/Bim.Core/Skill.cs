using System.Globalization;
namespace Bim.Core;

// Domain skill manifest v1: a portable, versioned pack of design knowledge (style, typology, code or QA) that the
// product applies. A new input format, separate from BIM-IR. Each skill lives at skills/<id>/skill.json.
// Contract: docs/contracts/skill.schema.json. SkillParser reads one manifest strictly, SkillLoader reads a directory of
// them safely, SkillResolver composes a requested set into effective settings traceable to their skill.
public enum SkillKind { Style, Typology, Code, Qa }

public enum SkillSeverity { Error, Warning, Info }

public enum SkillParameterType { Number, Integer, Boolean, String }

public enum SkillValueKind { Number, Text, Boolean }

// The closed set of rule checks. Min: property >= threshold. Max: property <= threshold. EqualTo: property == threshold.
// In: property is one of Values. RatioMin: property / Over >= threshold.
public enum SkillCheckOp { Min, Max, EqualTo, In, RatioMin }

// How an effective setting was chosen: the only contributor, the highest priority, the caller's precedence order among
// equal priorities, or equal priorities that agree on the value.
public enum SkillDecision { Only, Priority, Precedence, Agreement }

// A JSON scalar: a finite number, a text or a boolean. -0 is stored as 0 so equal quantities compare equal.
public sealed record SkillValue
{
    SkillValue(SkillValueKind kind, double number, string? text, bool boolean)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Boolean = boolean;
    }

    public SkillValueKind Kind { get; }
    public double Number { get; }
    public string? Text { get; }
    public bool Boolean { get; }

    public static SkillValue Of(double number)
    {
        if (!double.IsFinite(number)) throw new ArgumentOutOfRangeException(nameof(number), "finite number required");
        return new SkillValue(SkillValueKind.Number, number == 0 ? 0.0 : number, null, false);
    }

    public static SkillValue Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new SkillValue(SkillValueKind.Text, 0, text, false);
    }

    public static SkillValue Of(bool boolean) => new(SkillValueKind.Boolean, 0, null, boolean);

    // Invariant JSON-like text: 0.3, "bedroom", true.
    public override string ToString() => Kind switch
    {
        SkillValueKind.Number => Number.ToString("R", CultureInfo.InvariantCulture),
        SkillValueKind.Text => "\"" + Text + "\"",
        _ => Boolean ? "true" : "false",
    };
}

// Semantic version MAJOR.MINOR.PATCH without pre-release or build metadata.
public readonly record struct SkillVersion(int Major, int Minor, int Patch) : IComparable<SkillVersion>
{
    public int CompareTo(SkillVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        return Patch.CompareTo(other.Patch);
    }

    public static bool operator <(SkillVersion left, SkillVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SkillVersion left, SkillVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SkillVersion left, SkillVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SkillVersion left, SkillVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + "." + Patch.ToString(CultureInfo.InvariantCulture);
}

public enum SkillComparatorOp { Equal, Greater, GreaterOrEqual, Less, LessOrEqual }

public readonly record struct SkillComparator(SkillComparatorOp Op, SkillVersion Version)
{
    public bool Matches(SkillVersion version) => Op switch
    {
        SkillComparatorOp.Equal => version == Version,
        SkillComparatorOp.Greater => version > Version,
        SkillComparatorOp.GreaterOrEqual => version >= Version,
        SkillComparatorOp.Less => version < Version,
        _ => version <= Version,
    };
}

// A semver range: "*" or space-separated comparators that must all hold. Each comparator is a version optionally prefixed
// by =, >, >=, <, <=, ^ (same left-most non-zero component) or ~ (same major and minor). Text is the range as written.
public sealed record SkillVersionRange(string Text, IReadOnlyList<SkillComparator> Comparators)
{
    public bool Satisfies(SkillVersion version)
    {
        foreach (var comparator in Comparators) if (!comparator.Matches(version)) return false;
        return true;
    }

    public override string ToString() => Text;
}

public sealed record SkillRequirement(string Id, SkillVersionRange Range);

// Min and Max are only allowed for number and integer parameters; the default lies within them.
public sealed record SkillParameter(string Name, SkillParameterType Type, SkillValue Default, double? Min = null, double? Max = null, string? Description = null);

public sealed record SkillDefault(string Key, SkillValue Value);

// Exactly one of ElementKind (an element kind token such as "wall") or RoomPattern (a case-insensitive room name glob
// where * matches any run of characters) is set.
public sealed record SkillTarget(string? ElementKind, string? RoomPattern)
{
    public bool MatchesRoom(string roomName)
    {
        ArgumentNullException.ThrowIfNull(roomName);
        return RoomPattern is not null && Glob(RoomPattern, roomName);
    }

    static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

// Threshold is a literal (Value) or a reference to one of the skill's parameters (Parameter); In uses Values instead.
// Over is the denominator property of RatioMin.
public sealed record SkillCheck(SkillCheckOp Op, string Property, SkillValue? Value = null, string? Parameter = null, IReadOnlyList<SkillValue>? Values = null, string? Over = null);

public sealed record SkillRule(string Id, SkillSeverity Severity, SkillTarget Target, SkillCheck Check, string Message, string? Citation = null);

public sealed record SkillManifest(int ManifestVersion, string Id, SkillVersion Version, SkillKind Kind, string Description, int Priority,
    IReadOnlyList<SkillRequirement> Requires, IReadOnlyList<string> ConflictsWith, IReadOnlyList<SkillParameter> Parameters,
    IReadOnlyList<SkillDefault> Defaults, IReadOnlyList<SkillRule> Rules)
{
    public const int CurrentVersion = 1;
}

// A set of skills with unique ids (two manifests with one id are refused as id_duplicate), kept in ordinal id order.
public sealed class SkillCatalog
{
    readonly Dictionary<string, SkillManifest> byId = new(StringComparer.Ordinal);

    public SkillCatalog(IEnumerable<SkillManifest> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        foreach (var skill in skills)
        {
            ArgumentNullException.ThrowIfNull(skill);
            if (!byId.TryAdd(skill.Id, skill)) throw new SkillException(SkillCodes.IdDuplicate, "id", "skill " + skill.Id + " appears more than once in the catalog");
        }
        Skills = byId.Values.OrderBy(skill => skill.Id, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<SkillManifest> Skills { get; }

    public SkillManifest? Find(string id) => id is not null && byId.TryGetValue(id, out var skill) ? skill : null;
}

// A requested skill; Range defaults to "*".
public sealed record SkillRequest(string Id, string Range = "*");

// Where a value came from: the skill, the origin ("default", "parameter" or "rule") and the default key, parameter name
// or rule id within that skill.
public sealed record SkillTrace(string SkillId, string Origin, string Key)
{
    public const string DefaultOrigin = "default";
    public const string ParameterOrigin = "parameter";
    public const string RuleOrigin = "rule";

    public override string ToString() => SkillId + ":" + Origin + ":" + Key;
}

// One effective dotted setting. Overridden lists the contributions that lost or agreed, in resolution order.
public sealed record SkillSetting(string Key, SkillValue Value, SkillTrace Source, SkillDecision Decision, IReadOnlyList<SkillTrace> Overridden);

public sealed record SkillEffectiveParameter(string SkillId, string Name, SkillValue Value, SkillTrace Source);

// Threshold is the resolved literal or parameter value (null for In); ThresholdSource names the parameter when used.
public sealed record SkillEffectiveRule(string SkillId, SkillRule Rule, SkillValue? Threshold, SkillTrace Source, SkillTrace? ThresholdSource)
{
    public string QualifiedId => SkillId + "/" + Rule.Id;
}

// Subject is the skill id, setting key or precedence entry the issue is about.
public sealed record SkillIssue(string Code, string Subject, string Detail);

// Either Issues is empty and the rest is populated, or Issues lists every refusal and the rest is empty.
// Skills are in dependency order (requirements first, ties by ordinal id); settings by ordinal key.
public sealed record SkillResolution(IReadOnlyList<SkillManifest> Skills, IReadOnlyList<SkillSetting> Settings, IReadOnlyList<SkillEffectiveParameter> Parameters,
    IReadOnlyList<SkillEffectiveRule> Rules, IReadOnlyList<SkillIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0;

    public SkillSetting? Setting(string key)
    {
        foreach (var setting in Settings) if (string.Equals(setting.Key, key, StringComparison.Ordinal)) return setting;
        return null;
    }
}

public static class SkillCodes
{
    // Parser refusals (SkillException).
    public const string InputTooLarge = "input_too_large";
    public const string JsonInvalid = "json_invalid";
    public const string VersionUnsupported = "version_unsupported";
    public const string MemberUnknown = "member_unknown";
    public const string MemberDuplicate = "member_duplicate";
    public const string MemberMissing = "member_missing";
    public const string TypeInvalid = "type_invalid";
    public const string ValueInvalid = "value_invalid";
    public const string IdInvalid = "id_invalid";
    public const string SemverInvalid = "semver_invalid";
    public const string RangeInvalid = "range_invalid";
    public const string KeyInvalid = "key_invalid";
    public const string EntryDuplicate = "entry_duplicate";
    public const string TooManyItems = "too_many_items";
    public const string SelfReference = "self_reference";
    public const string BoundsInvalid = "bounds_invalid";
    public const string DefaultOutOfRange = "default_out_of_range";
    public const string ParameterUnknown = "parameter_unknown";
    public const string ParameterTypeMismatch = "parameter_type_mismatch";
    // Loader refusals (SkillException with File).
    public const string RootMissing = "root_missing";
    public const string PathUnsafe = "path_unsafe";
    public const string ManifestMissing = "manifest_missing";
    public const string IdMismatch = "id_mismatch";
    // An I/O or permission failure while enumerating the root or reading a manifest.
    public const string ReadFailed = "read_failed";
    // A zero-length manifest (empty file, or a FIFO, device or socket) or a handle that is not a seekable regular file.
    public const string NotRegularFile = "not_regular_file";
    // Catalog refusal: two manifests with one id.
    public const string IdDuplicate = "id_duplicate";
    // Resolver issues (SkillIssue).
    public const string RequestInvalid = "request_invalid";
    public const string SkillMissing = "skill_missing";
    public const string DependencyMissing = "dependency_missing";
    public const string VersionMismatch = "version_mismatch";
    public const string DependencyCycle = "dependency_cycle";
    public const string SkillConflict = "skill_conflict";
    public const string DefaultConflict = "default_conflict";
    public const string PrecedenceUnknown = "precedence_unknown";
    public const string PrecedenceDuplicate = "precedence_duplicate";
}

// A typed parser or loader refusal. Message equals Code; Field is the JSON path of the offending value or null for the
// document; File is the manifest or directory path (relative to the skills root, '/' separated) when loading.
public sealed class SkillException : FormatException
{
    public SkillException(string code, string? field = null, string? detail = null, string? file = null) : base(code)
    {
        Code = code;
        Field = field;
        Detail = detail;
        File = file;
    }

    public string Code { get; }
    public string? Field { get; }
    public string? Detail { get; }
    public string? File { get; }
}
