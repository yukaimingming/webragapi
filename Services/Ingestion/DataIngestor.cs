using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using WebRagApi.Models;
using WebRagApi.Services.Retrieval;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 文档导入器：解析 → 语义切块 → 向量化 → 写入 Qdrant。
/// 与 AIChatApp 的实现同源，区别是向量库换成 Qdrant（经自定义 QdrantChunkWriter 写入），
/// 并对接任务进度上报。按文件内容 SHA256 去重：内容相同即跳过（与文件名无关）；
/// 同名但内容变了会删旧向量再导入。
/// </summary>
public class DataIngestor(
    ILogger<DataIngestor> logger,
    ILoggerFactory loggerFactory,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    QdrantClient qdrantClient,
    Bm25Index bm25Index,
    DocumentCatalog documentCatalog,
    IConfiguration configuration)
{
    // 与 DocxReader 的 cl100k 分词保持一致
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-3.5-turbo");
    private readonly SemaphoreSlim _ingestGate = new(1, 1);

    /// <summary>导入一批文件。全库同一时刻只跑一个导入任务，避免并行互踩。</summary>
    public async Task IngestFilesAsync(IngestionTask task, DirectoryInfo directory, IReadOnlyList<IngestionFileProgress> files)
    {
        await _ingestGate.WaitAsync();
        try
        {
            task.Status = IngestionTaskStatus.Processing;
            await embeddingGenerator.GenerateAsync("warmup");

            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var pending = files.Where(f => f.Status is IngestionFileStatus.Pending
                    or IngestionFileStatus.Processing or IngestionFileStatus.Failed).ToList();
                if (pending.Count == 0)
                    break;

                if (attempt > 1)
                {
                    logger.LogWarning("文档导入第 {Attempt} 次尝试，重试 {Count} 个失败文件。", attempt, pending.Count);
                    task.Error = $"第 {attempt - 1} 次未全部成功，正在重试剩余文件…";
                    foreach (var f in pending)
                        f.Status = IngestionFileStatus.Pending;
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }

                await IngestPendingAsync(task, directory, pending);
            }
        }
        finally
        {
            _ingestGate.Release();
        }
    }

    /// <summary>按文档提交：一篇写成功后再删旧切块；失败只回滚本篇新点。</summary>
    private async Task IngestPendingAsync(IngestionTask task, DirectoryInfo directory, List<IngestionFileProgress> files)
    {
        int writeBatchSize = configuration.GetValue("Knowledge:ChunkWriteBatchSize", 32);
        var reader = new DocumentReader(directory, loggerFactory);
        var chunker = new SemanticSimilarityChunker(embeddingGenerator, new IngestionChunkerOptions(Tokenizer)
        {
            MaxTokensPerChunk = 1024,
            OverlapTokens = 50,
        });

        bool anyImported = false;
        foreach (var progress in files)
        {
            if (progress.Status is IngestionFileStatus.Skipped or IngestionFileStatus.Imported)
                continue;

            var filePath = Path.Combine(directory.FullName, progress.FileName);
            if (!File.Exists(filePath))
            {
                MarkFile(progress, IngestionFileStatus.Failed, "文件不存在");
                continue;
            }

            var hash = progress.ContentHash;
            if (string.IsNullOrEmpty(hash))
                hash = ContentHash.Sha256File(filePath);
            progress.ContentHash = hash;

            var existingByHash = await FindDocumentIdByContentHashAsync(hash);
            if (existingByHash is not null)
            {
                MarkFile(progress, IngestionFileStatus.Skipped,
                    $"知识库中已存在相同内容的文档（{existingByHash}），已过滤");
                continue;
            }

            progress.Status = IngestionFileStatus.Processing;
            if (progress.Task is not null)
                progress.Task.CurrentFile = progress.FileName;

            var file = new FileInfo(filePath);
            var fileMeta = new Dictionary<string, (long Size, DateTimeOffset UploadedAt, string ContentHash)>
            {
                [file.Name] = (file.Length, DateTimeOffset.Now, hash),
            };
            var writer = new QdrantChunkWriter(embeddingGenerator, qdrantClient,
                loggerFactory.CreateLogger<QdrantChunkWriter>(), fileMeta, writeBatchSize);

            List<Guid> oldIds;
            try
            {
                oldIds = await ListPointIdsAsync(progress.DocumentId);
                var document = await reader.ReadAsync(file, file.Name);
                var chunks = new List<IngestionChunk<string>>();
                await foreach (var chunk in chunker.ProcessAsync(document))
                    chunks.Add(chunk);

                if (chunks.Count == 0)
                {
                    MarkFile(progress, IngestionFileStatus.Imported,
                        "成功但解析到 0 个切块——文本提取与 OCR 均未得到可用内容，无法被检索和提问", 0);
                    continue;
                }

                await writer.EnsureCollectionAsync();
                await writer.WriteAsync(ToAsyncEnumerable(chunks));

                // 新切块已成功：再删旧点，避免先删后写失败丢知识
                if (oldIds.Count > 0)
                {
                    await qdrantClient.DeleteAsync(QdrantChunkWriter.CollectionName, oldIds);
                    logger.LogInformation("文档 '{Id}' 新切块已提交，已删除 {Count} 个旧切块。", progress.DocumentId, oldIds.Count);
                }

                var chunkCount = await CountChunksAsync(progress.DocumentId);
                MarkFile(progress, IngestionFileStatus.Imported, null, chunkCount);
                anyImported = true;
                logger.LogInformation("文档 '{id}' 导入完成，共 {Count} 个切块。", progress.DocumentId, chunkCount);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "文档 '{id}' 导入失败（旧切块已保留）。", progress.DocumentId);
                MarkFile(progress, IngestionFileStatus.Failed, ex.Message);
            }
        }

        if (anyImported)
        {
            bm25Index.MarkDirty();
            documentCatalog.MarkDirty();
            _ = Task.Run(() => bm25Index.EnsureReadyAsync());
        }
    }

    private static async IAsyncEnumerable<IngestionChunk<string>> ToAsyncEnumerable(IEnumerable<IngestionChunk<string>> chunks)
    {
        foreach (var chunk in chunks)
            yield return chunk;
        await Task.CompletedTask;
    }

    /// <summary>把处理结果写回进度对象，并推进任务级计数</summary>
    private void MarkFile(IngestionFileProgress progress, IngestionFileStatus status, string? error = null, int chunkCount = 0)
    {
        progress.Status = status;
        progress.Error = error;
        progress.ChunkCount = chunkCount;
        if (progress.Task is not null)
        {
            progress.Task.ProcessedFiles = progress.Task.Files.Count(f =>
                f.Status is not (IngestionFileStatus.Pending or IngestionFileStatus.Processing));
            progress.Task.CurrentFile = null;
        }
    }

    /// <summary>列出某文档当前切块的点 ID，供覆盖导入时「先写新再删旧」。</summary>
    public async Task<List<Guid>> ListPointIdsAsync(string documentId)
    {
        var ids = new List<Guid>();
        PointId? offset = null;
        while (true)
        {
            ScrollResponse response;
            try
            {
                response = await qdrantClient.ScrollAsync(
                    QdrantChunkWriter.CollectionName,
                    filter: DocumentFilter(documentId),
                    limit: 1000,
                    offset: offset,
                    payloadSelector: false);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                return ids;
            }

            var list = response.Result.ToList();
            if (list.Count == 0)
                break;
            foreach (var p in list)
            {
                if (p.Id.HasUuid && Guid.TryParse(p.Id.Uuid, out var g))
                    ids.Add(g);
            }
            if (list.Count < 1000 || response.NextPageOffset is null)
                break;
            offset = response.NextPageOffset;
        }
        return ids;
    }

    /// <summary>构造按文档标识过滤的 Qdrant 检索条件（新版客户端必须用 Condition 包裹 FieldCondition）</summary>
    public static Qdrant.Client.Grpc.Filter DocumentFilter(string documentId) => new()
    {
        Must = { new Qdrant.Client.Grpc.Condition
        {
            Field = new Qdrant.Client.Grpc.FieldCondition
            {
                Key = "documentid",
                Match = new Qdrant.Client.Grpc.Match { Keyword = documentId }
            }
        }}
    };

    /// <summary>按内容 SHA256 查找已入库文档的 documentid（即文件名）；没有则返回 null</summary>
    public async Task<string?> FindDocumentIdByContentHashAsync(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
            return null;
        try
        {
            var response = await qdrantClient.ScrollAsync(
                QdrantChunkWriter.CollectionName,
                filter: ContentHashFilter(contentHash),
                limit: 1);
            var point = response.Result.FirstOrDefault();
            if (point is null)
                return null;
            if (point.Payload.TryGetValue("filename", out var fn) && fn.HasStringValue && fn.StringValue.Length > 0)
                return fn.StringValue;
            if (point.Payload.TryGetValue("documentid", out var id) && id.HasStringValue)
                return id.StringValue;
            return null;
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>按文件内容 SHA256 过滤</summary>
    public static Qdrant.Client.Grpc.Filter ContentHashFilter(string contentHash) => new()
    {
        Must = { new Qdrant.Client.Grpc.Condition
        {
            Field = new Qdrant.Client.Grpc.FieldCondition
            {
                Key = "contenthash",
                Match = new Qdrant.Client.Grpc.Match { Keyword = contentHash }
            }
        }}
    };

    /// <summary>判断某文档是否已在向量库中（按 documentid 精确计数）</summary>
    public async Task<bool> DocumentExistsAsync(string documentId)
    {
        try
        {
            // Qdrant 计数接口返回的数量 > 0 表示已存在该文档
            var count = await qdrantClient.CountAsync(QdrantChunkWriter.CollectionName, filter: DocumentFilter(documentId));
            return count > 0;
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            // 集合还不存在（空库），视为没有已导入文档
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "查询 Qdrant 文档是否已存在失败（{DocumentId}）。", documentId);
            throw;
        }
    }

    /// <summary>统计某文档的切块数量（0 表示尚未导入）</summary>
    public async Task<int> CountChunksAsync(string documentId)
    {
        var count = await qdrantClient.CountAsync(QdrantChunkWriter.CollectionName, filter: DocumentFilter(documentId));
        return (int)count;
    }
}
