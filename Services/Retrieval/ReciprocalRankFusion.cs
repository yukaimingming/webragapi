using WebRagApi.Models;

namespace WebRagApi.Services.Retrieval;

/// <summary>
/// Reciprocal Rank Fusion：按名次融合多路检索，不依赖各路分数是否同量纲。
/// score(d) = Σ 1 / (k + rank_i(d))，k 常用 60。
/// </summary>
internal static class ReciprocalRankFusion
{
    /// <param name="k">RRF 平滑常数，越大各名次分差越小，常用 60</param>
    public static List<SearchResultItem> Fuse(IReadOnlyList<IReadOnlyList<SearchResultItem>> rankedLists, int k, int topK)
    {
        var scores = new Dictionary<string, (SearchResultItem Item, double Score)>(StringComparer.Ordinal);
        foreach (var list in rankedLists)
        {
            for (int rank = 0; rank < list.Count; rank++)
            {
                var item = list[rank];
                var key = item.ChunkId ?? $"{item.DocumentId}\n{item.Text}";
                // rank 从 0 计，公式用 1-based：1/(k+1)、1/(k+2)…
                double add = 1.0 / (k + rank + 1);
                if (scores.TryGetValue(key, out var existing))
                {
                    scores[key] = (Merge(existing.Item, item), existing.Score + add);
                }
                else
                {
                    scores[key] = (Clone(item), add);
                }
            }
        }

        return scores.Values
            .OrderByDescending(v => v.Score)
            .Take(topK)
            .Select(v =>
            {
                v.Item.Score = v.Score;
                return v.Item;
            })
            .ToList();
    }

    private static SearchResultItem Clone(SearchResultItem item) => new()
    {
        ChunkId = item.ChunkId,
        DocumentId = item.DocumentId,
        FileName = item.FileName,
        Score = item.Score,
        VectorScore = item.VectorScore,
        Bm25Score = item.Bm25Score,
        Text = item.Text,
        Context = item.Context,
        PageNumber = item.PageNumber,
    };

    /// <summary>同一切块出现在多路结果里时，把向量分和 BM25 分合并到同一条上。</summary>
    private static SearchResultItem Merge(SearchResultItem a, SearchResultItem b)
    {
        a.VectorScore ??= b.VectorScore;
        a.Bm25Score ??= b.Bm25Score;
        a.ChunkId ??= b.ChunkId;
        return a;
    }
}
