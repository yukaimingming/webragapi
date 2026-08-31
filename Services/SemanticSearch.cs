using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using WebRagApi.Models;
using WebRagApi.Services.Ingestion;

namespace WebRagApi.Services;

/// <summary>
/// 语义检索服务：把查询文本向量化后在 Qdrant 中做余弦相似度检索。
/// 每次检索都直接查询 Qdrant 实时数据，新上传的文档导入完成后无需重启即可被检索到。
/// </summary>
public class SemanticSearch(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    QdrantClient qdrantClient)
{
    /// <summary>带相似度得分的向量检索（供 /api/knowledge/search 与 chat 引用来源使用）</summary>
    public async Task<List<SearchResultItem>> SearchWithScoreAsync(
        string text, string? documentIdFilter, int maxResults, CancellationToken cancellationToken = default)
    {
        // 1. 查询文本向量化
        var embedding = await embeddingGenerator.GenerateAsync(text, cancellationToken: cancellationToken);
        var vector = embedding.Vector.ToArray();

        // 2. 可选：限定只在某个文档内检索
        var filter = documentIdFilter is { Length: > 0 } ? DataIngestor.DocumentFilter(documentIdFilter) : null;

        // 3. Qdrant 余弦相似度检索（Score 越大越相关）
        IReadOnlyList<ScoredPoint> points;
        try
        {
            points = await qdrantClient.SearchAsync(
                QdrantChunkWriter.CollectionName,
                vector,
                filter: filter,
                limit: (ulong)maxResults,
                cancellationToken: cancellationToken);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            // 集合还不存在（空库），视为没有命中
            return [];
        }

        return points.Select(p => new SearchResultItem
        {
            DocumentId = GetPayload(p, "documentid") ?? "(未知)",
            FileName = GetPayload(p, "filename") ?? GetPayload(p, "documentid") ?? "(未知)",
            Score = p.Score,
            Text = GetPayload(p, "content") ?? string.Empty,
            Context = string.IsNullOrEmpty(GetPayload(p, "context")) ? null : GetPayload(p, "context"),
            PageNumber = p.Payload.TryGetValue("page_number", out var pv) && pv.HasIntegerValue ? (int)pv.IntegerValue : null,
        }).ToList();
    }

    private static string? GetPayload(ScoredPoint point, string key)
        => point.Payload.TryGetValue(key, out var v) && v.HasStringValue && v.StringValue.Length > 0 ? v.StringValue : null;
}
