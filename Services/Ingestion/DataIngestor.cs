using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using Qdrant.Client;
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
    IConfiguration configuration)
{
    // 与 DocxReader 的 cl100k 分词保持一致
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-3.5-turbo");

    /// <summary>导入一批文件并实时更新任务进度</summary>
    public async Task IngestFilesAsync(IngestionTask task, DirectoryInfo directory, IReadOnlyList<IngestionFileProgress> files)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                task.Status = IngestionTaskStatus.Processing;
                await IngestCoreAsync(task, directory, files);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                // Ollama 冷启动/偶发 400 时重试整个批次（已成功的文件在下一轮会被去重跳过）
                logger.LogWarning(ex, "文档导入第 {Attempt} 次尝试失败，准备重试...", attempt);
                task.Error = $"第 {attempt} 次尝试失败：{ex.Message}，正在重试...";
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private async Task IngestCoreAsync(IngestionTask task, DirectoryInfo directory, IReadOnlyList<IngestionFileProgress> files)
    {
        // 预热：先发一个小请求确保 Ollama 模型加载完毕，避免首次大请求碰到 runner 未就绪的 400
        await embeddingGenerator.GenerateAsync("warmup");

        // 按内容哈希去重；上传阶段已标 Skipped/Failed 的不再处理
        var newFiles = new List<FileInfo>();
        foreach (var progress in files)
        {
            if (progress.Status is IngestionFileStatus.Skipped or IngestionFileStatus.Failed)
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

            // 内容已经在库里（哪怕文件名不同）→ 跳过
            var existingByHash = await FindDocumentIdByContentHashAsync(hash);
            if (existingByHash is not null)
            {
                MarkFile(progress, IngestionFileStatus.Skipped,
                    $"知识库中已存在相同内容的文档（{existingByHash}），已过滤");
                continue;
            }

            // 同名但哈希不同：旧文件被替换，删掉旧向量再导入新内容
            if (await DocumentExistsAsync(progress.DocumentId))
            {
                await qdrantClient.DeleteAsync(QdrantChunkWriter.CollectionName, DocumentFilter(progress.DocumentId));
                logger.LogInformation("文档 '{Id}' 内容已变化，已删除旧向量，将按新内容导入。", progress.DocumentId);
            }

            newFiles.Add(new FileInfo(filePath));
        }

        task.ProcessedFiles = files.Count(f => f.Status is not (IngestionFileStatus.Pending or IngestionFileStatus.Processing));
        if (newFiles.Count == 0)
        {
            logger.LogInformation("所有文档均已存在或无有效文件，无需导入。");
            return;
        }

        logger.LogInformation("待导入新文档 {Count} 个：{Files}", newFiles.Count, string.Join(", ", newFiles.Select(f => f.Name)));

        // 文件级元数据：写入器在写切块 payload 时顺带补上，供列表/详情接口聚合
        var fileMeta = newFiles.ToDictionary(
            f => f.Name,
            f =>
            {
                var p = files.First(x => x.FileName == f.Name);
                return (Size: f.Length, UploadedAt: DateTimeOffset.Now, ContentHash: p.ContentHash ?? "");
            });

        int writeBatchSize = configuration.GetValue("Knowledge:ChunkWriteBatchSize", 32);
        var writer = new QdrantChunkWriter(embeddingGenerator, qdrantClient,
            loggerFactory.CreateLogger<QdrantChunkWriter>(), fileMeta, writeBatchSize);
        await writer.EnsureCollectionAsync();

        var chunkerOptions = new IngestionChunkerOptions(Tokenizer)
        {
            MaxTokensPerChunk = 1024,
            OverlapTokens = 50
        };

        // 先把本批文件全部解析+切块，再一次性交给写入器按批向量化入库。
        // 旧管道是「一个文件走完再处理下一个」，小文件无法拼进同一 embedding/Qdrant 批次。
        var reader = new DocumentReader(directory, loggerFactory);
        var chunker = new SemanticSimilarityChunker(embeddingGenerator, chunkerOptions);
        var allChunks = new List<IngestionChunk<string>>();
        var parsedOk = new List<IngestionFileProgress>();

        logger.LogInformation("开始解析切块 {Count} 个文档，随后按每批 {Batch} 条向量化入库。",
            newFiles.Count, writeBatchSize);

        foreach (var file in newFiles)
        {
            var progress = files.First(x => x.FileName == file.Name);
            progress.Status = IngestionFileStatus.Processing;
            if (progress.Task is not null)
                progress.Task.CurrentFile = file.Name;

            try
            {
                var document = await reader.ReadAsync(file, file.Name);
                await foreach (var chunk in chunker.ProcessAsync(document))
                    allChunks.Add(chunk);
                parsedOk.Add(progress);
            }
            catch (Exception ex)
            {
                MarkFile(progress, IngestionFileStatus.Failed, ex.Message);
                logger.LogWarning(ex, "文档 '{id}' 解析/切块失败。", progress.DocumentId);
            }
        }

        if (allChunks.Count > 0)
        {
            logger.LogInformation("解析完成，共 {ChunkCount} 个切块来自 {DocCount} 个文档，开始批量向量化入库。",
                allChunks.Count, parsedOk.Count);
            await writer.WriteAsync(ToAsyncEnumerable(allChunks));
        }

        foreach (var progress in parsedOk)
        {
            var chunkCount = await CountChunksAsync(progress.DocumentId);
            string? warning = chunkCount == 0
                ? "成功但解析到 0 个切块——文本提取与 OCR 均未得到可用内容，无法被检索和提问"
                : null;
            MarkFile(progress, IngestionFileStatus.Imported, warning, chunkCount);
            logger.LogInformation("文档 '{id}' 导入完成，共 {Count} 个切块。{Warning}",
                progress.DocumentId, chunkCount, warning ?? "");
        }

        // 有新切块入库后让 BM25 倒排失效，下次检索再从 Qdrant 重建
        if (files.Any(f => f.Status == IngestionFileStatus.Imported))
            bm25Index.MarkDirty();
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
        bool wasProcessed = progress.Status is not (IngestionFileStatus.Pending or IngestionFileStatus.Processing);
        progress.Status = status;
        progress.Error = error;
        progress.ChunkCount = chunkCount;
        if (!wasProcessed && progress.Task is not null)
            progress.Task.ProcessedFiles++;
        progress.Task!.CurrentFile = null;
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
