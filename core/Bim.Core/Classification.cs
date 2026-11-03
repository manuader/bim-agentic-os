using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// P10-05 offline classification mapping (bSDD-style, no network). A versioned dictionary file
// (format bim.classification-dictionary/1, see core/Bim.Core/Classification/README.md) maps BIM-IR element kinds to
// classification codes plus the fields of an IFC IfcClassificationReference and its IfcClassification. BIM-IR elements
// carry no type, so rules are keyed by kind only. Every element without a rule is reported as unmapped with a reason.
public static class ClassificationCodes
{
    public const string InputTooLarge = "classification_input_too_large";
    public const string InvalidJson = "classification_invalid_json";
    public const string DuplicateKey = "classification_duplicate_key";
    public const string UnknownMember = "classification_unknown_member";
    public const string MissingMember = "classification_missing_member";
    public const string ValueInvalid = "classification_value_invalid";
    public const string FormatUnsupported = "classification_format_unsupported";
    public const string CodeInvalid = "classification_code_invalid";
    public const string DuplicateKind = "classification_duplicate_kind";
    public const string TooManyItems = "classification_too_many_items";
    public const string DuplicateDictionary = "classification_duplicate_dictionary";
    public const string UnknownDictionary = "classification_unknown_dictionary";
    public const string DuplicateElement = "classification_duplicate_element";
    public const string ReadFailed = "classification_read_failed";
    // Unmapped reasons.
    public const string NoRule = "no_rule";
    public const string Excluded = "excluded";
}

public sealed class ClassificationException(string code, string? path, string message) : Exception(code + (path is null ? "" : " at " + path) + ": " + message)
{
    public string Code { get; } = code;
    public string? Path { get; } = path;
}

public sealed record ClassificationRule(string Kind, string Code, string Title, string Location);
public sealed record ClassificationExclusion(string Kind, string Note);

public sealed record ClassificationDictionary(
    string Id, string Version, string Name, string Publisher, string Edition, string? EditionDate, string SourceUrl,
    string Retrieved, string Licence, string CodePattern, IReadOnlyList<ClassificationRule> Rules, IReadOnlyList<ClassificationExclusion> Unmapped)
{
    public ClassificationRule? Rule(string kind) => Rules.FirstOrDefault(rule => rule.Kind == kind);
}

// IFC4 IfcClassification attributes (Source = publisher, Edition, EditionDate, Name, Location). EditionDate is an IFC4
// IfcDate (YYYY-MM-DD) or null when the publisher gives only a month; the raw edition text then carries it.
public sealed record IfcClassificationSource(string Name, string Source, string Edition, string? EditionDate, string Location);
// IFC4 IfcClassificationReference attributes (Location, Identification, Name, ReferencedSource).
public sealed record IfcClassificationReferenceFields(string Identification, string Name, string Location, IfcClassificationSource ReferencedSource);

public sealed record ClassificationMapped(Guid ElementId, string Kind, string Code, string Title, IfcClassificationReferenceFields Ifc);
public sealed record ClassificationUnmapped(Guid ElementId, string Kind, string Reason, string Note);
public sealed record ClassificationResult(string DictionaryId, string DictionaryVersion, IReadOnlyList<ClassificationMapped> Mapped, IReadOnlyList<ClassificationUnmapped> Unmapped);

public static class ClassificationDictionaryParser
{
    public const string Format = "bim.classification-dictionary/1";
    public const int MaxInputBytes = 65536;
    public const int MaxRules = 256;
    // Labels and identifiers become IFC IfcLabel/IfcIdentifier values, which IFC limits to 255 characters.
    public const int MaxText = 255;
    public const int MaxUrl = 512;
    public const int MaxNote = 512;
    public const int MaxLicence = 2048;
    public const string ResourcePrefix = "Bim.Core.Classification.";
    const RegexOptions Safe = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(100);
    static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9.-]{0,63}$", Safe, RegexBudget);
    static readonly Regex VersionPattern = new("^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})$", Safe, RegexBudget);
    static readonly Regex KindPattern = new("^[a-z][a-z_]{0,31}$", Safe, RegexBudget);
    // Every code, whatever the dictionary, is short printable ASCII; the dictionary's own code_pattern narrows it.
    static readonly Regex BaseCodePattern = new("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", Safe, RegexBudget);
    static readonly string[] TopMembers = { "format", "id", "version", "name", "publisher", "edition", "edition_date", "source_url", "retrieved", "licence", "code_pattern", "rules", "unmapped" };
    static readonly string[] RuleMembers = { "kind", "code", "title", "location" };
    static readonly string[] ExclusionMembers = { "kind", "note" };

    // Dictionaries shipped inside Bim.Core as embedded resources (core/Bim.Core/Classification/*.json), by id.
    public static IReadOnlyList<string> BuiltInIds => typeof(ClassificationDictionaryParser).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
        .Select(name => name[ResourcePrefix.Length..^".json".Length]).Order(StringComparer.Ordinal).ToArray();

    // Same bounded, strict path as LoadFile, over the embedded resource; the file's id must equal the requested id.
    public static ClassificationDictionary LoadBuiltIn(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!IdPattern.IsMatch(id)) throw new ClassificationException(ClassificationCodes.UnknownDictionary, null, "no built-in dictionary " + id);
        byte[] bytes;
        using (var stream = typeof(ClassificationDictionaryParser).Assembly.GetManifestResourceStream(ResourcePrefix + id + ".json"))
        {
            if (stream is null) throw new ClassificationException(ClassificationCodes.UnknownDictionary, null, "no built-in dictionary " + id);
            try { bytes = ReadBounded(stream); }
            catch (IOException error) { throw new ClassificationException(ClassificationCodes.ReadFailed, null, error.Message); }
        }
        var dictionary = Decode(bytes);
        if (dictionary.Id != id) throw new ClassificationException(ClassificationCodes.ValueInvalid, "$.id", "built-in resource " + id + " declares id " + dictionary.Id);
        return dictionary;
    }

    // Reads at most MaxInputBytes + 1 bytes, strips one UTF-8 byte order mark and requires strict UTF-8.
    public static ClassificationDictionary LoadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytes = ReadBounded(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { throw new ClassificationException(ClassificationCodes.ReadFailed, null, error.Message); }
        return Decode(bytes);
    }

    static byte[] ReadBounded(Stream stream)
    {
        var buffer = new byte[MaxInputBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxInputBytes) throw new ClassificationException(ClassificationCodes.InputTooLarge, null, "at most " + MaxInputBytes + " bytes");
        return buffer[..total];
    }

    static ClassificationDictionary Decode(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) span = span[3..];
        string text;
        try { text = new UTF8Encoding(false, true).GetString(span); }
        catch (DecoderFallbackException) { throw new ClassificationException(ClassificationCodes.InvalidJson, null, "not strict UTF-8"); }
        return Parse(text);
    }

    public static ClassificationDictionary Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxInputBytes) throw new ClassificationException(ClassificationCodes.InputTooLarge, null, "at most " + MaxInputBytes + " bytes");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }); }
        catch (JsonException error) { throw new ClassificationException(ClassificationCodes.InvalidJson, null, error.Message); }
        using (document)
        {
            // A lone surrogate escape (\ud800) parses but cannot become a .NET string: GetString and Name throw.
            try { return Read(document.RootElement); }
            catch (InvalidOperationException error) { throw new ClassificationException(ClassificationCodes.InvalidJson, null, error.Message); }
        }
    }

    static ClassificationDictionary Read(JsonElement root)
    {
        RefuseDuplicates(root, "$");
        var top = Object(root, "$", TopMembers);
        var format = Text(top, "format", "$", MaxText);
        if (format != Format) throw new ClassificationException(ClassificationCodes.FormatUnsupported, "$.format", "expected " + Format);
        var id = Matching(top, "id", "$", IdPattern);
        var version = Matching(top, "version", "$", VersionPattern);
        var name = Text(top, "name", "$", MaxText);
        var publisher = Text(top, "publisher", "$", MaxText);
        var edition = Text(top, "edition", "$", MaxText);
        string? editionDate = null;
        if (top.TryGetValue("edition_date", out var editionDateValue) && editionDateValue.ValueKind != JsonValueKind.Null) editionDate = Date(top, "edition_date", "$");
        var sourceUrl = Url(top, "source_url", "$");
        var retrieved = Date(top, "retrieved", "$");
        var licence = Text(top, "licence", "$", MaxLicence);
        var codePatternText = Text(top, "code_pattern", "$", MaxText);
        if (!codePatternText.StartsWith('^') || !codePatternText.EndsWith('$')) throw new ClassificationException(ClassificationCodes.ValueInvalid, "$.code_pattern", "must be anchored with ^ and $");
        Regex codePattern;
        try { codePattern = new Regex(codePatternText, Safe, RegexBudget); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { throw new ClassificationException(ClassificationCodes.ValueInvalid, "$.code_pattern", "unsupported pattern (no backreferences or lookarounds): " + error.Message); }

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var rules = new List<ClassificationRule>();
        var ruleArray = RequiredArray(top, "rules", "$");
        for (var index = 0; index < ruleArray.Length; index++)
        {
            var at = "$.rules[" + index + "]";
            var rule = Object(ruleArray[index], at, RuleMembers);
            var kind = Matching(rule, "kind", at, KindPattern);
            var code = Text(rule, "code", at, MaxText);
            if (!Matches(BaseCodePattern, code) || !Matches(codePattern, code)) throw new ClassificationException(ClassificationCodes.CodeInvalid, at + ".code", "code " + code + " does not match " + codePatternText);
            var title = Text(rule, "title", at, MaxText);
            var location = Url(rule, "location", at);
            if (!kinds.Add(kind)) throw new ClassificationException(ClassificationCodes.DuplicateKind, at + ".kind", "kind " + kind + " already has a rule");
            rules.Add(new(kind, code, title, location));
        }
        var exclusions = new List<ClassificationExclusion>();
        if (top.TryGetValue("unmapped", out var unmappedElement))
        {
            var unmappedArray = ArrayOf(unmappedElement, "$.unmapped");
            for (var index = 0; index < unmappedArray.Length; index++)
            {
                var at = "$.unmapped[" + index + "]";
                var exclusion = Object(unmappedArray[index], at, ExclusionMembers);
                var kind = Matching(exclusion, "kind", at, KindPattern);
                var note = Text(exclusion, "note", at, MaxNote);
                if (!kinds.Add(kind)) throw new ClassificationException(ClassificationCodes.DuplicateKind, at + ".kind", "kind " + kind + " already has a rule or exclusion");
                exclusions.Add(new(kind, note));
            }
        }
        if (rules.Count + exclusions.Count > MaxRules) throw new ClassificationException(ClassificationCodes.TooManyItems, "$.rules", "at most " + MaxRules + " rules and exclusions");
        return new(id, version, name, publisher, edition, editionDate, sourceUrl, retrieved, licence, codePatternText,
            rules.OrderBy(item => item.Kind, StringComparer.Ordinal).ToArray(), exclusions.OrderBy(item => item.Kind, StringComparer.Ordinal).ToArray());
    }

    static bool Matches(Regex pattern, string value)
    {
        try { return pattern.IsMatch(value); }
        catch (RegexMatchTimeoutException) { throw new ClassificationException(ClassificationCodes.ValueInvalid, "$.code_pattern", "pattern exceeded its time budget"); }
    }

    static void RefuseDuplicates(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ClassificationException(ClassificationCodes.DuplicateKey, path + "." + property.Name, "duplicate member");
                RefuseDuplicates(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) RefuseDuplicates(item, path + "[" + index++ + "]");
        }
    }

    static Dictionary<string, JsonElement> Object(JsonElement element, string path, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ClassificationException(ClassificationCodes.ValueInvalid, path, "expected an object");
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new ClassificationException(ClassificationCodes.UnknownMember, path + "." + property.Name, "unknown member");
            members.Add(property.Name, property.Value);
        }
        return members;
    }

    static JsonElement[] RequiredArray(Dictionary<string, JsonElement> members, string name, string path)
    {
        if (!members.TryGetValue(name, out var value)) throw new ClassificationException(ClassificationCodes.MissingMember, path + "." + name, "required");
        return ArrayOf(value, path + "." + name);
    }

    static JsonElement[] ArrayOf(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new ClassificationException(ClassificationCodes.ValueInvalid, path, "expected an array");
        var items = value.EnumerateArray().ToArray();
        if (items.Length > MaxRules) throw new ClassificationException(ClassificationCodes.TooManyItems, path, "at most " + MaxRules + " items");
        return items;
    }

    static string Text(Dictionary<string, JsonElement> members, string name, string path, int max)
    {
        if (!members.TryGetValue(name, out var value)) throw new ClassificationException(ClassificationCodes.MissingMember, path + "." + name, "required");
        if (value.ValueKind != JsonValueKind.String) throw new ClassificationException(ClassificationCodes.ValueInvalid, path + "." + name, "expected a string");
        var text = value.GetString()!;
        if (text.Trim().Length == 0 || text.Length > max || text != text.Trim() || text.Any(char.IsControl)) throw new ClassificationException(ClassificationCodes.ValueInvalid, path + "." + name, "1 to " + max + " characters, no surrounding space or control characters");
        return text;
    }

    static string Matching(Dictionary<string, JsonElement> members, string name, string path, Regex pattern)
    {
        var text = Text(members, name, path, MaxText);
        if (!pattern.IsMatch(text)) throw new ClassificationException(ClassificationCodes.ValueInvalid, path + "." + name, "does not match " + pattern);
        return text;
    }

    // IFC4 IfcDate: a real calendar date written YYYY-MM-DD.
    static string Date(Dictionary<string, JsonElement> members, string name, string path)
    {
        var text = Text(members, name, path, MaxText);
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new ClassificationException(ClassificationCodes.ValueInvalid, path + "." + name, "expected a calendar date YYYY-MM-DD");
        return text;
    }

    static string Url(Dictionary<string, JsonElement> members, string name, string path)
    {
        var text = Text(members, name, path, MaxUrl);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new ClassificationException(ClassificationCodes.ValueInvalid, path + "." + name, "expected an absolute https URL");
        return text;
    }
}

// Several dictionaries side by side, chosen by id.
public sealed class ClassificationCatalog
{
    readonly Dictionary<string, ClassificationDictionary> byId = new(StringComparer.Ordinal);
    public ClassificationCatalog(IEnumerable<ClassificationDictionary> dictionaries)
    {
        ArgumentNullException.ThrowIfNull(dictionaries);
        foreach (var dictionary in dictionaries)
        {
            ArgumentNullException.ThrowIfNull(dictionary);
            if (!byId.TryAdd(dictionary.Id, dictionary)) throw new ClassificationException(ClassificationCodes.DuplicateDictionary, null, "dictionary " + dictionary.Id + " given twice");
        }
    }
    public IReadOnlyList<string> Ids => byId.Keys.Order(StringComparer.Ordinal).ToArray();
    public ClassificationDictionary Get(string id) => byId.TryGetValue(id, out var dictionary) ? dictionary : throw new ClassificationException(ClassificationCodes.UnknownDictionary, null, "no dictionary " + id);
    public ClassificationResult Map(string id, Project project) => Classification.Map(Get(id), project);
}

public static class Classification
{
    public static ClassificationResult Map(ClassificationDictionary dictionary, Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Map(dictionary, project.Elements);
    }

    public static ClassificationResult Map(ClassificationDictionary dictionary, Element element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Map(dictionary, new[] { element });
    }

    // Results are ordered by element id (ordinal over the lowercase "D" form), so the same input always gives the same lists.
    public static ClassificationResult Map(ClassificationDictionary dictionary, IEnumerable<Element> elements)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(elements);
        var source = new IfcClassificationSource(dictionary.Name, dictionary.Publisher, dictionary.Edition, dictionary.EditionDate, dictionary.SourceUrl);
        var rules = dictionary.Rules.ToDictionary(item => item.Kind, StringComparer.Ordinal);
        var exclusions = dictionary.Unmapped.ToDictionary(item => item.Kind, StringComparer.Ordinal);
        var list = elements.ToArray();
        if (list.Any(item => item is null)) throw new ArgumentException("elements must not contain null", nameof(elements));
        var seen = new HashSet<Guid>();
        var mapped = new List<ClassificationMapped>();
        var unmapped = new List<ClassificationUnmapped>();
        foreach (var element in list.OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            if (!seen.Add(element.Id)) throw new ClassificationException(ClassificationCodes.DuplicateElement, null, "element " + element.Id.ToString("D") + " given twice");
            var kind = element.Kind is string text ? text : "";
            if (rules.TryGetValue(kind, out var rule))
                mapped.Add(new(element.Id, kind, rule.Code, rule.Title, new(rule.Code, rule.Title, rule.Location, source)));
            else if (exclusions.TryGetValue(kind, out var exclusion))
                unmapped.Add(new(element.Id, kind, ClassificationCodes.Excluded, exclusion.Note));
            else
                unmapped.Add(new(element.Id, kind, ClassificationCodes.NoRule, "dictionary " + dictionary.Id + " has no rule for kind " + kind));
        }
        return new(dictionary.Id, dictionary.Version, mapped, unmapped);
    }
}
