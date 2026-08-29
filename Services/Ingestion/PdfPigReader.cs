using Microsoft.Extensions.DataIngestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// PDF 解析器：基于 PdfPig 按页提取文本块，每一页作为一个 Section（自动带页码）。
/// 使用 Docstrum 版面分析把页面上的字符聚合成接近阅读顺序的文本块。
/// </summary>
internal sealed class PdfPigReader : IngestionDocumentReader
{
    public override Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        using var pdf = PdfDocument.Open(source);
        var document = new IngestionDocument(identifier);
        foreach (var page in pdf.GetPages())
        {
            document.Sections.Add(GetPageSection(page));
        }
        return Task.FromResult(document);
    }

    private static IngestionDocumentSection GetPageSection(Page pdfPage)
    {
        var section = new IngestionDocumentSection
        {
            PageNumber = pdfPage.Number,
        };

        var letters = pdfPage.Letters;
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters);

        foreach (var textBlock in DocstrumBoundingBoxes.Instance.GetBlocks(words))
        {
            section.Elements.Add(new IngestionDocumentParagraph(textBlock.Text)
            {
                Text = textBlock.Text
            });
        }

        return section;
    }
}
