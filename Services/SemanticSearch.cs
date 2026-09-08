using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using WebRagApi.Models;
using WebRagApi.Services.Ingestion;
using WebRagApi.Services.Retrieval;

namespace WebRagApi.Services;

/// <summary>
/// 检索服务：向量（Qdrant）+ BM25 + RRF 融合，可选 Cross-Encoder 重排。
/// 默认 hybrid+rerank；mode=vector 可退回原来的纯向量检索。
/// </summary>
public class SemanticSearch(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    QdrantClient qdrantClient,
    Bm25Index bm25Index,
    CrossEncoderReranker reranker,
    IOptions<RetrievalOptions> retrievalOptions)
{
    /// <summary>带得分的检索（供 /api/knowledge/search 与 chat 引用来源使用）</summary>
    public async Task<List<SearchResultItem>> SearchWithScoreAsync(
        string text, string? documentIdFilter, int maxResults, CancellationToken cancellationToken = default)
        => await SearchWithScoreAsync(text, documentIdFilter, maxResults, mode: null, cancellationToken);

    /// <param name="mode">vector / hybrid / rerank；空则用 Retrieval:DefaultMode</param>
    public async Task<List<SearchResultItem>> SearchWithScoreAsync(
        string text, string? documentIdFilter, int maxResults, string? mode, CancellationToken cancellationToken = default)
    {
        var options = retrievalOptions.Value;
        var resolved = NormalizeMode(mode, options.DefaultMode);
        // 融合/重排前每路多取一些，避免只看向量 TopK 把词法命中挤掉
        int fetch = Math.Clamp(maxResults * Math.Max(1, options.CandidateMultiplier), maxResults, 50);

        if (resolved == "vector")
            return await SearchVectorAsync(text, documentIdFilter, maxResults, cancellationToken);

        await bm25Index.EnsureReadyAsync(cancellationToken);
        var vectorTask = SearchVectorAsync(text, documentIdFilter, fetch, cancellationToken);
        var bm25 = bm25Index.Search(text, fetch, documentIdFilter);
        var vector = await vectorTask;

        // RRF 只看名次，向量余弦和 BM25 原始分不必对齐
        var fused = ReciprocalRankFusion.Fuse([vector, bm25], Math.Max(1, options.RrfK), fetch);
        if (resolved != "rerank")
            return fused.Take(maxResults).ToList();

        return reranker.Rerank(text, fused, maxResults);
    }

    /// <summary>Qdrant 稠密向量检索（bge-m3，余弦）。Score / VectorScore 均为相似度。</summary>
    private async Task<List<SearchResultItem>> SearchVectorAsync(
        string text, string? documentIdFilter, int maxResults, CancellationToken cancellationToken)
    {
        var embedding = await embeddingGenerator.GenerateAsync(text, cancellationToken: cancellationToken);
        var vector = embedding.Vector.ToArray();
        var filter = documentIdFilter is { Length: > 0 } ? DataIngestor.DocumentFilter(documentIdFilter) : null;

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
            return [];
        }

        return points.Select(p => new SearchResultItem
        {
            ChunkId = p.Id.HasUuid ? p.Id.Uuid : p.Id.Num.ToString(),
            DocumentId = GetPayload(p, "documentid") ?? "(未知)",
            FileName = GetPayload(p, "filename") ?? GetPayload(p, "documentid") ?? "(未知)",
            Score = p.Score,
            VectorScore = p.Score,
            Text = GetPayload(p, "content") ?? string.Empty,
            Context = string.IsNullOrEmpty(GetPayload(p, "context")) ? null : GetPayload(p, "context"),
            PageNumber = p.Payload.TryGetValue("page_number", out var pv) && pv.HasIntegerValue ? (int)pv.IntegerValue : null,
        }).ToList();
    }

    private static string NormalizeMode(string? mode, string defaultMode)
    {
        var m = (mode ?? defaultMode).Trim().ToLowerInvariant();
        return m is "vector" or "hybrid" or "rerank" ? m : "rerank";
    }

    private static string? GetPayload(ScoredPoint point, string key)
        => point.Payload.TryGetValue(key, out var v) && v.HasStringValue && v.StringValue.Length > 0 ? v.StringValue : null;
}
