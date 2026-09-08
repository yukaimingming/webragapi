using Microsoft.Extensions.DataIngestion;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Rendering.Skia;
using UglyToad.PdfPig.Rendering.Skia.Helpers;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// PDF 解析器：基于 PdfPig 按页提取文本块，每一页作为一个 Section（自动带页码）。
/// 使用 Docstrum 版面分析把页面上的字符聚合成接近阅读顺序的文本块。
/// 当某一页几乎没有文字层（扫描件/图片型 PDF）时，对该页做 OCR，其它有文字层的页面保持原解析。
/// </summary>
internal sealed class PdfPigReader(ILogger<PdfPigReader>? logger = null) : IngestionDocumentReader
{
    // 少于此可见字符数视为“没有可用文字层”，才走 OCR，避免改动正常文本 PDF
    private const int MinUsableChars = 40;
    // 整页渲染缩放：1=72dpi，3≈216dpi，兼顾中文 OCR 与速度
    private const float OcrRenderScale = 3f;

    public override Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;

        // 有文字层的 PDF 仍按原来的打开方式解析，避免影响正常文档
        using var pdf = PdfDocument.Open(buffer);
        var document = new IngestionDocument(identifier);
        var pagesNeedingOcr = new List<int>();

        foreach (var page in pdf.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = GetPageSection(page);
            if (!HasUsableText(section))
            {
                var fromImages = TryOcrEmbeddedImages(page, identifier);
                if (TesseractOcr.CountVisibleChars(fromImages) >= MinUsableChars)
                {
                    ApplyOcr(section, fromImages, identifier, page.Number);
                }
                else
                {
                    pagesNeedingOcr.Add(page.Number);
                }
            }
            document.Sections.Add(section);
        }

        if (pagesNeedingOcr.Count > 0)
        {
            buffer.Position = 0;
            using var renderPdf = PdfDocument.Open(buffer, SkiaRenderingParsingOptions.Instance);
            renderPdf.AddSkiaPageFactory();
            foreach (var pageNumber in pagesNeedingOcr)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ocrText = TryOcrRenderedPage(renderPdf, pageNumber, identifier);
                if (TesseractOcr.CountVisibleChars(ocrText) < MinUsableChars)
                    continue;
                var section = document.Sections[pageNumber - 1];
                ApplyOcr(section, ocrText, identifier, pageNumber);
            }
        }

        return Task.FromResult(document);
    }

    private void ApplyOcr(IngestionDocumentSection section, string ocrText, string identifier, int pageNumber)
    {
        section.Elements.Clear();
        foreach (var para in TesseractOcr.SplitParagraphs(ocrText))
            section.Elements.Add(new IngestionDocumentParagraph(para) { Text = para });
        logger?.LogInformation("PDF '{Id}' 第 {Page} 页无文字层，已用 OCR 识别 {Chars} 个字符。",
            identifier, pageNumber, TesseractOcr.CountVisibleChars(ocrText));
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

    private static bool HasUsableText(IngestionDocumentSection section)
    {
        int n = 0;
        foreach (var el in section.Elements)
            n += TesseractOcr.CountVisibleChars(el.Text);
        return n >= MinUsableChars;
    }

    /// <summary>OCR 页面内嵌的大幅图像（扫描件常见）。</summary>
    private string TryOcrEmbeddedImages(Page page, string identifier)
    {
        var parts = new List<string>();
        try
        {
            foreach (var image in page.GetImages())
            {
                if (image.BoundingBox.Width * image.BoundingBox.Height < 80 * 80)
                    continue;

                try
                {
                    using var bitmap = image.GetSKBitmap();
                    if (bitmap is null || bitmap.Width < 40 || bitmap.Height < 40)
                        continue;
                    using var skImage = SKImage.FromBitmap(bitmap);
                    using var data = skImage.Encode(SKEncodedImageFormat.Png, 90);
                    var text = TesseractOcr.Recognize(data.ToArray(), logger);
                    if (TesseractOcr.CountVisibleChars(text) > 0)
                        parts.Add(text);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "PDF '{Id}' 第 {Page} 页内嵌图 OCR 失败，将尝试整页渲染。", identifier, page.Number);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "PDF '{Id}' 第 {Page} 页提取内嵌图失败。", identifier, page.Number);
        }

        return string.Join("\n\n", parts);
    }

    private string TryOcrRenderedPage(PdfDocument pdf, int pageNumber, string identifier)
    {
        try
        {
            using var rendered = pdf.GetPageAsSKBitmap(pageNumber, OcrRenderScale, SKColors.White);
            if (rendered is null)
                return string.Empty;
            using var skImage = SKImage.FromBitmap(rendered);
            using var data = skImage.Encode(SKEncodedImageFormat.Png, 90);
            return TesseractOcr.Recognize(data.ToArray(), logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PDF '{Id}' 第 {Page} 页整页渲染 OCR 失败。", identifier, pageNumber);
            return string.Empty;
        }
    }
}
