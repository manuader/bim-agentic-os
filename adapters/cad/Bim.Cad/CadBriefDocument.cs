using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bim.Core;
namespace Bim.Cad;

// P6-39: the `cad-brief` document (interfaces/cli/CadCommand.cs) built in one public place, so the CLI and the tests that
// pin its bytes go through the same entry point. FromDxf / FromPdf (P6-41: one factory per input kind, replacing the
// P6-39 Create with its exactly-one-of guard) run BriefExtract on a read DXF drawing or PDF page and hold
// {"kind":"cad-brief","version":1,"persisted":false,"source":{"file","format","bytes","sha256","page","scale",
// "metres_per_unit"},"brief","rooms","uncertainties","issues"}; ToUtf8Bytes writes it compact (UTF-8, no BOM, enums as
// strings). scale is known-dimension (a calibration, PDF only), metres-per-unit (options.MetresPerUnit), insunits (the
// drawing's $INSUNITS, DXF only) or none. The builder owns the input hash (P6-41: the CLI no longer hashes the input for
// cad-brief, so it is hashed once). Reading the input (reader options, page checks) stays with the caller, because
// CadCommand reads it once for cad-draft and cad-brief alike. Bytes are those the CLI wrote before P6-39.
public sealed class CadBriefDocument
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    readonly object document;

    CadBriefDocument(BriefDraft result, object document)
    {
        Result = result;
        this.document = document;
    }

    // The BriefExtract draft the document holds.
    public BriefDraft Result { get; }

    // A DXF brief (page null). ArgumentNullException for a missing argument; BriefExtract's own refusals pass through.
    public static CadBriefDocument FromDxf(string sourceFile, byte[] bytes, DxfDrawing drawing, BriefExtractOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(options);
        var result = BriefExtract.FromDxf(drawing, options);
        var scale = options.MetresPerUnit is not null ? "metres-per-unit" : drawing.MetresPerUnit is not null ? "insunits" : "none";
        return Build(sourceFile, bytes, "dxf", null, scale, result);
    }

    // A brief of one PDF page. ArgumentNullException for a missing argument; BriefExtract's own refusals (a missing page,
    // a bad calibration) pass through.
    public static CadBriefDocument FromPdf(string sourceFile, byte[] bytes, PdfDocument pdf, int page, PdfCalibration? calibration, BriefExtractOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(options);
        var result = BriefExtract.FromPdf(pdf, page, calibration, options);
        var scale = calibration is not null ? "known-dimension" : options.MetresPerUnit is not null ? "metres-per-unit" : "none";
        return Build(sourceFile, bytes, "pdf", page, scale, result);
    }

    static CadBriefDocument Build(string sourceFile, byte[] bytes, string format, int? page, string scale, BriefDraft result)
    {
        var document = new
        {
            kind = "cad-brief",
            version = 1,
            persisted = false,
            source = new { file = sourceFile, format, bytes = bytes.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(bytes)), page, scale, metres_per_unit = result.MetresPerUnit },
            brief = JsonNode.Parse(BriefSerializer.Serialize(result.Brief)),
            rooms = result.Rooms,
            uncertainties = result.Uncertainties,
            issues = result.Issues,
        };
        return new CadBriefDocument(result, document);
    }

    // Non-finite numbers cannot be written as JSON: ArgumentException, nothing returned.
    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(document, Json);
}
