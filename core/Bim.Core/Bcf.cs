using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Bim.Core;

// P10-02 BCF 3.0 export. A small neutral issue model (independent of the validators that produce it) is written as a
// deterministic BCF 3.0 archive: bcf.version, extensions.xml, project.bcfp and one folder per topic GUID holding
// markup.bcf and, when the issue has a camera or affected elements, viewpoint.bcfv.
// Determinism: GUIDs are name-based (SHA-256 of "bim-agentic-os/bcf/{kind}/{name}", RFC 9562 version 8), the
// timestamp is injected and normalized to UTC, topics are ordered by issue id (ordinal), element ids are deduplicated
// and ordered, and every zip entry carries the same timestamp, so the same input yields a byte-identical zip.
public enum BcfSeverity { Error, Warning, Info }

public enum BcfCameraKind { Perspective, Orthogonal }

// Coordinates in metres in the model coordinate system, as BCF requires.
public readonly record struct BcfVector(double X, double Y, double Z);

// Value is the field of view in degrees (perspective, 45..60 in BCF 3.0) or the view-to-world scale (orthogonal).
public sealed record BcfCamera(BcfCameraKind Kind, BcfVector ViewPoint, BcfVector Direction, BcfVector UpVector, double Value, double AspectRatio)
{
    public static BcfCamera Perspective(BcfVector viewPoint, BcfVector direction, BcfVector upVector, double fieldOfView, double aspectRatio) =>
        new(BcfCameraKind.Perspective, viewPoint, direction, upVector, fieldOfView, aspectRatio);

    public static BcfCamera Orthogonal(BcfVector viewPoint, BcfVector direction, BcfVector upVector, double viewToWorldScale, double aspectRatio) =>
        new(BcfCameraKind.Orthogonal, viewPoint, direction, upVector, viewToWorldScale, aspectRatio);
}

// ElementIds: canonical GUIDs (converted to IFC compressed GUIDs), 22-character IFC GUIDs (kept), or any other
// authoring-tool id (written as AuthoringToolId). RuleId becomes the topic label.
public sealed record BcfIssue(string Id, string Title, string? Description, BcfSeverity Severity, string? RuleId = null,
    IReadOnlyList<string>? ElementIds = null, BcfCamera? Camera = null);

public sealed record BcfExportOptions(string ProjectId, string ProjectName, string Author, DateTimeOffset Timestamp);

public sealed record BcfEntry(string Path, byte[] Content);

public static class BcfLimits
{
    public const int MaxIssues = 2000;
    public const int MaxIdLength = 128;
    public const int MaxTitleLength = 256;
    public const int MaxDescriptionLength = 8192;
    public const int MaxElementsPerIssue = 1000;
    public const int MaxElementIdLength = 128;
    public const double MaxCoordinate = 1e7;
}

public static class BcfCodes
{
    public const string IssuesTooMany = "bcf_issues_too_many";
    public const string IssueInvalid = "bcf_issue_invalid";
    public const string IdInvalid = "bcf_issue_id_invalid";
    public const string IdDuplicate = "bcf_issue_id_duplicate";
    public const string TextInvalid = "bcf_text_invalid";
    public const string TextTooLong = "bcf_text_too_long";
    public const string ElementsInvalid = "bcf_elements_invalid";
    public const string CameraInvalid = "bcf_camera_invalid";
    public const string OptionsInvalid = "bcf_options_invalid";
}

public sealed class BcfExportException : ArgumentException
{
    public BcfExportException(string code, string? detail = null) : base(detail is null ? code : code + ": " + detail)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }
    public string? Detail { get; }
}

public static class BcfExport
{
    public const string VersionId = "3.0";
    public const string TopicType = "Issue";
    public const string TopicStatus = "Open";
    public const string ViewpointFile = "viewpoint.bcfv";
    private const string IfcAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
    private static readonly string[] Priorities = { "Error", "Warning", "Info" };

    public static string Priority(BcfSeverity severity) =>
        Enum.IsDefined(severity) ? Priorities[(int)severity] : throw new BcfExportException(BcfCodes.IssueInvalid, "undefined severity");

    public static BcfSeverity FromSkill(SkillSeverity severity) => severity switch
    {
        SkillSeverity.Error => BcfSeverity.Error,
        SkillSeverity.Warning => BcfSeverity.Warning,
        _ => BcfSeverity.Info,
    };

    public static BcfSeverity FromBrief(BriefSeverity severity) => severity == BriefSeverity.Error ? BcfSeverity.Error : BcfSeverity.Warning;

    public static string TopicGuid(string issueId) => DeriveGuid("topic", issueId);
    public static string ViewpointGuid(string issueId) => DeriveGuid("viewpoint", issueId);
    public static string ProjectGuid(string projectId) => DeriveGuid("project", projectId);

    public static string DeriveGuid(string kind, string name)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("bim-agentic-os/bcf/" + kind + "/" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32];
    }

    // IFC compressed GUID (22 base-64 characters) over the 128 bits in canonical textual order.
    public static string ToIfcGuid(Guid guid)
    {
        var bytes = Convert.FromHexString(guid.ToString("N", CultureInfo.InvariantCulture));
        var text = new StringBuilder(22);
        text.Append(IfcAlphabet[bytes[0] / 64]).Append(IfcAlphabet[bytes[0] % 64]);
        for (var i = 1; i < 16; i += 3)
        {
            var value = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
            for (var shift = 18; shift >= 0; shift -= 6) text.Append(IfcAlphabet[(value >> shift) & 63]);
        }
        return text.ToString();
    }

    public static byte[] WriteZip(IReadOnlyList<BcfIssue> issues, BcfExportOptions options)
    {
        var entries = Entries(issues, options);
        var stamp = ZipStamp(options.Timestamp);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Path, CompressionLevel.Optimal);
                zipEntry.LastWriteTime = stamp;
                using var stream = zipEntry.Open();
                stream.Write(entry.Content);
            }
        }
        return buffer.ToArray();
    }

    public static IReadOnlyList<BcfEntry> Entries(IReadOnlyList<BcfIssue> issues, BcfExportOptions options)
    {
        if (options is null) throw new BcfExportException(BcfCodes.OptionsInvalid, "options are required");
        if (issues is null) throw new BcfExportException(BcfCodes.IssueInvalid, "issues are required");
        ValidateOptions(options);
        if (issues.Count > BcfLimits.MaxIssues) throw new BcfExportException(BcfCodes.IssuesTooMany, issues.Count + " > " + BcfLimits.MaxIssues);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var prepared = new List<(BcfIssue Issue, IReadOnlyList<Component> Components)>(issues.Count);
        foreach (var issue in issues)
        {
            var components = ValidateIssue(issue);
            if (!ids.Add(issue.Id)) throw new BcfExportException(BcfCodes.IdDuplicate, issue.Id);
            prepared.Add((issue, components));
        }
        prepared.Sort((left, right) => string.CompareOrdinal(left.Issue.Id, right.Issue.Id));
        var date = options.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var entries = new List<BcfEntry>
        {
            new("bcf.version", Xml(writer =>
            {
                writer.WriteStartElement("Version");
                writer.WriteAttributeString("VersionId", VersionId);
                writer.WriteEndElement();
            })),
            new("extensions.xml", Xml(writer =>
            {
                writer.WriteStartElement("Extensions");
                List(writer, "TopicTypes", "TopicType", new[] { TopicType });
                List(writer, "TopicStatuses", "TopicStatus", new[] { TopicStatus });
                List(writer, "Priorities", "Priority", Priorities);
                List(writer, "TopicLabels", "TopicLabel", prepared.Select(p => p.Issue.RuleId).OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal));
                List(writer, "Users", "User", new[] { options.Author });
                writer.WriteEndElement();
            })),
            new("project.bcfp", Xml(writer =>
            {
                writer.WriteStartElement("ProjectInfo");
                writer.WriteStartElement("Project");
                writer.WriteAttributeString("ProjectId", ProjectGuid(options.ProjectId));
                writer.WriteElementString("Name", options.ProjectName);
                writer.WriteEndElement();
                writer.WriteEndElement();
            })),
        };

        foreach (var (issue, components) in prepared)
        {
            var topicGuid = TopicGuid(issue.Id);
            var viewpointGuid = ViewpointGuid(issue.Id);
            var hasViewpoint = issue.Camera is not null || components.Count > 0;
            entries.Add(new(topicGuid + "/markup.bcf", Xml(writer =>
            {
                writer.WriteStartElement("Markup");
                writer.WriteStartElement("Topic");
                writer.WriteAttributeString("Guid", topicGuid);
                writer.WriteAttributeString("TopicType", TopicType);
                writer.WriteAttributeString("TopicStatus", TopicStatus);
                writer.WriteElementString("Title", issue.Title);
                writer.WriteElementString("Priority", Priority(issue.Severity));
                if (issue.RuleId is not null)
                {
                    writer.WriteStartElement("Labels");
                    writer.WriteElementString("Label", issue.RuleId);
                    writer.WriteEndElement();
                }
                writer.WriteElementString("CreationDate", date);
                writer.WriteElementString("CreationAuthor", options.Author);
                if (!string.IsNullOrEmpty(issue.Description)) writer.WriteElementString("Description", issue.Description);
                if (hasViewpoint)
                {
                    writer.WriteStartElement("Viewpoints");
                    writer.WriteStartElement("ViewPoint");
                    writer.WriteAttributeString("Guid", viewpointGuid);
                    writer.WriteElementString("Viewpoint", ViewpointFile);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            })));
            if (hasViewpoint) entries.Add(new(topicGuid + "/" + ViewpointFile, Xml(writer => Viewpoint(writer, viewpointGuid, components, issue.Camera))));
        }
        return entries;
    }

    private sealed record Component(string? IfcGuid, string? AuthoringToolId)
    {
        public string Key => IfcGuid ?? AuthoringToolId!;
    }

    private static void Viewpoint(XmlWriter writer, string guid, IReadOnlyList<Component> components, BcfCamera? camera)
    {
        writer.WriteStartElement("VisualizationInfo");
        writer.WriteAttributeString("Guid", guid);
        if (components.Count > 0)
        {
            writer.WriteStartElement("Components");
            writer.WriteStartElement("Selection");
            foreach (var component in components)
            {
                writer.WriteStartElement("Component");
                if (component.IfcGuid is not null) writer.WriteAttributeString("IfcGuid", component.IfcGuid);
                if (component.AuthoringToolId is not null) writer.WriteElementString("AuthoringToolId", component.AuthoringToolId);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("Visibility");
            writer.WriteAttributeString("DefaultVisibility", XmlConvert.ToString(true));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        if (camera is not null)
        {
            var perspective = camera.Kind == BcfCameraKind.Perspective;
            writer.WriteStartElement(perspective ? "PerspectiveCamera" : "OrthogonalCamera");
            Vector(writer, "CameraViewPoint", camera.ViewPoint);
            Vector(writer, "CameraDirection", camera.Direction);
            Vector(writer, "CameraUpVector", camera.UpVector);
            writer.WriteElementString(perspective ? "FieldOfView" : "ViewToWorldScale", XmlConvert.ToString(camera.Value));
            writer.WriteElementString("AspectRatio", XmlConvert.ToString(camera.AspectRatio));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void Vector(XmlWriter writer, string name, BcfVector vector)
    {
        writer.WriteStartElement(name);
        writer.WriteElementString("X", XmlConvert.ToString(vector.X));
        writer.WriteElementString("Y", XmlConvert.ToString(vector.Y));
        writer.WriteElementString("Z", XmlConvert.ToString(vector.Z));
        writer.WriteEndElement();
    }

    private static void List(XmlWriter writer, string list, string item, IEnumerable<string> values)
    {
        writer.WriteStartElement(list);
        foreach (var value in values) writer.WriteElementString(item, value);
        writer.WriteEndElement();
    }

    private static byte[] Xml(Action<XmlWriter> body)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            CheckCharacters = true,
        };
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument();
            body(writer);
            writer.WriteEndDocument();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static DateTimeOffset ZipStamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        var even = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second - utc.Second % 2, DateTimeKind.Unspecified);
        return new DateTimeOffset(even, TimeSpan.Zero);
    }

    private static void ValidateOptions(BcfExportOptions options)
    {
        if (!IsLine(options.ProjectId, BcfLimits.MaxIdLength)) throw new BcfExportException(BcfCodes.OptionsInvalid, "project id");
        if (!IsLine(options.ProjectName, BcfLimits.MaxTitleLength)) throw new BcfExportException(BcfCodes.OptionsInvalid, "project name");
        if (!IsLine(options.Author, BcfLimits.MaxIdLength)) throw new BcfExportException(BcfCodes.OptionsInvalid, "author");
        var year = options.Timestamp.UtcDateTime.Year;
        if (year < 1980 || year > 2107) throw new BcfExportException(BcfCodes.OptionsInvalid, "timestamp outside 1980..2107");
    }

    private static IReadOnlyList<Component> ValidateIssue(BcfIssue? issue)
    {
        if (issue is null) throw new BcfExportException(BcfCodes.IssueInvalid, "null issue");
        if (!IsLine(issue.Id, BcfLimits.MaxIdLength)) throw new BcfExportException(BcfCodes.IdInvalid, "issue id must be 1.." + BcfLimits.MaxIdLength + " printable characters");
        if (!Enum.IsDefined(issue.Severity)) throw new BcfExportException(BcfCodes.IssueInvalid, "undefined severity for " + issue.Id);
        Text(issue.Title, BcfLimits.MaxTitleLength, multiline: false, "title of " + issue.Id);
        if (issue.Description is not null) Text(issue.Description, BcfLimits.MaxDescriptionLength, multiline: true, "description of " + issue.Id, allowBlank: true);
        if (issue.RuleId is not null) Text(issue.RuleId, BcfLimits.MaxIdLength, multiline: false, "rule id of " + issue.Id);
        if (issue.Camera is not null) ValidateCamera(issue.Camera, issue.Id);
        var elements = issue.ElementIds ?? Array.Empty<string>();
        if (elements.Count > BcfLimits.MaxElementsPerIssue) throw new BcfExportException(BcfCodes.ElementsInvalid, issue.Id + " has more than " + BcfLimits.MaxElementsPerIssue + " elements");
        var components = new SortedDictionary<string, Component>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (!IsLine(element, BcfLimits.MaxElementIdLength)) throw new BcfExportException(BcfCodes.ElementsInvalid, "element id of " + issue.Id);
            var component = Classify(element.Trim());
            components.TryAdd(component.Key, component);
        }
        return components.Values.ToList();
    }

    private static Component Classify(string element)
    {
        if (element.Length == 36 && Guid.TryParseExact(element, "D", out var guid)) return new Component(ToIfcGuid(guid), null);
        if (element.Length == 22 && element[0] is >= '0' and <= '3' && element.All(c => IfcAlphabet.Contains(c, StringComparison.Ordinal))) return new Component(element, null);
        return new Component(null, element);
    }

    private static void ValidateCamera(BcfCamera camera, string issueId)
    {
        static bool Finite(BcfVector v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z)
            && Math.Abs(v.X) <= BcfLimits.MaxCoordinate && Math.Abs(v.Y) <= BcfLimits.MaxCoordinate && Math.Abs(v.Z) <= BcfLimits.MaxCoordinate;
        static bool NonZero(BcfVector v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z > 1e-18;
        if (!Enum.IsDefined(camera.Kind)) throw new BcfExportException(BcfCodes.CameraInvalid, "undefined camera kind for " + issueId);
        if (!Finite(camera.ViewPoint) || !Finite(camera.Direction) || !Finite(camera.UpVector)) throw new BcfExportException(BcfCodes.CameraInvalid, "non-finite or out-of-range camera vector for " + issueId);
        if (!NonZero(camera.Direction) || !NonZero(camera.UpVector)) throw new BcfExportException(BcfCodes.CameraInvalid, "zero camera direction or up vector for " + issueId);
        if (!double.IsFinite(camera.AspectRatio) || camera.AspectRatio <= 0 || camera.AspectRatio > 1000) throw new BcfExportException(BcfCodes.CameraInvalid, "aspect ratio for " + issueId);
        var valid = camera.Kind == BcfCameraKind.Perspective
            ? double.IsFinite(camera.Value) && camera.Value >= 45 && camera.Value <= 60
            : double.IsFinite(camera.Value) && camera.Value > 0 && camera.Value <= BcfLimits.MaxCoordinate;
        if (!valid) throw new BcfExportException(BcfCodes.CameraInvalid, (camera.Kind == BcfCameraKind.Perspective ? "field of view must be 45..60" : "view-to-world scale must be positive") + " for " + issueId);
    }

    private static bool IsLine(string? value, int maxLength) =>
        value is not null && value.Length <= maxLength && !string.IsNullOrWhiteSpace(value) && IsXmlText(value, multiline: false);

    private static void Text(string? value, int maxLength, bool multiline, string what, bool allowBlank = false)
    {
        if (value is null || (!allowBlank && string.IsNullOrWhiteSpace(value))) throw new BcfExportException(BcfCodes.TextInvalid, what + " is required");
        if (value.Length > maxLength) throw new BcfExportException(BcfCodes.TextTooLong, what + " exceeds " + maxLength + " characters");
        if (!IsXmlText(value, multiline)) throw new BcfExportException(BcfCodes.TextInvalid, what + " contains characters not allowed in XML" + (multiline ? "" : " or line breaks"));
    }

    private static bool IsXmlText(string value, bool multiline)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { i++; continue; }
                return false;
            }
            if (!XmlConvert.IsXmlChar(c)) return false;
            if (!multiline && char.IsControl(c)) return false;
        }
        return true;
    }
}
