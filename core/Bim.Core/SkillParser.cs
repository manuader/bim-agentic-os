using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// Strict reader for skill manifest v1: bounded size, depth and counts, no comments or trailing commas, unknown and
// duplicate members refused, JSON types, enumerations, patterns, semver, ranges and internal references checked.
// Every refusal is a SkillException carrying a code and the JSON path of the offending value.
public static class SkillParser
{
    public const int MaxInputChars = 65536;
    public const int MaxDepth = 16;
    public const int MaxStringChars = 2048;
    public const int MaxRangeChars = 128;
    public const int MaxRequires = 64;
    public const int MaxConflicts = 64;
    public const int MaxParameters = 128;
    public const int MaxDefaults = 512;
    public const int MaxRules = 256;
    public const int MaxInValues = 64;

    // Pattern bodies shared with docs/contracts/skill.schema.json, which writes them as "^" + body + "$".
    // Skill id: reverse-DNS-like, at least two dot-separated labels of [a-z0-9-] starting with a letter, at most 128.
    public const string IdPatternBody = "(?=.{3,128}$)[a-z][a-z0-9-]{0,62}(\\.[a-z][a-z0-9-]{0,62})+";
    public const string SemverPatternBody = "(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})";
    // Version range: "*" or single-space-separated comparators, each an optional =, >=, <=, >, <, ^ or ~ and a semver.
    public const string RangePatternBody = "(\\*|(=|>=|<=|>|<|\\^|~)?" + SemverPatternBody + "( (=|>=|<=|>|<|\\^|~)?" + SemverPatternBody + ")*)";
    // Dotted setting path such as wall.exterior.thickness_m: at least two segments of [a-z0-9_] starting with a letter.
    public const string KeyPatternBody = "(?=.{3,128}$)[a-z][a-z0-9_]{0,63}(\\.[a-z][a-z0-9_]{0,63})+";
    // Parameter names, property names and element kinds.
    public const string NamePatternBody = "[a-z][a-z0-9_]{0,63}";
    public const string RuleIdPatternBody = "[a-z0-9][a-z0-9_-]{0,63}";
    // Room name glob: letters, digits, space, _ - and the * wildcard.
    public const string RoomPatternBody = "[A-Za-z0-9 _*-]{1,128}";

    public static readonly IReadOnlyList<string> RootMembers = new[] { "manifest_version", "id", "version", "kind", "description", "priority", "requires", "conflicts_with", "parameters", "defaults", "rules" };
    public static readonly IReadOnlyList<string> RootRequired = new[] { "manifest_version", "id", "version", "kind", "description", "priority" };
    public static readonly IReadOnlyList<string> RequirementMembers = new[] { "id", "range" };
    public static readonly IReadOnlyList<string> ParameterMembers = new[] { "name", "type", "default", "min", "max", "description" };
    public static readonly IReadOnlyList<string> RuleMembers = new[] { "id", "severity", "target", "check", "message", "citation" };
    public static readonly IReadOnlyList<string> TargetMembers = new[] { "element", "room" };
    public static readonly IReadOnlyList<string> ThresholdCheckMembers = new[] { "op", "property", "value", "parameter" };
    public static readonly IReadOnlyList<string> InCheckMembers = new[] { "op", "property", "values" };
    public static readonly IReadOnlyList<string> RatioCheckMembers = new[] { "op", "property", "over", "value", "parameter" };
    public static readonly IReadOnlyList<string> Kinds = new[] { "style", "typology", "code", "qa" };
    public static readonly IReadOnlyList<string> Severities = new[] { "error", "warning", "info" };
    public static readonly IReadOnlyList<string> ParameterTypes = new[] { "number", "integer", "boolean", "string" };
    public static readonly IReadOnlyList<string> CheckOps = new[] { "min", "max", "equals", "in", "ratio_min" };

    static readonly Regex IdPattern = Anchored(IdPatternBody);
    static readonly Regex SemverPattern = Anchored(SemverPatternBody);
    static readonly Regex KeyPattern = Anchored(KeyPatternBody);
    static readonly Regex NamePattern = Anchored(NamePatternBody);
    static readonly Regex RuleIdPattern = Anchored(RuleIdPatternBody);
    static readonly Regex RoomPattern = Anchored(RoomPatternBody);

    static Regex Anchored(string body) => new("\\A(?:" + body.Replace("$", "\\z", StringComparison.Ordinal) + ")\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsId(string? value) => value is not null && IdPattern.IsMatch(value);
    public static bool IsKey(string? value) => value is not null && KeyPattern.IsMatch(value);
    public static bool IsName(string? value) => value is not null && NamePattern.IsMatch(value);

    public static string KindName(SkillKind kind) => Kinds[(int)kind];
    public static string SeverityName(SkillSeverity severity) => Severities[(int)severity];
    public static string ParameterTypeName(SkillParameterType type) => ParameterTypes[(int)type];
    public static string OpName(SkillCheckOp op) => CheckOps[(int)op];

    // Non-blank, at most MaxStringChars UTF-16 code units, no lone surrogate.
    public static bool IsText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxStringChars) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
            else if (char.IsSurrogate(value[index])) return false;
        }
        return true;
    }

    public static bool TryParseVersion(string? text, out SkillVersion version)
    {
        version = default;
        if (text is null) return false;
        var match = SemverPattern.Match(text);
        if (!match.Success) return false;
        version = new SkillVersion(int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture));
        return true;
    }

    // "*" or single-space-separated comparators; see SkillVersionRange.
    public static bool TryParseRange(string? text, out SkillVersionRange range)
    {
        range = new SkillVersionRange("*", Array.Empty<SkillComparator>());
        if (text is null || text.Length == 0 || text.Length > MaxRangeChars) return false;
        if (text == "*") return true;
        var comparators = new List<SkillComparator>();
        foreach (var token in text.Split(' '))
        {
            var (prefix, rest) = token.StartsWith(">=", StringComparison.Ordinal) || token.StartsWith("<=", StringComparison.Ordinal) ? (token[..2], token[2..])
                : token.Length > 0 && "=<>^~".Contains(token[0], StringComparison.Ordinal) ? (token[..1], token[1..])
                : ("", token);
            if (!TryParseVersion(rest, out var version)) return false;
            switch (prefix)
            {
                case "": case "=": comparators.Add(new(SkillComparatorOp.Equal, version)); break;
                case ">": comparators.Add(new(SkillComparatorOp.Greater, version)); break;
                case ">=": comparators.Add(new(SkillComparatorOp.GreaterOrEqual, version)); break;
                case "<": comparators.Add(new(SkillComparatorOp.Less, version)); break;
                case "<=": comparators.Add(new(SkillComparatorOp.LessOrEqual, version)); break;
                case "~":
                    comparators.Add(new(SkillComparatorOp.GreaterOrEqual, version));
                    if (version.Minor == int.MaxValue) return false;
                    comparators.Add(new(SkillComparatorOp.Less, new SkillVersion(version.Major, version.Minor + 1, 0)));
                    break;
                default:
                    comparators.Add(new(SkillComparatorOp.GreaterOrEqual, version));
                    var upper = version.Major > 0 ? new SkillVersion(version.Major + 1, 0, 0)
                        : version.Minor > 0 ? new SkillVersion(0, version.Minor + 1, 0)
                        : new SkillVersion(0, 0, version.Patch + 1);
                    comparators.Add(new(SkillComparatorOp.Less, upper));
                    break;
            }
        }
        range = new SkillVersionRange(text, comparators.ToArray());
        return true;
    }

    public static SkillManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxInputChars) throw new SkillException(SkillCodes.InputTooLarge, null, "at most 65536 characters");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException error) { throw new SkillException(SkillCodes.JsonInvalid, null, error.Message); }
        catch (ArgumentException error) { throw new SkillException(SkillCodes.JsonInvalid, null, error.Message); }
        catch (InvalidOperationException error) { throw new SkillException(SkillCodes.JsonInvalid, null, error.Message); }
        using (document)
        {
            try { return ParseRoot(document.RootElement); }
            catch (InvalidOperationException error) { throw new SkillException(SkillCodes.JsonInvalid, null, error.Message); }
        }
    }

    static SkillManifest ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new SkillException(SkillCodes.TypeInvalid, null, "object required");
        // The version is checked before members so a future version is reported as such, not as unknown members.
        if (!root.TryGetProperty("manifest_version", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var manifestVersion) || manifestVersion != SkillManifest.CurrentVersion)
            throw new SkillException(SkillCodes.VersionUnsupported, "manifest_version", "1 required");
        var members = Members(root, "", RootMembers);
        foreach (var name in RootRequired) _ = Required(members, "", name);
        var id = Id(members["id"], "id");
        var versionText = Text(members["version"], "version");
        if (!TryParseVersion(versionText, out var version)) throw new SkillException(SkillCodes.SemverInvalid, "version", "MAJOR.MINOR.PATCH without leading zeros");
        var kind = (SkillKind)Enumerated(members["kind"], "kind", Kinds);
        var description = Text(members["description"], "description");
        var priority = Integer(members["priority"], "priority");

        var requires = Items(members, "requires", MaxRequires, Requirement);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < requires.Length; index++)
        {
            var path = Indexed("requires", index) + ".id";
            if (requires[index].Id == id) throw new SkillException(SkillCodes.SelfReference, path, "a skill cannot require itself");
            if (!seen.Add(requires[index].Id)) throw new SkillException(SkillCodes.EntryDuplicate, path, "required once");
        }
        var conflicts = Items(members, "conflicts_with", MaxConflicts, Id);
        seen.Clear();
        for (var index = 0; index < conflicts.Length; index++)
        {
            var path = Indexed("conflicts_with", index);
            if (conflicts[index] == id) throw new SkillException(SkillCodes.SelfReference, path, "a skill cannot conflict with itself");
            if (!seen.Add(conflicts[index])) throw new SkillException(SkillCodes.EntryDuplicate, path, "listed once");
            if (requires.Any(requirement => requirement.Id == conflicts[index])) throw new SkillException(SkillCodes.SelfReference, path, "a skill cannot both require and conflict with " + conflicts[index]);
        }

        var parameters = Items(members, "parameters", MaxParameters, Parameter);
        seen.Clear();
        for (var index = 0; index < parameters.Length; index++)
            if (!seen.Add(parameters[index].Name)) throw new SkillException(SkillCodes.EntryDuplicate, Indexed("parameters", index) + ".name", "parameter names are unique");

        var defaults = new List<SkillDefault>();
        if (members.TryGetValue("defaults", out var defaultsElement))
        {
            // Members() already refuses duplicate keys; any key is allowed here and checked against the key pattern.
            var entries = Members(defaultsElement, "defaults", null);
            if (entries.Count > MaxDefaults) throw new SkillException(SkillCodes.TooManyItems, "defaults", "at most 512 entries");
            foreach (var (key, valueElement) in entries)
            {
                var path = "defaults." + key;
                if (!IsKey(key)) throw new SkillException(SkillCodes.KeyInvalid, path, "dotted path of at least two [a-z][a-z0-9_]* segments");
                defaults.Add(new SkillDefault(key, Scalar(valueElement, path)));
            }
        }

        var rules = Items(members, "rules", MaxRules, Rule);
        seen.Clear();
        for (var index = 0; index < rules.Length; index++)
        {
            var path = Indexed("rules", index);
            if (!seen.Add(rules[index].Id)) throw new SkillException(SkillCodes.EntryDuplicate, path + ".id", "rule ids are unique within a skill");
            var check = rules[index].Check;
            if (check.Parameter is null) continue;
            var parameter = parameters.FirstOrDefault(candidate => candidate.Name == check.Parameter);
            if (parameter is null) throw new SkillException(SkillCodes.ParameterUnknown, path + ".check.parameter", "no parameter named " + check.Parameter);
            if (check.Op != SkillCheckOp.EqualTo && parameter.Type is not (SkillParameterType.Number or SkillParameterType.Integer))
                throw new SkillException(SkillCodes.ParameterTypeMismatch, path + ".check.parameter", OpName(check.Op) + " needs a number or integer parameter");
        }

        return new SkillManifest(manifestVersion, id, version, kind, description, priority, requires, conflicts, parameters, defaults.ToArray(), rules);
    }

    static SkillRequirement Requirement(JsonElement element, string path)
    {
        var members = Members(element, path, RequirementMembers);
        var id = Id(Required(members, path, "id"), Join(path, "id"));
        var rangePath = Join(path, "range");
        var text = Text(Required(members, path, "range"), rangePath);
        if (!TryParseRange(text, out var range)) throw new SkillException(SkillCodes.RangeInvalid, rangePath, "* or space-separated [=|>|>=|<|<=|^|~]MAJOR.MINOR.PATCH");
        return new SkillRequirement(id, range);
    }

    static SkillParameter Parameter(JsonElement element, string path)
    {
        var members = Members(element, path, ParameterMembers);
        var name = Name(Required(members, path, "name"), Join(path, "name"));
        var type = (SkillParameterType)Enumerated(Required(members, path, "type"), Join(path, "type"), ParameterTypes);
        var defaultPath = Join(path, "default");
        var defaultElement = Required(members, path, "default");
        var value = type switch
        {
            SkillParameterType.Number => SkillValue.Of(Number(defaultElement, defaultPath)),
            SkillParameterType.Integer => SkillValue.Of(Integer(defaultElement, defaultPath)),
            SkillParameterType.Boolean => defaultElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? SkillValue.Of(defaultElement.GetBoolean()) : throw new SkillException(SkillCodes.TypeInvalid, defaultPath, "boolean required"),
            _ => SkillValue.Of(Text(defaultElement, defaultPath)),
        };
        double? min = null, max = null;
        foreach (var bound in new[] { "min", "max" })
        {
            if (!members.TryGetValue(bound, out var boundElement)) continue;
            var boundPath = Join(path, bound);
            if (type is not (SkillParameterType.Number or SkillParameterType.Integer)) throw new SkillException(SkillCodes.BoundsInvalid, boundPath, "only number and integer parameters take a range");
            double number = type == SkillParameterType.Integer ? Integer(boundElement, boundPath) : Number(boundElement, boundPath);
            if (bound == "min") min = number; else max = number;
        }
        if (min is not null && max is not null && min > max) throw new SkillException(SkillCodes.BoundsInvalid, Join(path, "max"), "min must not exceed max");
        if ((min is not null && value.Number < min) || (max is not null && value.Number > max))
            throw new SkillException(SkillCodes.DefaultOutOfRange, defaultPath, "default must lie within [min, max]");
        string? description = members.TryGetValue("description", out var descriptionElement) ? Text(descriptionElement, Join(path, "description")) : null;
        return new SkillParameter(name, type, value, min, max, description);
    }

    static SkillRule Rule(JsonElement element, string path)
    {
        var members = Members(element, path, RuleMembers);
        var idPath = Join(path, "id");
        var id = Text(Required(members, path, "id"), idPath);
        if (!RuleIdPattern.IsMatch(id)) throw new SkillException(SkillCodes.IdInvalid, idPath, "1-64 of [a-z0-9_-], starting with a letter or digit");
        var severity = (SkillSeverity)Enumerated(Required(members, path, "severity"), Join(path, "severity"), Severities);
        var target = Target(Required(members, path, "target"), Join(path, "target"));
        var check = Check(Required(members, path, "check"), Join(path, "check"));
        var message = Text(Required(members, path, "message"), Join(path, "message"));
        string? citation = members.TryGetValue("citation", out var citationElement) ? Text(citationElement, Join(path, "citation")) : null;
        return new SkillRule(id, severity, target, check, message, citation);
    }

    static SkillTarget Target(JsonElement element, string path)
    {
        var members = Members(element, path, TargetMembers);
        if (members.Count != 1) throw new SkillException(SkillCodes.ValueInvalid, path, "exactly one of element or room");
        if (members.TryGetValue("element", out var kindElement)) return new SkillTarget(Name(kindElement, Join(path, "element")), null);
        var roomPath = Join(path, "room");
        var pattern = Text(members["room"], roomPath);
        if (!RoomPattern.IsMatch(pattern)) throw new SkillException(SkillCodes.ValueInvalid, roomPath, "1-128 of letters, digits, space, _, - and *");
        return new SkillTarget(null, pattern);
    }

    static SkillCheck Check(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new SkillException(SkillCodes.TypeInvalid, path, "object required");
        // The op selects the allowed members, so it is read first.
        var opPath = Join(path, "op");
        if (!element.TryGetProperty("op", out var opElement)) throw new SkillException(SkillCodes.MemberMissing, opPath, "required");
        var op = (SkillCheckOp)Enumerated(opElement, opPath, CheckOps);
        var allowed = op switch { SkillCheckOp.In => InCheckMembers, SkillCheckOp.RatioMin => RatioCheckMembers, _ => ThresholdCheckMembers };
        var members = Members(element, path, allowed);
        var property = Name(Required(members, path, "property"), Join(path, "property"));
        if (op == SkillCheckOp.In)
        {
            var valuesPath = Join(path, "values");
            var valuesElement = Required(members, path, "values");
            if (valuesElement.ValueKind != JsonValueKind.Array) throw new SkillException(SkillCodes.TypeInvalid, valuesPath, "array required");
            var values = new List<SkillValue>();
            foreach (var item in valuesElement.EnumerateArray())
            {
                var itemPath = Indexed(valuesPath, values.Count);
                if (values.Count == MaxInValues) throw new SkillException(SkillCodes.TooManyItems, valuesPath, "at most 64 values");
                var value = Scalar(item, itemPath);
                if (values.Contains(value)) throw new SkillException(SkillCodes.EntryDuplicate, itemPath, "values are unique");
                values.Add(value);
            }
            if (values.Count == 0) throw new SkillException(SkillCodes.ValueInvalid, valuesPath, "at least one value");
            return new SkillCheck(op, property, Values: values.ToArray());
        }
        string? over = null;
        if (op == SkillCheckOp.RatioMin)
        {
            var overPath = Join(path, "over");
            over = Name(Required(members, path, "over"), overPath);
            if (over == property) throw new SkillException(SkillCodes.ValueInvalid, overPath, "the denominator differs from the property");
        }
        var hasValue = members.TryGetValue("value", out var valueElement);
        var hasParameter = members.TryGetValue("parameter", out var parameterElement);
        if (hasValue == hasParameter) throw new SkillException(hasValue ? SkillCodes.ValueInvalid : SkillCodes.MemberMissing, Join(path, "value"), "exactly one of value or parameter");
        if (hasParameter) return new SkillCheck(op, property, Parameter: Name(parameterElement, Join(path, "parameter")), Over: over);
        var valuePath = Join(path, "value");
        var threshold = op == SkillCheckOp.EqualTo ? Scalar(valueElement, valuePath) : SkillValue.Of(Number(valueElement, valuePath));
        return new SkillCheck(op, property, Value: threshold, Over: over);
    }

    static T[] Items<T>(Dictionary<string, JsonElement> members, string name, int max, Func<JsonElement, string, T> read)
    {
        if (!members.TryGetValue(name, out var element)) return Array.Empty<T>();
        if (element.ValueKind != JsonValueKind.Array) throw new SkillException(SkillCodes.TypeInvalid, name, "array required");
        if (element.GetArrayLength() > max) throw new SkillException(SkillCodes.TooManyItems, name, "at most " + max.ToString(CultureInfo.InvariantCulture) + " items");
        var items = new List<T>();
        foreach (var item in element.EnumerateArray()) items.Add(read(item, Indexed(name, items.Count)));
        return items.ToArray();
    }

    // allowed null accepts any member name (used for the defaults map); duplicates are always refused.
    static Dictionary<string, JsonElement> Members(JsonElement element, string path, IReadOnlyList<string>? allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new SkillException(SkillCodes.TypeInvalid, path.Length == 0 ? null : path, "object required");
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var at = Join(path, property.Name);
            if (members.ContainsKey(property.Name)) throw new SkillException(SkillCodes.MemberDuplicate, at, "duplicate member");
            if (allowed is not null && !allowed.Contains(property.Name, StringComparer.Ordinal)) throw new SkillException(SkillCodes.MemberUnknown, at, "unexpected member");
            members[property.Name] = property.Value;
        }
        return members;
    }

    static JsonElement Required(Dictionary<string, JsonElement> members, string path, string name) =>
        members.TryGetValue(name, out var value) ? value : throw new SkillException(SkillCodes.MemberMissing, Join(path, name), "required");

    static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    static string Indexed(string path, int index) => path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    static int Enumerated(JsonElement element, string path, IReadOnlyList<string> names)
    {
        var text = Text(element, path);
        for (var index = 0; index < names.Count; index++) if (names[index] == text) return index;
        throw new SkillException(SkillCodes.ValueInvalid, path, "one of " + string.Join(", ", names));
    }

    static string Id(JsonElement element, string path)
    {
        var text = Text(element, path);
        if (!IsId(text)) throw new SkillException(SkillCodes.IdInvalid, path, "reverse-DNS-like id such as bim.qa.room-basics");
        return text;
    }

    static string Name(JsonElement element, string path)
    {
        var text = Text(element, path);
        if (!IsName(text)) throw new SkillException(SkillCodes.ValueInvalid, path, "1-64 of [a-z0-9_], starting with a letter");
        return text;
    }

    static string Text(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String) throw new SkillException(SkillCodes.TypeInvalid, path, "string required");
        var text = element.GetString()!;
        if (!IsText(text)) throw new SkillException(SkillCodes.ValueInvalid, path, "non-blank string of at most 2048 UTF-16 code units");
        return text;
    }

    static SkillValue Scalar(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.Number => SkillValue.Of(Number(element, path)),
        JsonValueKind.String => SkillValue.Of(Text(element, path)),
        JsonValueKind.True => SkillValue.Of(true),
        JsonValueKind.False => SkillValue.Of(false),
        _ => throw new SkillException(SkillCodes.TypeInvalid, path, "number, string or boolean required"),
    };

    static double Number(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new SkillException(SkillCodes.TypeInvalid, path, "finite number required");
        return number == 0 ? 0.0 : number;
    }

    static int Integer(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number)) throw new SkillException(SkillCodes.TypeInvalid, path, "32-bit integer required");
        return number;
    }
}
