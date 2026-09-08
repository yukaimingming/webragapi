using Microsoft.Extensions.DataIngestion;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 扫描件图片（png/jpg/jpeg/tif/tiff/bmp）：整图 Tesseract OCR 后按段落构建 IngestionDocument。
/// PDF 扫描件不走这里，由 PdfPigReader 在无文字层时 OCR。
/// </summary>
internal sealed class ImageOcrReader(ILogger<ImageOcrReader>? logger = null) : IngestionDocumentReader
{
    public override Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var text = TesseractOcr.Recognize(buffer.ToArray(), logger);

        var document = new IngestionDocument(identifier);
        var section = new IngestionDocumentSection { PageNumber = 1 };
        document.Sections.Add(section);

        foreach (var para in TesseractOcr.SplitParagraphs(text))
            section.Elements.Add(new IngestionDocumentParagraph(para) { Text = para });

        if (section.Elements.Count == 0)
            logger?.LogWarning("图片 '{Id}' OCR 未识别到可用文本。", identifier);

        return Task.FromResult(document);
    }
}
