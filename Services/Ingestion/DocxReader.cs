using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using System.Text;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 读取 .docx（Word 2007+ OpenXML 格式），直接构建 IngestionDocument。
/// 不经过 markdown 文本解析：超大型表格会让 MarkdownReader 触发嵌套深度限制而失败。
/// 切块器无法拆分超过 MaxTokensPerChunk 的单个元素，所以这里按 token 数把超大段落/表格行
/// 拆小（每个元素 ≤ MaxTokensPerElement），保证导入必定成功。
/// </summary>
internal sealed class DocxReader : IngestionDocumentReader
{
    // 与 DataIngestor 的 cl100k 分词保持一致
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-3.5-turbo");

    // 单个元素的最大 token 数，需小于 DataIngestor 的 MaxTokensPerChunk(1024)
    internal const int MaxTokensPerElement = 700;

    // 每个表格元素最多包含的行数
    private const int MaxRowsPerTableElement = 10;

    public override Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        using var doc = WordprocessingDocument.Open(source, isEditable: false);
        var body = doc.MainDocumentPart?.Document.Body
            ?? throw new InvalidOperationException($"Word 文档 '{identifier}' 没有正文内容。");

        var document = new IngestionDocument(identifier);
        var section = new IngestionDocumentSection();
        document.Sections.Add(section);

        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph p:
                    AppendParagraph(section, p);
                    break;
                case Table table:
                    AppendTable(section, table);
                    break;
            }
        }

        return Task.FromResult(document);
    }

    private static void AppendParagraph(IngestionDocumentSection section, Paragraph p)
    {
        var text = p.InnerText?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        bool isHeading = styleId is not null && styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(styleId["Heading".Length..], out var n) && n is >= 1 and <= 6;

        if (isHeading && CountTokens(text) <= MaxTokensPerElement)
        {
            section.Elements.Add(new IngestionDocumentHeader(text) { Level = int.Parse(styleId!["Heading".Length..]) });
        }
        else if (CountTokens(text) > MaxTokensPerElement)
        {
            // 超长段落按 token 边界拆成多个元素，否则切块器无法容纳
            foreach (var piece in SplitByTokens(text, MaxTokensPerElement))
                section.Elements.Add(new IngestionDocumentParagraph(piece));
        }
        else
        {
            section.Elements.Add(new IngestionDocumentParagraph(text));
        }
    }

    private static void AppendTable(IngestionDocumentSection section, Table table)
    {
        var rows = table.Elements<TableRow>()
            .Select(r => r.Elements<TableCell>().Select(c => c.InnerText?.Trim() ?? string.Empty).ToList())
            .Where(cells => cells.Any(s => s.Length > 0))
            .ToList();

        if (rows.Count == 0)
            return;

        int columnCount = rows.Max(r => r.Count);
        var group = new List<List<string>>();
        int groupTokens = 0;

        foreach (var row in rows)
        {
            int rowTokens = CountTokens(string.Join(" ", row)) + 8; // 8 为表格 markdown 管道符等开销余量

            if (rowTokens > MaxTokensPerElement)
            {
                // 单行就超限（合并单元格携带大量文本）：整组先落盘，再把这一行拍平成独立段落
                FlushTable(section, group, columnCount);
                group.Clear();
                foreach (var cell in row.Where(s => s.Length > 0))
                {
                    if (CountTokens(cell) > MaxTokensPerElement)
                    {
                        foreach (var piece in SplitByTokens(cell, MaxTokensPerElement))
                            section.Elements.Add(new IngestionDocumentParagraph(piece));
                    }
                    else
                    {
                        section.Elements.Add(new IngestionDocumentParagraph(cell));
                    }
                }
                continue;
            }

            if (group.Count >= MaxRowsPerTableElement || groupTokens + rowTokens > MaxTokensPerElement)
            {
                FlushTable(section, group, columnCount);
                group.Clear();
                groupTokens = 0;
            }

            group.Add(row);
            groupTokens += rowTokens;
        }

        FlushTable(section, group, columnCount);
    }

    private static void FlushTable(IngestionDocumentSection section, List<List<string>> group, int columnCount)
    {
        if (group.Count == 0)
            return;

        bool hasHeader = group.Count > 1;
        var cells = new IngestionDocumentElement[group.Count, columnCount];
        for (int r = 0; r < group.Count; r++)
            for (int c = 0; c < columnCount; c++)
            {
                // 元素的 markdown 不能为 null/空白（会抛 ArgumentNullException），空单元格用占位符
                var cellText = c < group[r].Count ? group[r][c] : string.Empty;
                cells[r, c] = new IngestionDocumentParagraph(
                    string.IsNullOrWhiteSpace(cellText) ? "(空)" : cellText);
            }

        section.Elements.Add(new IngestionDocumentTable(BuildTableMarkdown(group, columnCount, hasHeader), cells));
    }

    private static string BuildTableMarkdown(List<List<string>> rows, int columnCount, bool hasHeader)
    {
        var sb = new StringBuilder();
        string Row(List<string> cells)
        {
            var padded = Enumerable.Range(0, columnCount).Select(i => i < cells.Count ? cells[i] : string.Empty);
            return "| " + string.Join(" | ", padded) + " |";
        }

        sb.AppendLine(Row(rows[0]));
        sb.Append('|');
        for (int i = 0; i < columnCount; i++)
            sb.Append(" --- |");
        sb.AppendLine();

        int dataStart = hasHeader ? 1 : 0;
        // 没有表头时第一行也作为数据行再输出一次，保证 markdown 表格语义完整
        for (int i = dataStart; i < rows.Count; i++)
            sb.AppendLine(Row(rows[i]));

        return sb.ToString();
    }

    internal static int CountTokens(string text)
        => Tokenizer.CountTokens(text, considerPreTokenization: true, considerNormalization: true);

    internal static IEnumerable<string> SplitByTokens(string text, int maxTokens)
    {
        var ids = Tokenizer.EncodeToIds(text, int.MaxValue, out _, out _, true, true);
        for (int i = 0; i < ids.Count; i += maxTokens)
        {
            var slice = ids.Skip(i).Take(maxTokens).ToList();
            if (slice.Count > 0)
                yield return Tokenizer.Decode(slice).Trim();
        }
    }
}
