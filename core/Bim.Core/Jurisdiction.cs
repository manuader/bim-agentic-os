using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// P10-06 jurisdiction rule-pack registry: types, codes and the jurisdiction claim sidecar.
// A code pack (a skill of kind code, skills/<id>/skill.json) declares which jurisdiction it covers in a sidecar
// skills/<id>/jurisdiction.json (a new standalone format, version 1; the skill manifest and BIM-IR are unchanged, and
// SkillLoader ignores the sidecar). JurisdictionRegistry selects the pack for a project from a jurisdiction code or from
// country and municipality metadata; JurisdictionValidator wraps the existing room validation in an envelope that names
// the selected packs, their versions and a source_confidence summary.

// What a caller knows about the project's place. Code is a jurisdiction code such as "ar.caba"; Country and Municipality
// are names or codes ("Argentina" or "ar", "Ciudad Autonoma de Buenos Aires" or "caba"), matched after accent stripping,
// whitespace folding and lower-casing. Not part of BIM-IR: the caller fills it from its own project metadata.
public sealed record ProjectJurisdiction(string? Code = null, string? Country = null, string? Municipality = null);

// One sidecar: the pack covers Code, "<country>.<locality>" where country is two lowercase letters and locality one
// [a-z][a-z0-9-]* label. Pack is the skill id, which starts with Code + ".". Names are extra matching aliases for the
// metadata route; the code labels themselves always match. Stub marks an example pack that is not real law.
public sealed record JurisdictionClaim(string Code, string Pack, IReadOnlyList<string> CountryNames, IReadOnlyList<string> MunicipalityNames, bool Stub, string? Note)
{
    public const int CurrentVersion = 1;
    public const int MaxInputChars = 16384;
    public const int MaxNames = 32;
    public const int MaxNameChars = 128;

    // Locality is one skill-id label (at most 63 characters).
    static readonly Regex CodePattern = new("\\A[a-z]{2}\\.[a-z][a-z0-9-]{0,62}\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly string[] Members = { "jurisdiction_version", "code", "pack", "country_names", "municipality_names", "stub", "note" };

    public string Country => Code[..2];
    public string Locality => Code[3..];

    public static bool IsCode(string? value) => value is not null && value.Length <= 66 && CodePattern.IsMatch(value);

    // Strict parse: an object with exactly the known members, no duplicates, jurisdiction_version the integer token 1,
    // code and pack well formed with pack under code, names non-blank, unique after normalisation, stub a boolean.
    // Refusals carry the member path as Subject.
    public static bool TryParse(string text, out JurisdictionClaim? claim, out JurisdictionIssue? issue)
    {
        claim = null;
        issue = null;
        try
        {
            claim = Parse(text);
            return true;
        }
        catch (JurisdictionParseException error)
        {
            issue = new JurisdictionIssue(error.Code, error.Field, error.Message);
            return false;
        }
    }

    static JurisdictionClaim Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxInputChars) throw new JurisdictionParseException(JurisdictionCodes.InputTooLarge, "$", "at most 16384 characters");
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 }); }
        catch (JsonException error) { throw new JurisdictionParseException(JurisdictionCodes.JsonInvalid, "$", error.Message); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JurisdictionParseException(JurisdictionCodes.TypeInvalid, "$", "an object is required");
            var seen = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!Members.Contains(property.Name, StringComparer.Ordinal)) throw new JurisdictionParseException(JurisdictionCodes.MemberUnknown, "$." + property.Name, "unknown member");
                if (!seen.TryAdd(property.Name, property.Value)) throw new JurisdictionParseException(JurisdictionCodes.MemberDuplicate, "$." + property.Name, "member appears twice");
            }
            JsonElement Required(string name) => seen.TryGetValue(name, out var value) ? value : throw new JurisdictionParseException(JurisdictionCodes.MemberMissing, "$." + name, "required member");
            var version = Required("jurisdiction_version");
            if (version.ValueKind != JsonValueKind.Number) throw new JurisdictionParseException(JurisdictionCodes.TypeInvalid, "$.jurisdiction_version", "an integer is required");
            if (version.GetRawText() != "1") throw new JurisdictionParseException(JurisdictionCodes.VersionUnsupported, "$.jurisdiction_version", "only version 1 is supported");
            var code = TextOf(Required("code"), "$.code");
            if (!IsCode(code)) throw new JurisdictionParseException(JurisdictionCodes.CodeInvalid, "$.code", "jurisdiction code such as ar.caba: two lowercase letters, a dot and one [a-z][a-z0-9-]* label");
            var pack = TextOf(Required("pack"), "$.pack");
            if (!SkillParser.IsId(pack)) throw new JurisdictionParseException(JurisdictionCodes.ValueInvalid, "$.pack", "a skill id is required");
            if (!pack.StartsWith(code + ".", StringComparison.Ordinal)) throw new JurisdictionParseException(JurisdictionCodes.PackMismatch, "$.pack", "pack id " + pack + " does not start with " + code + ".");
            var countries = Names(seen, "country_names");
            var municipalities = Names(seen, "municipality_names");
            var stubElement = Required("stub");
            if (stubElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JurisdictionParseException(JurisdictionCodes.TypeInvalid, "$.stub", "a boolean is required");
            string? note = null;
            if (seen.TryGetValue("note", out var noteElement))
            {
                note = TextOf(noteElement, "$.note");
                if (note.Length > 2048 || string.IsNullOrWhiteSpace(note)) throw new JurisdictionParseException(JurisdictionCodes.ValueInvalid, "$.note", "non-blank text of at most 2048 characters");
            }
            return new JurisdictionClaim(code, pack, countries, municipalities, stubElement.ValueKind == JsonValueKind.True, note);
        }
    }

    static string TextOf(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String) throw new JurisdictionParseException(JurisdictionCodes.TypeInvalid, path, "a string is required");
        try { return element.GetString()!; }
        catch (InvalidOperationException error) { throw new JurisdictionParseException(JurisdictionCodes.ValueInvalid, path, "not well-formed text: " + error.Message); }
    }

    static IReadOnlyList<string> Names(Dictionary<string, JsonElement> seen, string member)
    {
        if (!seen.TryGetValue(member, out var array)) return Array.Empty<string>();
        var path = "$." + member;
        if (array.ValueKind != JsonValueKind.Array) throw new JurisdictionParseException(JurisdictionCodes.TypeInvalid, path, "an array of strings is required");
        if (array.GetArrayLength() > MaxNames) throw new JurisdictionParseException(JurisdictionCodes.TooManyItems, path, "at most 32 names");
        var names = new List<string>();
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var at = path + "[" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            var name = TextOf(item, at);
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameChars) throw new JurisdictionParseException(JurisdictionCodes.ValueInvalid, at, "non-blank name of at most 128 characters");
            if (!normalized.Add(JurisdictionRegistry.Normalize(name))) throw new JurisdictionParseException(JurisdictionCodes.EntryDuplicate, at, "name repeats after normalisation");
            names.Add(name);
        }
        return names;
    }
}

// Subject is the member path, file, pack id or jurisdiction code the issue is about.
public sealed record JurisdictionIssue(string Code, string Subject, string Detail);

public static class JurisdictionCodes
{
    // Sidecar parse and load refusals.
    public const string InputTooLarge = "input_too_large";
    public const string JsonInvalid = "json_invalid";
    public const string VersionUnsupported = "version_unsupported";
    public const string MemberUnknown = "member_unknown";
    public const string MemberDuplicate = "member_duplicate";
    public const string MemberMissing = "member_missing";
    public const string TypeInvalid = "type_invalid";
    public const string ValueInvalid = "value_invalid";
    public const string EntryDuplicate = "entry_duplicate";
    public const string TooManyItems = "too_many_items";
    public const string CodeInvalid = "code_invalid";
    // The sidecar pack is not under its code, or differs from the directory it sits in.
    public const string PackMismatch = "pack_mismatch";
    public const string PathUnsafe = "path_unsafe";
    public const string NotRegularFile = "not_regular_file";
    public const string ReadFailed = "read_failed";
    // The skills directory itself was refused by SkillLoader; Detail starts with the SkillCodes code.
    public const string SkillsInvalid = "skills_invalid";
    // Registry issues (a claim is dropped, or a code is claimed twice and becomes ambiguous).
    public const string PackMissing = "pack_missing";
    public const string PackNotCode = "pack_not_code";
    public const string ClaimDuplicate = "claim_duplicate";
    // Two jurisdictions share a country alias and a municipality alias after normalisation (both stay registered).
    public const string AliasOverlap = "alias_overlap";
    // Selection refusals.
    public const string JurisdictionUnspecified = "jurisdiction_unspecified";
    public const string JurisdictionIncomplete = "jurisdiction_incomplete";
    public const string JurisdictionInvalid = "jurisdiction_invalid";
    public const string JurisdictionUnknown = "jurisdiction_unknown";
    public const string JurisdictionAmbiguous = "jurisdiction_ambiguous";
    public const string JurisdictionConflict = "jurisdiction_conflict";
    // The selected claim is a stub example pack and the caller did not opt in.
    public const string JurisdictionStubRefused = "jurisdiction_stub_refused";
    // Validation refusals.
    public const string PackUnresolved = "pack_unresolved";
    public const string InputInvalid = "input_invalid";
}

static class JurisdictionRedStub { public static readonly bool On = true; }

sealed class JurisdictionParseException : Exception
{
    public JurisdictionParseException(string code, string field, string detail) : base(detail)
    {
        Code = code;
        Field = field;
    }

    public string Code { get; }
    public string Field { get; }
}

// Reads skills/<id>/jurisdiction.json files safely: never through a link, regular non-empty files only, bounded read,
// strict UTF-8 with one optional byte order mark.
static class JurisdictionSidecar
{
    public const string FileName = "jurisdiction.json";

    // null when the pack has no sidecar.
    public static (JurisdictionClaim? Claim, JurisdictionIssue? Issue) Read(string skillsRoot, string packId)
    {
        var relative = packId + "/" + FileName;
        try
        {
            var directory = new DirectoryInfo(Path.Combine(skillsRoot, packId));
            var file = new FileInfo(Path.Combine(directory.FullName, FileName));
            var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skillsRoot)) + Path.DirectorySeparatorChar;
            if (IsLink(directory) || IsLink(file)) return (null, new JurisdictionIssue(JurisdictionCodes.PathUnsafe, relative, "symbolic links and junctions are not followed"));
            if (!Inside(file.FullName, boundary)) return (null, new JurisdictionIssue(JurisdictionCodes.PathUnsafe, relative, "path leaves the skills root"));
            if (!JurisdictionRedStub.On && Directory.Exists(file.FullName)) return (null, new JurisdictionIssue(JurisdictionCodes.NotRegularFile, relative, "a directory is not a sidecar"));
            if (!file.Exists) return (null, null);
            if (file.Length == 0) return (null, new JurisdictionIssue(JurisdictionCodes.NotRegularFile, relative, "zero-length entry is not read"));
            if (file.Length > JurisdictionClaim.MaxInputChars * 4L) return (null, new JurisdictionIssue(JurisdictionCodes.InputTooLarge, relative, "sidecar too large"));
            byte[] bytes;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None))
            {
                if (!stream.CanSeek) return (null, new JurisdictionIssue(JurisdictionCodes.NotRegularFile, relative, "not a regular file"));
                var buffer = new byte[JurisdictionClaim.MaxInputChars * 4 + 1];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = stream.Read(buffer.AsSpan(total));
                    if (read == 0) break;
                    total += read;
                }
                bytes = buffer.AsSpan(0, total).ToArray();
            }
            directory.Refresh();
            file.Refresh();
            if (IsLink(directory) || IsLink(file)) return (null, new JurisdictionIssue(JurisdictionCodes.PathUnsafe, relative, "entry became a link while being read"));
            if (bytes.Length > JurisdictionClaim.MaxInputChars * 4) return (null, new JurisdictionIssue(JurisdictionCodes.InputTooLarge, relative, "sidecar too large"));
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset); }
            catch (DecoderFallbackException error) { return (null, new JurisdictionIssue(JurisdictionCodes.JsonInvalid, relative, "invalid UTF-8: " + error.Message)); }
            if (!JurisdictionClaim.TryParse(text, out var claim, out var issue)) return (null, new JurisdictionIssue(issue!.Code, relative, issue.Subject + ": " + issue.Detail));
            if (claim!.Pack != packId) return (null, new JurisdictionIssue(JurisdictionCodes.PackMismatch, relative, "sidecar pack " + claim.Pack + " differs from directory " + packId));
            return (claim, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, new JurisdictionIssue(JurisdictionCodes.ReadFailed, relative, error.Message));
        }
    }

    static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0);

    static bool Inside(string fullPath, string boundary) =>
        Path.GetFullPath(fullPath).StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
