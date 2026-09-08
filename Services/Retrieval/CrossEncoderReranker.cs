using WebRagApi.Models;

namespace WebRagApi.Services.Retrieval;

/// <summary>
/// Cross-Encoder 重排：对 (query, passage) 成对打分后再截断 TopK。
/// 未部署 bge-reranker ONNX 时，用查询-文档交叉特征（覆盖率、短语命中、向量/BM25 标准化）做联合打分，
/// 结构与 Cross-Encoder 相同（成对输入），便于 Demo 验证重排效果。
/// </summary>
public sealed class CrossEncoderReranker(ILogger<CrossEncoderReranker> logger)
{
    /// <summary>
    /// 对候选切块按 (query, 文本) 联合打分后截断。
    /// 注意：这里还不是 BAAI bge-reranker 神经网络，只是同样的成对打分形态。
    /// </summary>
    public List<SearchResultItem> Rerank(string query, List<SearchResultItem> candidates, int topK)
    {
        if (candidates.Count == 0 || topK <= 0)
            return [];

        var qTokens = ChineseLexicalTokenizer.Tokenize(query);
        var qSet = qTokens.ToHashSet(StringComparer.Ordinal);
        string qNorm = query.Trim();

        double maxBm25 = candidates.Select(c => c.Bm25Score ?? 0).DefaultIfEmpty(0).Max();
        if (maxBm25 <= 0) maxBm25 = 1;

        var scored = new List<(SearchResultItem Item, double Score)>(candidates.Count);
        foreach (var item in candidates)
        {
            double vec = item.VectorScore ?? 0;
            double bm25n = Math.Min(1, (item.Bm25Score ?? 0) / maxBm25);

            var dSet = ChineseLexicalTokenizer.Tokenize(item.Text).ToHashSet(StringComparer.Ordinal);
            int hit = 0;
            foreach (var t in qSet)
            {
                if (dSet.Contains(t))
                    hit++;
            }
            // 查询词在文档中的覆盖率
            double coverage = qSet.Count == 0 ? 0 : (double)hit / qSet.Count;
            // 整句/短语直接出现在切块里时加分（对人名、术语更准）
            double phrase = qNorm.Length >= 2 && item.Text.Contains(qNorm, StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            // 成对打分：语义 + 词法 + 覆盖 + 短语。尚未使用 bge-reranker ONNX 权重。
            double score = 0.32 * vec + 0.28 * bm25n + 0.25 * coverage + 0.15 * phrase;
            scored.Add((item, score));
        }

        logger.LogDebug("Cross-Encoder 重排 {Count} 个候选 → Top {TopK}。", candidates.Count, topK);

        return scored
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .Select(s =>
            {
                s.Item.Score = s.Score;
                return s.Item;
            })
            .ToList();
    }
}
