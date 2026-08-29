using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Qdrant.Client.Grpc;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// Qdrant 切块写入器：继承 IngestionChunkWriter，把导入管道产出的切块
/// 向量化后直接写入 Qdrant（绕开 SK Qdrant 连接器的键类型限制，键用 Guid）。
/// 写入时顺带把文件元数据（文件名/大小/上传时间）补进 payload，
/// 这样列表和详情接口可以直接从 Qdrant 聚合，不需要额外的元数据库。
/// </summary>
public class QdrantChunkWriter(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    Qdrant.Client.QdrantClient qdrantClient,
    ILogger<QdrantChunkWriter> logger,
    IReadOnlyDictionary<string, (long Size, DateTimeOffset UploadedAt)>? fileMeta) : IngestionChunkWriter<string>
{
    /// <summary>Qdrant 集合名称</summary>
    public const string CollectionName = "webrag-chunks";

    /// <summary>向量维度：bge-m3 输出 1024 维</summary>
    public const int VectorDimensions = 1024;

    // 每批向量化/写入的切块数（避免 Ollama 单次请求过大）
    private const int BatchSize = 32;

    private readonly List<string> _seenDocumentIds = [];

    /// <summary>确保集合存在（1024 维、余弦距离）</summary>
    public async Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        if (!await qdrantClient.CollectionExistsAsync(CollectionName, ct))
        {
            await qdrantClient.CreateCollectionAsync(
                CollectionName,
                new VectorParams { Size = VectorDimensions, Distance = Distance.Cosine });
            logger.LogInformation("已创建 Qdrant 集合 '{Collection}'（{Dims} 维，余弦距离）。", CollectionName, VectorDimensions);
        }
    }

    /// <summary>管道回调：把切块批量向量化并写入 Qdrant</summary>
    public override async Task WriteAsync(IAsyncEnumerable<IngestionChunk<string>> chunks, CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(cancellationToken);

        var batch = new List<IngestionChunk<string>>(BatchSize);
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            var docId = chunk.Document.Identifier;
            if (!_seenDocumentIds.Contains(docId))
                _seenDocumentIds.Add(docId);

            batch.Add(chunk);
            if (batch.Count >= BatchSize)
            {
                await WriteBatchAsync(batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await WriteBatchAsync(batch, cancellationToken);
    }

    /// <summary>把一批切块向量化并 Upsert 到 Qdrant。失败时清理已写入的部分，保证不留半成品文档</summary>
    private async Task WriteBatchAsync(List<IngestionChunk<string>> batch, CancellationToken ct)
    {
        try
        {
            // 1. 批量向量化（BatchingEmbeddingGenerator 内部会再按 Ollama 安全的小批拆分）
            var contents = batch.Select(c => c.Content).ToList();
            var embeddings = await embeddingGenerator.GenerateAsync(contents, cancellationToken: ct);

            // 2. 组装 Qdrant 点并写入
            var points = new List<PointStruct>(batch.Count);
            for (int i = 0; i < batch.Count; i++)
            {
                var chunk = batch[i];
                var docId = chunk.Document.Identifier;
                (long Size, DateTimeOffset UploadedAt) meta = fileMeta is not null && fileMeta.TryGetValue(docId, out var m) ? m : (0L, DateTimeOffset.Now);

                var payload = new Dictionary<string, Value>
                {
                    ["documentid"] = new() { StringValue = docId },
                    ["content"] = new() { StringValue = chunk.Content },
                    ["context"] = new() { StringValue = chunk.Context ?? string.Empty },
                    ["filename"] = new() { StringValue = docId },
                    ["filesize"] = new() { DoubleValue = meta.Size },
                    ["uploadedat"] = new() { StringValue = meta.UploadedAt.ToString("O") },
                };

                // PDF 等按页解析的文档：从切块元数据里提取页码（若切块器有输出）
                if (TryGetPageNumber(chunk, out var pageNumber))
                    payload["page_number"] = new() { IntegerValue = pageNumber };

                points.Add(new PointStruct
                {
                    Id = Guid.NewGuid(),
                    Vectors = new Vectors { Vector = new Vector { Data = { embeddings[i].Vector.ToArray() } } },
                    Payload = { payload },
                });
            }

            await qdrantClient.UpsertAsync(CollectionName, points, cancellationToken: ct);
            logger.LogDebug("已写入 {Count} 个切块到 Qdrant。", points.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "切块写入 Qdrant 失败，清理本次已写入的文档，避免留下不完整数据。");
            foreach (var docId in _seenDocumentIds)
            {
                try { await qdrantClient.DeleteAsync(CollectionName, DataIngestor.DocumentFilter(docId)); }
                catch { /* 清理失败不影响主异常抛出 */ }
            }
            throw;
        }
    }

    /// <summary>从切块元数据里提取页码（切块器若输出了 page 相关元数据）</summary>
    private static bool TryGetPageNumber(IngestionChunk<string> chunk, out long pageNumber)
    {
        pageNumber = 0;
        if (!chunk.HasMetadata)
            return false;

        foreach (var (key, value) in chunk.Metadata)
        {
            if (!key.Contains("page", StringComparison.OrdinalIgnoreCase) || value is null)
                continue;

            // 元数据值可能是数字、字符串或字符串数组，统一尝试解析第一个元素
            var candidate = value switch
            {
                int n => n.ToString(),
                long l => l.ToString(),
                string s => s,
                System.Collections.IEnumerable list and not string => list.Cast<object>().FirstOrDefault()?.ToString(),
                _ => null,
            };

            if (candidate is not null && long.TryParse(candidate, out var parsed))
            {
                pageNumber = parsed;
                return true;
            }
        }
        return false;
    }
}
