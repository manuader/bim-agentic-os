using System.Text.Json;
namespace Bim.Core;

// One entry of capabilities/capabilities.json. Not part of persisted BIM-IR state.
public sealed record Capability(string Id, string CoverageStatus, bool NativeVerified, string[] VersionScope, string[] Limitations, string[] VerificationTests)
{
    const string NativePrefix = "revit.";
    const string CreateSuffix = ".create";

    // "revit.<kind>.create" declares native authoring for that BIM-IR kind.
    public string? NativeCreateKind =>
        Id.StartsWith(NativePrefix, StringComparison.Ordinal) && Id.EndsWith(CreateSuffix, StringComparison.Ordinal) && Id.Length > NativePrefix.Length + CreateSuffix.Length
            ? Id[NativePrefix.Length..^CreateSuffix.Length]
            : null;
}

public sealed class CapabilityRegistry
{
    readonly Dictionary<string, Capability> byId = new(StringComparer.Ordinal);

    public CapabilityRegistry(IEnumerable<Capability> capabilities)
    {
        var ordered = new List<Capability>();
        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability.Id)) throw new ArgumentException("invalid_capabilities");
            if (!byId.TryAdd(capability.Id, capability)) throw new ArgumentException("duplicate_capability");
            ordered.Add(capability);
        }
        Capabilities = ordered.AsReadOnly();
        NativeCreateKinds = ordered.Select(capability => capability.NativeCreateKind).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<Capability> Capabilities { get; }
    public IEnumerable<string> Ids => Capabilities.Select(capability => capability.Id);
    public IReadOnlyList<string> NativeCreateKinds { get; }
    public bool Contains(string id) => byId.ContainsKey(id);
    public bool SupportsNativeKind(string kind) => NativeCreateKinds.Contains(kind, StringComparer.Ordinal);

    // Built-in mirror of capabilities/capabilities.json for callers that cannot read the file
    // (for example inside the Revit add-in). A test keeps both identifier sets equal.
    public static CapabilityRegistry NativeDefault { get; } = new(new[] { "revit.level.create", "revit.wall.create", "revit.wall.axis.update", "revit.element.inspect", "revit.element.delete" }
        .Select(id => new Capability(id, "source-implemented", false, new[] { "25.2.0.38" }, Array.Empty<string>(), Array.Empty<string>())));

    public static CapabilityRegistry Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var capabilities = new List<Capability>();
            foreach (var item in document.RootElement.GetProperty("capabilities").EnumerateArray())
            {
                capabilities.Add(new Capability(Text(item, "id"), Text(item, "coverage_status"), item.GetProperty("native_verified").GetBoolean(),
                    Texts(item, "version_scope"), Texts(item, "limitations"), Texts(item, "verification_tests")));
            }
            return new CapabilityRegistry(capabilities);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ArgumentException("invalid_capabilities", error);
        }
    }

    static string Text(JsonElement item, string name) =>
        item.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new ArgumentException("invalid_capabilities");

    static string[] Texts(JsonElement item, string name) =>
        item.TryGetProperty(name, out var values)
            ? values.EnumerateArray().Select(value => value.GetString() ?? throw new ArgumentException("invalid_capabilities")).ToArray()
            : Array.Empty<string>();
}
