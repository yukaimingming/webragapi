using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 父子切块层次树（不上 Neo4j）：
/// 父块 = 同一标题下的完整小节；子块 = 检索单元（约 280 token，带重叠）。
/// 只把子块写入向量库，payload 带 parentid / parenttext / entities，命中后可回挂父段。
/// </summary>
internal sealed class ParentChildChunker(Tokenizer tokenizer) : IngestionChunker<string>
{
    internal const int ParentMaxTokens = 800;
    internal const int ChildMaxTokens = 280;
    internal const int ChildOverlapTokens = 40;

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IngestionDocument document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DocumentCleaner.Clean(document);
        foreach (var chunk in BuildChunks(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
        await Task.CompletedTask;
    }

    private List<IngestionChunk<string>> BuildChunks(IngestionDocument document)
    {
        var result = new List<IngestionChunk<string>>();
        foreach (var section in document.Sections)
        {
            // PDF 的 Section.PageNumber 从 1 起；md/docx 没有物理页，保持 null，切勿写成 0
            int? page = section.PageNumber;
            string heading = "";
            var buffer = new List<string>();
            int tokens = 0;

            void Flush()
            {
                if (buffer.Count == 0)
                    return;
                var parentText = string.Join("\n", buffer);
                var parentId = Guid.NewGuid().ToString("N");
                var title = string.IsNullOrWhiteSpace(heading) ? parentText.Split('\n')[0] : heading;
                if (title.Length > 80)
                    title = title[..80];
                foreach (var child in SplitChildren(parentText))
                {
                    var entities = MedicalEntityTagger.Tag(child + "\n" + title);
                    var chunk = new IngestionChunk<string>(child, document, title);
                    // 仅真实页码入库，避免问答出现「第 0 页」
                    if (page is > 0)
                        chunk.Metadata["page"] = page.Value;
                    chunk.Metadata["parentid"] = parentId;
                    chunk.Metadata["parenttext"] = parentText.Length > 1800 ? parentText[..1800] : parentText;
                    chunk.Metadata["chunkrole"] = "child";
                    chunk.Metadata["entities"] = string.Join("|", entities);
                    result.Add(chunk);
                }
                buffer.Clear();
                tokens = 0;
            }

            foreach (var el in section.Elements)
            {
                if (el is IngestionDocumentHeader header)
                {
                    Flush();
                    heading = header.Text ?? "";
                    if (!string.IsNullOrWhiteSpace(heading))
                    {
                        buffer.Add(heading);
                        tokens = CountTokens(heading);
                    }
                    continue;
                }

                var text = el.Text;
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                int t = CountTokens(text);
                if (tokens + t > ParentMaxTokens && buffer.Count > 0)
                    Flush();
                buffer.Add(text);
                tokens += t;
            }
            Flush();
        }
        return result;
    }

    private IEnumerable<string> SplitChildren(string parentText)
    {
        var parts = parentText.Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            yield break;

        var buf = new List<string>();
        int tokens = 0;
        foreach (var part in parts)
        {
            int t = CountTokens(part);
            if (t > ChildMaxTokens)
            {
                if (buf.Count > 0)
                {
                    yield return string.Join("\n", buf);
                    buf.Clear();
                    tokens = 0;
                }
                foreach (var piece in SplitLong(part))
                    yield return piece;
                continue;
            }
            if (tokens + t > ChildMaxTokens && buf.Count > 0)
            {
                yield return string.Join("\n", buf);
                // 重叠：保留末尾若干行
                var overlap = new List<string>();
                int ot = 0;
                for (int i = buf.Count - 1; i >= 0 && ot < ChildOverlapTokens; i--)
                {
                    overlap.Insert(0, buf[i]);
                    ot += CountTokens(buf[i]);
                }
                buf.Clear();
                buf.AddRange(overlap);
                tokens = ot;
            }
            buf.Add(part);
            tokens += t;
        }
        if (buf.Count > 0)
            yield return string.Join("\n", buf);
    }

    private IEnumerable<string> SplitLong(string text)
    {
        var tokens = tokenizer.EncodeToIds(text);
        int step = Math.Max(1, ChildMaxTokens - ChildOverlapTokens);
        for (int i = 0; i < tokens.Count; i += step)
        {
            var slice = tokens.Skip(i).Take(ChildMaxTokens).ToList();
            var decoded = tokenizer.Decode(slice) ?? text;
            if (!string.IsNullOrWhiteSpace(decoded))
                yield return decoded.Trim();
            if (i + ChildMaxTokens >= tokens.Count)
                break;
        }
    }

    private int CountTokens(string text)
        => Math.Max(1, tokenizer.EncodeToIds(text).Count);
}
