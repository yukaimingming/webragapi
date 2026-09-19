using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DataIngestion;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 导入前文本清洗：去掉页码、跨页重复水印/页眉页脚短句、多余空白。
/// PDF 页眉页脚还会在 PdfPigReader 里按坐标带再剥一层。
/// </summary>
internal static class DocumentCleaner
{
    // 整行页码：第 N 页、-12-、1/20、Page 3，以及中文文献常见的单独一行纯数字（1～3 位，避免误删 2024 这类年份）
    private static readonly Regex PageNumberLine = new(
        @"^(?:第\s*\d+\s*页(?:\s*[\/共]\s*\d+\s*页)?|-+\s*\d+\s*-+|—\s*\d+\s*—|\d+\s*\/\s*\d+|Page\s+\d+(?:\s*of\s*\d+)?|\d{1,3}|\[\d{1,3}\]|〔\d{1,3}〕)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>就地清洗文档各节元素；空段丢弃。</summary>
    public static void Clean(IngestionDocument document)
    {
        var repeating = FindRepeatingLines(document);
        foreach (var section in document.Sections)
        {
            var kept = new List<IngestionDocumentElement>();
            foreach (var el in section.Elements.ToList())
            {
                var raw = el.Text;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var cleaned = CleanText(raw, repeating);
                if (string.IsNullOrWhiteSpace(cleaned))
                    continue;
                SetText(el, cleaned);
                kept.Add(el);
            }
            section.Elements.Clear();
            foreach (var el in kept)
                section.Elements.Add(el);
        }
    }

    public static string CleanText(string text, ISet<string>? repeatingLines = null)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = CollapseSpace(rawLine);
            if (line.Length == 0)
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                    sb.Append('\n');
                continue;
            }
            if (PageNumberLine.IsMatch(line))
                continue;
            if (repeatingLines is not null && repeatingLines.Contains(NormalizeKey(line)))
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 跨页重复的短句视为页眉/页脚/水印。
    /// 仅对带真实 PageNumber 的多页文档（PDF 一页一节）按「页」计频率；
    /// md/docx 的 section 不是页，不做这项统计，避免把重复小标题当水印。
    /// </summary>
    private static HashSet<string> FindRepeatingLines(IngestionDocument document)
    {
        var paged = document.Sections.Where(s => s.PageNumber is > 0).ToList();
        if (paged.Count < 3)
            return [];

        var perPage = new List<HashSet<string>>();
        foreach (var section in paged)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in section.Elements)
            {
                if (string.IsNullOrWhiteSpace(el.Text))
                    continue;
                foreach (var line in el.Text.Replace("\r\n", "\n").Split('\n'))
                {
                    var key = NormalizeKey(line);
                    if (key.Length is >= 2 and <= 40)
                        keys.Add(key);
                }
            }
            if (keys.Count > 0)
                perPage.Add(keys);
        }

        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var page in perPage)
        {
            foreach (var k in page)
                freq[k] = freq.TryGetValue(k, out var n) ? n + 1 : 1;
        }

        int threshold = Math.Max(3, (int)Math.Ceiling(perPage.Count * 0.4));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (k, n) in freq)
        {
            if (n >= threshold && !LooksLikeContent(k))
                result.Add(k);
        }
        return result;
    }

    private static bool LooksLikeContent(string key)
        => key.Length > 24 && (key.Contains("治疗") || key.Contains("诊断") || key.Contains("患者"));

    private static string NormalizeKey(string line)
        => CollapseSpace(line).ToLowerInvariant();

    private static string CollapseSpace(string s)
        => Regex.Replace(s.Trim(), @"\s+", " ");

    private static void SetText(IngestionDocumentElement el, string text)
    {
        el.Text = text;
        switch (el)
        {
            case IngestionDocumentParagraph p:
                p.Text = text;
                break;
            case IngestionDocumentHeader h:
                h.Text = text;
                break;
        }
    }
}
