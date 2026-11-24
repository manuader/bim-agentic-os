using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bim.Core;
namespace Bim.Cad;

// P6-39: the `cad-brief` document (interfaces/cli/CadCommand.cs) built in one public place, so the CLI and the tests that
// pin its bytes go through the same entry point. Create runs BriefExtract on a read DXF drawing or PDF page and holds
// {"kind":"cad-brief","version":1,"persisted":false,"source":{"file","format","bytes","sha256","page","scale",
// "metres_per_unit"},"brief","rooms","uncertainties","issues"}; ToUtf8Bytes writes it compact (UTF-8, no BOM, enums as
// strings). scale is known-dimension (a calibration), metres-per-unit (options.MetresPerUnit), insunits (the drawing's
// $INSUNITS) or none. Reading the input (reader options, page checks) stays with the caller. Bytes are those the CLI
// wrote before P6-39.
public sealed class CadBriefDocument
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    readonly object document;

    CadBriefDocument(BriefDraft result, string scale, object document)
    {
        Result = result;
        Scale = scale;
        this.document = document;
    }

    // The BriefExtract draft the document holds.
    public BriefDraft Result { get; }

    public string Scale { get; }

    // Exactly one of drawing and pdf is given; a PDF needs its page number. ArgumentException (including
    // ArgumentNullException) for a bad call and BriefExtract's own refusals pass through.
    public static CadBriefDocument Create(string sourceFile, byte[] bytes, DxfDrawing? drawing, PdfDocument? pdf, int? page, PdfCalibration? calibration, BriefExtractOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(options);
        if ((drawing is null) == (pdf is null)) throw new ArgumentException("give exactly one of a DXF drawing and a PDF document");
        if (pdf is not null && page is null) throw new ArgumentException("a PDF brief needs a page number", nameof(page));
        var format = drawing is not null ? "dxf" : "pdf";
        var result = drawing is not null ? BriefExtract.FromDxf(drawing, options) : BriefExtract.FromPdf(pdf!, page!.Value, calibration, options);
        var scale = calibration is not null ? "known-dimension"
            : options.MetresPerUnit is not null ? "metres-per-unit"
            : drawing?.MetresPerUnit is not null ? "insunits" : "none";
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
        return new CadBriefDocument(result, scale, document);
    }

    // Non-finite numbers cannot be written as JSON: ArgumentException, nothing returned.
    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(document, Json);
}
