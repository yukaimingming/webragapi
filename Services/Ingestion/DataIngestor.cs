using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using Qdrant.Client;
using WebRagApi.Models;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 文档导入器：解析 → 语义切块 → 向量化 → 写入 Qdrant。
/// 与 AIChatApp 的实现同源，区别是向量库换成 Qdrant（经自定义 QdrantChunkWriter 写入），
/// 并对接任务进度上报。只导入库里没有的新文档，已存在的自动跳过。
/// </summary>
public class DataIngestor(
    ILogger<DataIngestor> logger,
    ILoggerFactory loggerFactory,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    QdrantClient qdrantClient)
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

        // 逐个文件过滤掉知识库里已存在的文档（增量导入：只处理新文档）
        var newFiles = new List<FileInfo>();
        foreach (var progress in files)
        {
            var filePath = Path.Combine(directory.FullName, progress.FileName);
            if (!File.Exists(filePath))
            {
                MarkFile(progress, IngestionFileStatus.Failed, "文件不存在");
                continue;
            }

            if (await DocumentExistsAsync(progress.DocumentId))
            {
                // 已存在：直接跳过，不重复向量化（“上传只上传新文档的，存在的过滤”）
                MarkFile(progress, IngestionFileStatus.Skipped, "知识库中已存在同名文档，已过滤");
                continue;
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
            f => (Size: f.Length, UploadedAt: DateTimeOffset.Now));

        // 自定义写入器：切块 → bge-m3 向量化 → 写入 Qdrant（Guid 键 + 完整 payload）
        var writer = new QdrantChunkWriter(embeddingGenerator, qdrantClient,
            loggerFactory.CreateLogger<QdrantChunkWriter>(), fileMeta);
        await writer.EnsureCollectionAsync();

        // 语义切块器：按嵌入相似度把相邻元素聚合成块，边界更贴合语义
        var chunkerOptions = new IngestionChunkerOptions(Tokenizer)
        {
            // bge-m3 支持 8192 上下文，1024 兼顾检索精度和超长元素（如大表格）的容纳能力
            MaxTokensPerChunk = 1024,
            OverlapTokens = 50
        };

        using var pipeline = new IngestionPipeline<string>(
            reader: new DocumentReader(directory),
            chunker: new SemanticSimilarityChunker(embeddingGenerator, chunkerOptions),
            writer: writer,
            loggerFactory: loggerFactory);

        // 逐文件处理并上报进度（pipeline 每完成一个文档 yield 一次结果）
        await foreach (var result in pipeline.ProcessAsync(newFiles))
        {
            var progress = files.FirstOrDefault(f => f.DocumentId == result.DocumentId);
            if (progress is null)
                continue;

            if (result.Succeeded)
            {
                var chunkCount = await CountChunksAsync(progress.DocumentId);
                // 成功但 0 切块：通常是扫描件/图片型 PDF，没有文字层可解析
                string? warning = chunkCount == 0
                    ? "成功但解析到 0 个切块——该文件没有可提取的文本（可能是扫描件/图片型 PDF，暂不支持 OCR），无法被检索和提问"
                    : null;
                MarkFile(progress, IngestionFileStatus.Imported, warning, chunkCount);
                logger.LogWarning("文档 '{id}' 导入完成，共 {Count} 个切块。{Warning}", result.DocumentId, chunkCount, warning ?? "");
            }
            else
            {
                MarkFile(progress, IngestionFileStatus.Failed, result.Exception?.Message ?? "导入失败");
                logger.LogWarning("文档 '{id}' 导入失败：{Reason}", result.DocumentId, result.Exception?.Message);
            }
        }
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

    /// <summary>判断某文档是否已在向量库中（按 documentid 精确计数）</summary>
    public async Task<bool> DocumentExistsAsync(string documentId)
    {
        try
        {
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
