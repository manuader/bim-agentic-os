using Bim.Core;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
// P10-02 BCF 3.0 export oracle: neutral validation issues become a deterministic BCF 3.0 zip (bcf.version,
// extensions.xml, project.bcfp, one folder per topic GUID with markup.bcf and an optional viewpoint.bcfv).
// Every XML entry is validated with System.Xml.Schema against tests/bcf/schemas (a project-authored structural subset
// of BCF-XML 3.0; see schemas/README.md). Unit-tested evidence only; no BCF viewer is exercised.
int passed = 0, failed = 0;

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::bcf FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}

string RepoRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException("repository root with AGENTS.md not found above " + AppContext.BaseDirectory);
}

string? Code(Action action)
{
    try { action(); return null; }
    catch (BcfExportException error) { return error.Code; }
}

// Independent re-derivation of the documented name-based GUID: SHA-256 over UTF-8 "bim-agentic-os/bcf/{kind}/{name}",
// first 16 bytes, RFC 9562 version 8 nibble and RFC 4122 variant bits, lowercase 8-4-4-4-12 in byte order.
string ExpectedGuid(string kind, string name)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes("bim-agentic-os/bcf/" + kind + "/" + name));
    hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
    hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
    var hex = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32];
}

var schemaDirectory = Path.Combine(RepoRoot(), "tests", "bcf", "schemas");
XmlSchemaSet Schema(string file)
{
    var set = new XmlSchemaSet { XmlResolver = null };
    using var reader = XmlReader.Create(Path.Combine(schemaDirectory, file), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
    set.Add(null, reader);
    set.Compile();
    return set;
}

var schemas = new Dictionary<string, XmlSchemaSet>(StringComparer.Ordinal);
List<string> Validate(byte[] xml, string schemaFile)
{
    if (!schemas.TryGetValue(schemaFile, out var set)) schemas[schemaFile] = set = Schema(schemaFile);
    var errors = new List<string>();
    var settings = new XmlReaderSettings
    {
        ValidationType = ValidationType.Schema,
        Schemas = set,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings | XmlSchemaValidationFlags.ProcessIdentityConstraints,
    };
    settings.ValidationEventHandler += (_, e) => errors.Add(e.Severity + ": " + e.Message + " @" + e.Exception?.LineNumber);
    try
    {
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read()) { }
    }
    catch (XmlException error) { errors.Add("malformed: " + error.Message); }
    return errors;
}

string SchemaFor(string path) => path switch
{
    "bcf.version" => "version.xsd",
    "extensions.xml" => "extensions.xsd",
    "project.bcfp" => "project.xsd",
    _ when path.EndsWith("/markup.bcf", StringComparison.Ordinal) => "markup.xsd",
    _ when path.EndsWith(".bcfv", StringComparison.Ordinal) => "visinfo.xsd",
    _ => throw new InvalidOperationException("unexpected entry " + path),
};

Dictionary<string, byte[]> ReadZip(byte[] zip, out List<ZipArchiveEntry> listing)
{
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    listing = new List<ZipArchiveEntry>();
    using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
    foreach (var entry in archive.Entries)
    {
        listing.Add(entry);
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        result[entry.FullName] = copy.ToArray();
    }
    return result;
}

// Raw ZIP structure: stored entries, fixed version made by / needed (20, host MS-DOS), zero flags, attributes, extras
// and comments, one DOS timestamp, correct CRC-32, sizes and offsets.
List<string> ZipHeaderProblems(byte[] zip, ushort time, ushort date)
{
    var problems = new List<string>();
    ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at));
    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at));
    var eocd = zip.Length - 22;
    if (eocd < 0 || U32(eocd) != 0x06054b50) { problems.Add("no end of central directory at the tail"); return problems; }
    int count = U16(eocd + 10);
    var centralSize = U32(eocd + 12);
    var centralOffset = U32(eocd + 16);
    if (U16(eocd + 4) != 0 || U16(eocd + 6) != 0 || U16(eocd + 8) != count || U16(eocd + 20) != 0 || centralOffset + centralSize != eocd) problems.Add("end record fields");
    var pos = (int)centralOffset;
    var expectedLocal = 0u;
    for (var i = 0; i < count; i++)
    {
        if (U32(pos) != 0x02014b50) { problems.Add("central signature " + i); return problems; }
        int nameLength = U16(pos + 28);
        var name = Encoding.ASCII.GetString(zip, pos + 46, nameLength);
        if (U16(pos + 4) != 20 || U16(pos + 6) != 20) problems.Add(name + " version made by/needed " + U16(pos + 4) + "/" + U16(pos + 6));
        if (U16(pos + 8) != 0 || U16(pos + 10) != 0) problems.Add(name + " flags/method " + U16(pos + 8) + "/" + U16(pos + 10));
        if (U16(pos + 12) != time || U16(pos + 14) != date) problems.Add(name + " central time/date " + U16(pos + 12) + "/" + U16(pos + 14));
        if (U16(pos + 30) != 0 || U16(pos + 32) != 0 || U16(pos + 34) != 0 || U16(pos + 36) != 0 || U32(pos + 38) != 0) problems.Add(name + " extra/comment/disk/attributes not zero");
        var crc = U32(pos + 16);
        var compressed = U32(pos + 20);
        var size = U32(pos + 24);
        var local = (int)U32(pos + 42);
        if (local != expectedLocal) problems.Add(name + " local offset " + local + " != " + expectedLocal);
        if (U32(local) != 0x04034b50 || U16(local + 4) != 20 || U16(local + 6) != 0 || U16(local + 8) != 0 || U16(local + 10) != time || U16(local + 12) != date
            || U32(local + 14) != crc || U32(local + 18) != compressed || U32(local + 22) != size || U16(local + 26) != nameLength || U16(local + 28) != 0
            || Encoding.ASCII.GetString(zip, local + 30, nameLength) != name) problems.Add(name + " local header mismatch");
        if (compressed != size) problems.Add(name + " is not stored");
        if (BcfZip.Crc32(zip.AsSpan(local + 30 + nameLength, (int)size)) != crc) problems.Add(name + " CRC-32 mismatch");
        expectedLocal = (uint)(local + 30 + nameLength + (int)size);
        pos += 46 + nameLength;
    }
    if (expectedLocal != centralOffset) problems.Add("data does not end at the central directory");
    return problems;
}

XDocument Parse(byte[] xml) => XDocument.Load(XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }));

var stamp = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
var options = new BcfExportOptions("tower-project", "Tower & Annex <A>", "bim-agentic-os@example.invalid", stamp, "AuthoringTool");
// DOS time/date of 2026-11-02T10:00:00Z: (10 << 11) and ((2026 - 1980) << 9 | 11 << 5 | 2).
const ushort DosTime = 20480;
const ushort DosDate = 23906;
// SHA-256 of WriteZip(issues, options) below; must be identical on Linux and Windows CI.
const string GoldenSha256 = "f787db0d5858cee76066d8e537e2fa6fe4a28227d5938e2a0bb7fe3c5c4cf7e8";
var wallGuid = new Guid("0f3c1d2e-4b5a-6978-8a9b-0c1d2e3f4a5b");
const string IfcCompressed = "1hZ8Hk$JfB$Ru5lYGCvzV8";
var issues = new List<BcfIssue>
{
    new("room-area-living", "Living room below minimum area", "Measured 7.5 m2 < required 9 m2.\nSee article 4.2.", BcfSeverity.Error,
        "es-ar/room.min_area", new[] { wallGuid.ToString(), IfcCompressed, "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b", IfcCompressed },
        BcfCamera.Perspective(new BcfVector(10, -5.5, 1.6), new BcfVector(0, 1, 0), new BcfVector(0, 0, 1), 60, 1.5)),
    new("door-width-entry", "Entry door narrower than 0.9 m", "Escaping test: <tag> & \"quotes\" 'apos' ]]> done", BcfSeverity.Warning,
        "es-ar/door.min_width", Array.Empty<string>(),
        BcfCamera.Orthogonal(new BcfVector(0, 0, 50), new BcfVector(0, 0, -1), new BcfVector(0, 1, 0), 0.02, 1.0)),
    new("brief-note", "Brief adjacency stated twice", null, BcfSeverity.Info),
};

try
{
    // ---- GUID derivation and IFC GUID compression ----
    Check(BcfExport.TopicGuid("room-area-living") == ExpectedGuid("topic", "room-area-living"), "topic GUID is SHA-256 name-based from the issue id",
        BcfExport.TopicGuid("room-area-living"));
    Check(BcfExport.ViewpointGuid("room-area-living") == ExpectedGuid("viewpoint", "room-area-living"), "viewpoint GUID is SHA-256 name-based from the issue id");
    Check(BcfExport.ProjectGuid("tower-project") == ExpectedGuid("project", "tower-project"), "project GUID is SHA-256 name-based from the project id");
    Check(BcfExport.TopicGuid("a") != BcfExport.TopicGuid("b") && BcfExport.TopicGuid("a") != BcfExport.ViewpointGuid("a"), "GUIDs differ by name and by kind");
    Check(Regex.IsMatch(BcfExport.TopicGuid("x"), "^[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"), "derived GUID is lowercase version 8 with RFC variant");
    Check(BcfExport.ToIfcGuid(Guid.Empty) == "0000000000000000000000", "IFC GUID of the empty GUID", BcfExport.ToIfcGuid(Guid.Empty));
    Check(BcfExport.ToIfcGuid(new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff")) == "3$$$$$$$$$$$$$$$$$$$$$", "IFC GUID of the all-ones GUID");
    Check(BcfExport.ToIfcGuid(new Guid("00000000-0000-0000-0000-000000000001")) == "0000000000000000000001", "IFC GUID keeps textual byte order (last bit)");
    Check(BcfExport.ToIfcGuid(new Guid("01000000-0000-0000-0000-000000000000")) == "0100000000000000000000", "IFC GUID keeps textual byte order (first byte)",
        BcfExport.ToIfcGuid(new Guid("01000000-0000-0000-0000-000000000000")));
    Check(BcfExport.ToIfcGuid(new Guid("00000000-0000-0000-0000-000000000040")) == "0000000000000000000010", "IFC GUID 64 maps to base-64 digit carry");
    // Mid-range pairs computed with a transcription of IfcOpenShell ifcopenshell.guid.compress (not with this code).
    Check(BcfExport.ToIfcGuid(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8")) == "1hfxWGdQqHqO2q0C1Fr338", "IFC GUID matches IfcOpenShell for the RFC 4122 DNS namespace UUID",
        BcfExport.ToIfcGuid(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8")));
    Check(BcfExport.ToIfcGuid(wallGuid) == "0FF1qkIrffU8gR31qkFqfR", "IFC GUID matches IfcOpenShell for the wall fixture", BcfExport.ToIfcGuid(wallGuid));
    Check(BcfZip.Crc32(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926u && BcfZip.Crc32(ReadOnlySpan<byte>.Empty) == 0u, "CRC-32 check value");
    Check(BcfZip.DosDateTime(stamp) == (DosTime, DosDate), "DOS date/time encoding");
    Check(BcfZip.DosDateTime(stamp.AddSeconds(1)) == (DosTime, DosDate) && BcfZip.DosDateTime(stamp.AddSeconds(3)).Time == DosTime + 1, "DOS time rounds odd seconds down");

    // ---- Severity mapping ----
    Check(BcfExport.Priority(BcfSeverity.Error) == "Error" && BcfExport.Priority(BcfSeverity.Warning) == "Warning" && BcfExport.Priority(BcfSeverity.Info) == "Info", "severity maps to BCF priority names");
    Check(BcfExport.FromSkill(SkillSeverity.Error) == BcfSeverity.Error && BcfExport.FromSkill(SkillSeverity.Warning) == BcfSeverity.Warning && BcfExport.FromSkill(SkillSeverity.Info) == BcfSeverity.Info, "skill severities map");
    Check(BcfExport.FromBrief(BriefSeverity.Error) == BcfSeverity.Error && BcfExport.FromBrief(BriefSeverity.Warning) == BcfSeverity.Warning, "brief severities map");
    Check(Code(() => BcfExport.FromSkill((SkillSeverity)9)) == BcfCodes.IssueInvalid && Code(() => BcfExport.FromBrief((BriefSeverity)9)) == BcfCodes.IssueInvalid && Code(() => BcfExport.Priority((BcfSeverity)9)) == BcfCodes.IssueInvalid, "undefined severities throw");

    // ---- Archive layout ----
    var zip = BcfExport.WriteZip(issues, options);
    var files = ReadZip(zip, out var listing);
    var living = BcfExport.TopicGuid("room-area-living");
    var door = BcfExport.TopicGuid("door-width-entry");
    var note = BcfExport.TopicGuid("brief-note");
    var topicOrder = new[] { ("brief-note", note), ("door-width-entry", door), ("room-area-living", living) };
    var expectedPaths = new List<string> { "bcf.version", "extensions.xml", "project.bcfp" };
    expectedPaths.Add(note + "/markup.bcf");
    expectedPaths.Add(door + "/markup.bcf");
    expectedPaths.Add(door + "/viewpoint.bcfv");
    expectedPaths.Add(living + "/markup.bcf");
    expectedPaths.Add(living + "/viewpoint.bcfv");
    var actualPaths = listing.Select(entry => entry.FullName).ToList();
    Check(actualPaths.SequenceEqual(expectedPaths), "zip entries in fixed order: root files then topics sorted by issue id (ordinal)", string.Join(",", actualPaths));
    Check(listing.All(entry => entry.LastWriteTime.DateTime == stamp.UtcDateTime), "every zip entry carries the injected timestamp",
        string.Join(",", listing.Select(entry => entry.LastWriteTime.ToString("O"))));
    Check(listing.All(entry => !entry.FullName.EndsWith('/') && !entry.FullName.StartsWith('/') && !entry.FullName.Contains('\\') && !entry.FullName.Contains("..")), "no directory, absolute or traversal entries");
    var headerProblems = ZipHeaderProblems(zip, DosTime, DosDate);
    Check(headerProblems.Count == 0, "raw zip headers are fixed, stored and CRC-correct", string.Join(" ; ", headerProblems));
    var actualSha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
    Check(actualSha == GoldenSha256, "WriteZip matches the committed golden SHA-256", "actual " + actualSha + " on " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    var entries = BcfExport.Entries(issues, options);
    Check(entries.Select(entry => entry.Path).SequenceEqual(expectedPaths) && entries.All(entry => files[entry.Path].AsSpan().SequenceEqual(entry.Content)), "Entries() equals the zipped contents");

    // ---- Schema validity of every entry ----
    foreach (var (path, content) in files)
    {
        var errors = Validate(content, SchemaFor(path));
        Check(errors.Count == 0, "schema-valid " + path.Split('/')[^1] + " (" + path[..Math.Min(8, path.Length)] + ")", string.Join(" ; ", errors));
        var text = Encoding.UTF8.GetString(content);
        Check(text.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", StringComparison.Ordinal) && content[0] == (byte)'<', "UTF-8 declaration without BOM in " + path, text[..Math.Min(60, text.Length)]);
        Check(!text.Contains('\r'), "LF line endings in " + path);
    }
    // The validator must actually reject non-conforming documents (guards against a vacuous oracle).
    Check(Validate(Encoding.UTF8.GetBytes("<Version VersionId=\"2.1\" />"), "version.xsd").Count > 0, "schema rejects a 2.1 version file");
    Check(Validate(Encoding.UTF8.GetBytes("<Markup><Topic Guid=\"" + note + "\" TopicType=\"Issue\" TopicStatus=\"Open\"><CreationDate>2026-11-02T10:00:00Z</CreationDate><CreationAuthor>a</CreationAuthor></Topic></Markup>"), "markup.xsd").Count > 0, "schema rejects a topic without Title");
    Check(Validate(Encoding.UTF8.GetBytes("<VisualizationInfo Guid=\"" + note + "\"><PerspectiveCamera><CameraViewPoint><X>0</X><Y>0</Y><Z>0</Z></CameraViewPoint><CameraDirection><X>1</X><Y>0</Y><Z>0</Z></CameraDirection><CameraUpVector><X>0</X><Y>0</Y><Z>1</Z></CameraUpVector><FieldOfView>90</FieldOfView><AspectRatio>1</AspectRatio></PerspectiveCamera></VisualizationInfo>"), "visinfo.xsd").Count > 0, "schema rejects a field of view outside 45..60");
    Check(Validate(Encoding.UTF8.GetBytes("<Extensions><Bogus /></Extensions>"), "extensions.xsd").Count > 0, "schema rejects unknown extensions children");

    // ---- Root documents ----
    var version = Parse(files["bcf.version"]).Root!;
    Check(version.Name.LocalName == "Version" && (string?)version.Attribute("VersionId") == "3.0", "bcf.version declares 3.0");
    var project = Parse(files["project.bcfp"]).Root!.Element("Project")!;
    Check((string?)project.Attribute("ProjectId") == BcfExport.ProjectGuid("tower-project") && (string?)project.Element("Name") == "Tower & Annex <A>", "project.bcfp carries derived id and escaped name round-trips");
    Check(Encoding.UTF8.GetString(files["project.bcfp"]).Contains("Tower &amp; Annex &lt;A&gt;", StringComparison.Ordinal), "project name is entity-escaped on the wire");
    var extensions = Parse(files["extensions.xml"]).Root!;
    List<string> Values(string list, string item) => extensions.Element(list)?.Elements(item).Select(e => e.Value).ToList() ?? new List<string>();
    Check(Values("TopicTypes", "TopicType").SequenceEqual(new[] { "Issue" }), "extensions list topic type Issue");
    Check(Values("TopicStatuses", "TopicStatus").SequenceEqual(new[] { "Open" }), "extensions list topic status Open");
    Check(Values("Priorities", "Priority").SequenceEqual(new[] { "Error", "Warning", "Info" }), "extensions list the three priorities");
    Check(Values("TopicLabels", "TopicLabel").SequenceEqual(new[] { "es-ar/door.min_width", "es-ar/room.min_area" }), "extensions list distinct rule ids sorted", string.Join(",", Values("TopicLabels", "TopicLabel")));
    Check(Values("Users", "User").SequenceEqual(new[] { "bim-agentic-os@example.invalid" }), "extensions list the author");
    foreach (var (path, content) in files.Where(f => f.Key.EndsWith("/markup.bcf", StringComparison.Ordinal)))
    {
        var topic = Parse(content).Root!.Element("Topic")!;
        var missing = new List<string>();
        void Need(string? value, string list, string item) { if (value is not null && !Values(list, item).Contains(value)) missing.Add(item + "=" + value); }
        Need((string?)topic.Attribute("TopicType"), "TopicTypes", "TopicType");
        Need((string?)topic.Attribute("TopicStatus"), "TopicStatuses", "TopicStatus");
        Need((string?)topic.Element("Priority"), "Priorities", "Priority");
        Need((string?)topic.Element("CreationAuthor"), "Users", "User");
        foreach (var label in topic.Element("Labels")?.Elements("Label") ?? Enumerable.Empty<XElement>()) Need(label.Value, "TopicLabels", "TopicLabel");
        Check(missing.Count == 0, "markup values are declared in extensions.xml " + path[..8], string.Join(",", missing));
    }

    // ---- Markup per topic ----
    foreach (var (issueId, guid) in topicOrder)
    {
        var issue = issues.Single(i => i.Id == issueId);
        var topic = Parse(files[guid + "/markup.bcf"]).Root!.Element("Topic")!;
        Check((string?)topic.Attribute("Guid") == guid && (string?)topic.Attribute("TopicType") == "Issue" && (string?)topic.Attribute("TopicStatus") == "Open", "topic attributes " + issueId);
        Check((string?)topic.Element("Title") == issue.Title, "title round-trips " + issueId);
        Check((string?)topic.Element("Priority") == BcfExport.Priority(issue.Severity), "priority " + issueId);
        Check((string?)topic.Element("CreationDate") == "2026-11-02T10:00:00Z" && (string?)topic.Element("CreationAuthor") == options.Author, "creation date and author injected " + issueId);
        var labels = topic.Element("Labels")?.Elements("Label").Select(e => e.Value).ToList() ?? new List<string>();
        Check(issue.RuleId is null ? topic.Element("Labels") is null : labels.SequenceEqual(new[] { issue.RuleId }), "rule id label " + issueId);
        Check(issue.Description is null ? topic.Element("Description") is null : (string?)topic.Element("Description") == issue.Description, "description round-trips " + issueId, (string?)topic.Element("Description") ?? "<none>");
        var hasViewpoint = issue.Camera is not null || issue.ElementIds is { Count: > 0 };
        var viewpoints = topic.Element("Viewpoints")?.Elements("ViewPoint").ToList() ?? new List<XElement>();
        Check(hasViewpoint
            ? viewpoints.Count == 1 && (string?)viewpoints[0].Attribute("Guid") == BcfExport.ViewpointGuid(issueId) && (string?)viewpoints[0].Element("Viewpoint") == "viewpoint.bcfv"
            : topic.Element("Viewpoints") is null && !files.ContainsKey(guid + "/viewpoint.bcfv"), "viewpoint reference " + issueId);
    }
    var doorMarkup = Encoding.UTF8.GetString(files[door + "/markup.bcf"]);
    Check(doorMarkup.Contains("&lt;tag&gt; &amp; \"quotes\" 'apos' ]]&gt; done", StringComparison.Ordinal), "description special characters escaped on the wire", doorMarkup);

    // ---- Viewpoints ----
    var livingView = Parse(files[living + "/viewpoint.bcfv"]).Root!;
    Check((string?)livingView.Attribute("Guid") == BcfExport.ViewpointGuid("room-area-living"), "visualization info GUID matches the markup ViewPoint");
    var components = livingView.Element("Components")!.Element("Selection")!.Elements("Component").ToList();
    var ifcGuids = components.Select(c => (string?)c.Attribute("IfcGuid")).Where(v => v is not null).ToList();
    var toolIds = components.Select(c => (string?)c.Element("AuthoringToolId")).Where(v => v is not null).ToList();
    var expectedIfc = new[] { BcfExport.ToIfcGuid(wallGuid), IfcCompressed }.OrderBy(v => v, StringComparer.Ordinal).ToList();
    Check(ifcGuids.SequenceEqual(expectedIfc), "GUID ids become IfcGuid (converted, passthrough, deduplicated, ordinal order)", string.Join(",", ifcGuids));
    Check(toolIds.SequenceEqual(new[] { "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b" }), "non-GUID ids become AuthoringToolId", string.Join(",", toolIds));
    Check(components.All(c => c.Element("AuthoringToolId") is null ? c.Element("OriginatingSystem") is null : (string?)c.Element("OriginatingSystem") == "AuthoringTool"), "OriginatingSystem accompanies AuthoringToolId only");
    var noSystem = BcfExport.Entries(new[] { new BcfIssue("tool", "Tool id", null, BcfSeverity.Error, null, new[] { "tool-1" }) }, options with { OriginatingSystem = null });
    Check(!Encoding.UTF8.GetString(noSystem.Single(e => e.Path.EndsWith(".bcfv", StringComparison.Ordinal)).Content).Contains("OriginatingSystem", StringComparison.Ordinal), "OriginatingSystem omitted when not given");
    var variants = BcfExport.Entries(new[] { new BcfIssue("case", "GUID spellings", null, BcfSeverity.Error, null,
        new[] { wallGuid.ToString().ToUpperInvariant(), wallGuid.ToString("B"), wallGuid.ToString("B").ToUpperInvariant(), wallGuid.ToString() }) }, options);
    var variantView = Parse(variants.Single(e => e.Path.EndsWith(".bcfv", StringComparison.Ordinal)).Content).Root!;
    var variantComponents = variantView.Element("Components")!.Element("Selection")!.Elements("Component").ToList();
    Check(variantComponents.Count == 1 && (string?)variantComponents[0].Attribute("IfcGuid") == "0FF1qkIrffU8gR31qkFqfR", "uppercase and braced GUID spellings convert to one IfcGuid", variantView.ToString());
    Check(components.Count == 3, "three distinct components selected", components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Check((string?)livingView.Element("Components")!.Element("Visibility")!.Attribute("DefaultVisibility") == "true", "default visibility true keeps context visible");
    var perspective = livingView.Element("PerspectiveCamera")!;
    Check((string?)perspective.Element("CameraViewPoint")!.Element("Y") == "-5.5" && (string?)perspective.Element("FieldOfView") == "60" && (string?)perspective.Element("AspectRatio") == "1.5", "perspective camera values written invariantly");
    var doorView = Parse(files[door + "/viewpoint.bcfv"]).Root!;
    Check(doorView.Element("Components") is null && (string?)doorView.Element("OrthogonalCamera")!.Element("ViewToWorldScale") == "0.02", "orthogonal camera without selection");
    var selectionOnly = BcfExport.Entries(new[] { new BcfIssue("sel", "Selection only", null, BcfSeverity.Error, null, new[] { IfcCompressed }) }, options);
    var selectionView = selectionOnly.Single(e => e.Path.EndsWith(".bcfv", StringComparison.Ordinal));
    Check(Validate(selectionView.Content, "visinfo.xsd").Count == 0 && Parse(selectionView.Content).Root!.Element("PerspectiveCamera") is null, "selection-only viewpoint has no camera and is schema-valid");

    // ---- Determinism ----
    var again = BcfExport.WriteZip(issues, options);
    Check(again.AsSpan().SequenceEqual(zip), "same input gives byte-identical zip");
    var reversed = BcfExport.WriteZip(Enumerable.Reverse(issues).ToList(), options);
    Check(reversed.AsSpan().SequenceEqual(zip), "input order does not change the zip");
    var shuffledElements = issues.Select(i => i.Id == "room-area-living" ? i with { ElementIds = i.ElementIds!.Reverse().ToList() } : i).ToList();
    Check(BcfExport.WriteZip(shuffledElements, options).AsSpan().SequenceEqual(zip), "element id order does not change the zip");
    Check(!BcfExport.WriteZip(issues, options with { Timestamp = stamp.AddDays(1) }).AsSpan().SequenceEqual(zip), "a different timestamp changes the zip");
    Check(BcfExport.WriteZip(issues, options with { Timestamp = new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.FromHours(-3)) }).AsSpan().SequenceEqual(zip), "timestamps are normalized to UTC");
    var odd = options with { Timestamp = stamp.AddSeconds(1) };
    var oddZip = BcfExport.WriteZip(issues, odd);
    var oddMarkup = Parse(ReadZip(oddZip, out _)[living + "/markup.bcf"]).Root!.Element("Topic")!;
    Check((string?)oddMarkup.Element("CreationDate") == "2026-11-02T10:00:01Z" && ZipHeaderProblems(oddZip, DosTime, DosDate).Count == 0, "odd second: CreationDate keeps it, zip time rounds down");
    var empty = BcfExport.Entries(Array.Empty<BcfIssue>(), options);
    Check(empty.Select(e => e.Path).SequenceEqual(new[] { "bcf.version", "extensions.xml", "project.bcfp" }) && empty.All(e => Validate(e.Content, SchemaFor(e.Path)).Count == 0), "an empty issue list is a valid archive without topics");

    // ---- Bounded, validated input ----
    var up = new BcfVector(0, 0, 1);
    var forward = new BcfVector(1, 0, 0);
    BcfIssue Ok(string id = "ok") => new(id, "Title", "Body", BcfSeverity.Error);
    string? Refuse(params BcfIssue[] list) => Code(() => BcfExport.WriteZip(list, options));
    Check(Code(() => BcfExport.WriteZip(Enumerable.Range(0, BcfLimits.MaxIssues + 1).Select(i => Ok("i" + i)).ToList(), options)) == BcfCodes.IssuesTooMany, "too many issues refused");
    Check(Code(() => BcfExport.Entries(Enumerable.Range(0, BcfLimits.MaxIssues).Select(i => Ok("i" + i)).ToList(), options)) is null, "exactly the issue limit is accepted");
    Check(Refuse(Ok("dup"), Ok("dup")) == BcfCodes.IdDuplicate, "duplicate issue ids refused");
    Check(Refuse(Ok("")) == BcfCodes.IdInvalid && Refuse(Ok("  ")) == BcfCodes.IdInvalid && Refuse(Ok(new string('a', BcfLimits.MaxIdLength + 1))) == BcfCodes.IdInvalid, "empty, blank or overlong issue id refused");
    Check(Refuse(Ok() with { Title = " " }) == BcfCodes.TextInvalid, "blank title refused");
    Check(Refuse(Ok() with { Title = new string('t', BcfLimits.MaxTitleLength + 1) }) == BcfCodes.TextTooLong, "overlong title refused");
    Check(Refuse(Ok() with { Description = new string('d', BcfLimits.MaxDescriptionLength + 1) }) == BcfCodes.TextTooLong, "overlong description refused");
    Check(Refuse(Ok() with { Title = "bell\u0007" }) == BcfCodes.TextInvalid && Refuse(Ok() with { Description = "nul\0" }) == BcfCodes.TextInvalid && Refuse(Ok() with { Title = "lone \uD800" }) == BcfCodes.TextInvalid, "XML-illegal characters refused");
    Check(Refuse(Ok() with { Title = "line\nbreak" }) == BcfCodes.TextInvalid, "line breaks refused in single-line fields");
    Check(Refuse(Ok() with { Description = "multi\nline\ttext \U0001F600" }) is null, "description allows newlines, tabs and astral characters");
    Check(Refuse(Ok() with { RuleId = "" }) == BcfCodes.TextInvalid && Refuse(Ok() with { RuleId = new string('r', BcfLimits.MaxIdLength + 1) }) == BcfCodes.TextTooLong, "empty or overlong rule id refused");
    Check(Refuse(Ok() with { Severity = (BcfSeverity)7 }) == BcfCodes.IssueInvalid, "undefined severity refused");
    Check(Code(() => BcfExport.WriteZip(new BcfIssue[] { null! }, options)) == BcfCodes.IssueInvalid, "null issue refused");
    Check(Refuse(Ok() with { ElementIds = Enumerable.Range(0, BcfLimits.MaxElementsPerIssue + 1).Select(i => "e" + i).ToList() }) == BcfCodes.ElementsInvalid, "too many element ids refused");
    Check(BcfLimits.MaxElementsPerIssue == 1000 && Refuse(Ok() with { ElementIds = Enumerable.Range(0, 1000).Select(i => "e" + i).ToList() }) is null, "exactly 1000 element ids accepted");
    Check(Refuse(Ok(" x")) == BcfCodes.IdInvalid && Refuse(Ok("x ")) == BcfCodes.IdInvalid && Refuse(Ok("x")) is null && Refuse(Ok(" x"), Ok("x")) == BcfCodes.IdInvalid, "issue ids with surrounding whitespace refused");
    Check(Refuse(Ok() with { ElementIds = new[] { " x", "x" } }) == BcfCodes.ElementsInvalid && Refuse(Ok() with { RuleId = " r" }) == BcfCodes.TextInvalid, "element and rule ids with surrounding whitespace refused");
    Check(Code(() => BcfExport.WriteZip(new[] { Ok() }, options with { Author = "a " })) == BcfCodes.OptionsInvalid && Code(() => BcfExport.WriteZip(new[] { Ok() }, options with { OriginatingSystem = " " })) == BcfCodes.OptionsInvalid, "author and originating system must be trimmed identifiers");
    Check(Refuse(Ok() with { Camera = BcfCamera.Perspective(new BcfVector(0, 0, 0), new BcfVector(0, 0, 2), up, 50, 1) }) == BcfCodes.CameraInvalid && Refuse(Ok() with { Camera = BcfCamera.Orthogonal(new BcfVector(0, 0, 0), new BcfVector(0, 0, -1), up, 1, 1) }) == BcfCodes.CameraInvalid, "parallel direction and up vector refused");
    Check(Refuse(Ok() with { ElementIds = new[] { "" } }) == BcfCodes.ElementsInvalid && Refuse(Ok() with { ElementIds = new[] { new string('e', BcfLimits.MaxElementIdLength + 1) } }) == BcfCodes.ElementsInvalid && Refuse(Ok() with { ElementIds = new[] { "bad\u0001" } }) == BcfCodes.ElementsInvalid, "empty, overlong or illegal element ids refused");
    Check(Refuse(Ok() with { Camera = BcfCamera.Perspective(new BcfVector(double.NaN, 0, 0), forward, up, 50, 1) }) == BcfCodes.CameraInvalid, "NaN camera refused");
    Check(Refuse(Ok() with { Camera = BcfCamera.Perspective(new BcfVector(0, 0, 0), forward, up, 90, 1) }) == BcfCodes.CameraInvalid, "field of view outside 45..60 refused");
    Check(Refuse(Ok() with { Camera = BcfCamera.Perspective(new BcfVector(0, 0, 0), new BcfVector(0, 0, 0), up, 50, 1) }) == BcfCodes.CameraInvalid, "zero direction refused");
    Check(Refuse(Ok() with { Camera = BcfCamera.Orthogonal(new BcfVector(0, 0, 0), forward, up, 0, 1) }) == BcfCodes.CameraInvalid && Refuse(Ok() with { Camera = BcfCamera.Orthogonal(new BcfVector(0, 0, 0), forward, up, 1, -1) }) == BcfCodes.CameraInvalid, "non-positive scale or aspect refused");
    Check(Refuse(Ok() with { Camera = BcfCamera.Perspective(new BcfVector(1e12, 0, 0), forward, up, 50, 1) }) == BcfCodes.CameraInvalid, "out-of-range coordinate refused");
    Check(Code(() => BcfExport.WriteZip(new[] { Ok() }, options with { Author = "" })) == BcfCodes.OptionsInvalid, "empty author refused");
    Check(Code(() => BcfExport.WriteZip(new[] { Ok() }, options with { ProjectId = " " })) == BcfCodes.OptionsInvalid, "blank project id refused");
    Check(Code(() => BcfExport.WriteZip(new[] { Ok() }, options with { Timestamp = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero) })) == BcfCodes.OptionsInvalid, "timestamp outside the zip date range refused");
    // Whole-export budget with every field at its maximum (documented estimate: 6 bytes per character, OriginatingSystem
    // counted once per element id, + 4096 per issue + 128 per element).
    var maxElements = Enumerable.Range(0, BcfLimits.MaxElementsPerIssue).Select(i => ("el-" + i).PadRight(BcfLimits.MaxElementIdLength, 'e')).ToList();
    var maxCamera = BcfCamera.Perspective(new BcfVector(1, 2, 3), forward, up, 50, 1);
    BcfIssue Max(int i) => new(("max-" + i).PadRight(BcfLimits.MaxIdLength, 'x'), new string('T', BcfLimits.MaxTitleLength), new string('D', BcfLimits.MaxDescriptionLength),
        BcfSeverity.Warning, new string('r', BcfLimits.MaxIdLength), maxElements, maxCamera);
    var perIssue = 6L * (BcfLimits.MaxIdLength + BcfLimits.MaxTitleLength + BcfLimits.MaxDescriptionLength + BcfLimits.MaxIdLength + BcfLimits.MaxElementsPerIssue * (BcfLimits.MaxElementIdLength + options.OriginatingSystem!.Length)) + 4096 + 128L * BcfLimits.MaxElementsPerIssue;
    var baseBytes = 6L * (options.ProjectId.Length + options.ProjectName.Length + options.Author.Length + options.OriginatingSystem!.Length) + 4096;
    var fits = (int)((BcfLimits.MaxTotalBytes - baseBytes) / perIssue);
    Check(BcfLimits.MaxTotalBytes == 64L * 1024 * 1024 && fits > 0 && fits < BcfLimits.MaxIssues, "budget is 64 MiB and binds before the issue count", fits.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Check(Code(() => BcfExport.WriteZip(Enumerable.Range(0, BcfLimits.MaxIssues).Select(Max).ToList(), options)) == BcfCodes.TotalTooLarge, "every field at its maximum for every issue refused as too large");
    Check(Code(() => BcfExport.Entries(Enumerable.Range(0, fits + 1).Select(Max).ToList(), options)) == BcfCodes.TotalTooLarge, "one maximal issue over the budget refused");
    long acceptedBytes = -1;
    Check(Code(() => acceptedBytes = BcfExport.WriteZip(Enumerable.Range(0, fits).Select(Max).ToList(), options).LongLength) is null && acceptedBytes > 0 && acceptedBytes <= BcfLimits.MaxTotalBytes + 1024 * 1024, "maximal issues up to the budget accepted", acceptedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Check(Code(() => BcfExport.WriteZip(new[] { Ok() }, null!)) == BcfCodes.OptionsInvalid && Code(() => BcfExport.WriteZip(null!, options)) == BcfCodes.IssueInvalid, "null arguments refused with codes");
}
catch (Exception error)
{
    Console.WriteLine("::error::bcf crashed: " + error.ToString().Replace('\n', ' ').Replace('\r', ' '));
    failed++;
}
Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, bcf_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
