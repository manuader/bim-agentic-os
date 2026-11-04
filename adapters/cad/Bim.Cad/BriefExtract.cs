using Bim.Core;
namespace Bim.Cad;

// P6-05: room labels and area texts of a DXF drawing or PDF page to a draft project brief (see BriefDraft).
public static class BriefExtract
{
    public static BriefDraft FromDxf(DxfDrawing drawing, BriefExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        return Empty(drawing.SourceFile, "dxf");
    }

    public static BriefDraft FromPdf(PdfDocument document, int pageNumber, PdfCalibration? calibration = null, BriefExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Empty(document.SourceFile, "pdf");
    }

    public static BriefTextReading ReadText(string? text, int maxLabelLength = 64, int maxLabelWords = 6) => new(null, null, null);

    private static BriefDraft Empty(string source, string format)
    {
        var brief = new ProjectBrief(ProjectBrief.CurrentVersion, "Brief draft", new BriefConstraints(), Array.Empty<BriefRoom>(), Array.Empty<BriefAdjacency>());
        return new BriefDraft(source, format, null, brief, Array.Empty<BriefRoomProposal>(), Array.Empty<BriefExtractUncertainty>(), BriefValidator.Validate(brief));
    }
}
