using Microsoft.Extensions.VectorData;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using WebRagApi.Models;
using WebRagApi.Services.Ingestion;

namespace WebRagApi.Services;

/// <summary>
/// 知识库管理服务：文档上传编排、列表、详情、删除。
/// 元数据（文件名/大小/上传时间）直接补写在 Qdrant 切块的 payload 上，
/// 不引入额外数据库，Qdrant 就是唯一的事实来源。
/// </summary>
public class KnowledgeService(
    ILogger<KnowledgeService> logger,
    QdrantClient qdrantClient,
    DataIngestor dataIngestor,
    IngestionTaskManager taskManager,
    IHostEnvironment environment,
    IConfiguration configuration)
{
    /// <summary>上传文档存放目录（相对于项目根目录，默认 App_Data/Documents）</summary>
    public DirectoryInfo DocumentsDirectory { get; } =
        new DirectoryInfo(Path.Combine(environment.ContentRootPath,
            configuration["Knowledge:DocumentsPath"] ?? "App_Data/Documents"));

    /// <summary>
    /// 保存上传的文件并启动后台导入任务。
    /// 返回任务 ID；同名文档已在知识库中的会被过滤为 duplicate。
    /// </summary>
    public UploadDocumentsResponse Upload(IFormFileCollection files)
    {
        DocumentsDirectory.Create();

        var progresses = new List<IngestionFileProgress>();
        var toSave = new List<(IFormFile File, string DocumentId)>();

        foreach (var file in files)
        {
            // 安全：剥离任何路径成分（防止 "..\..\evil.md" 之类的文件名写到目录之外）
            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeName) || safeName.StartsWith('.'))
            {
                progresses.Add(new IngestionFileProgress
                {
                    FileName = file.FileName,
                    DocumentId = file.FileName,
                    Status = IngestionFileStatus.Failed,
                    Error = "非法的文件名",
                });
                continue;
            }

            var ext = Path.GetExtension(safeName).ToLowerInvariant();
            if (!DocumentReader.SupportedExtensions.Contains(ext))
            {
                progresses.Add(new IngestionFileProgress
                {
                    FileName = safeName,
                    DocumentId = safeName,
                    Status = IngestionFileStatus.Failed,
                    Error = $"不支持的文件类型 '{ext}'，仅支持 pdf/doc/docx/md",
                });
                continue;
            }

            toSave.Add((file, safeName));
            progresses.Add(new IngestionFileProgress
            {
                FileName = safeName,
                DocumentId = safeName,
            });
        }

        // 先落盘再注册任务：后台导入直接读取磁盘文件
        foreach (var (file, _) in toSave)
        {
            var targetPath = Path.Combine(DocumentsDirectory.FullName, file.FileName);
            using var stream = new FileStream(targetPath, FileMode.Create);
            file.CopyTo(stream);
        }

        var task = taskManager.CreateTask("upload", progresses);

        // 后台导入：接口立刻返回任务 ID，前端轮询任务进度
        _ = Task.Run(async () =>
        {
            try
            {
                // 直接调用 DataIngestor 导入文件，导入过程中实时更新 task.Files 的状态
                await dataIngestor.IngestFilesAsync(task, DocumentsDirectory, task.Files);
                FinishTask(task);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "后台文档导入任务 {TaskId} 异常终止。", task.Id);
                task.Error = ex.Message;
                task.Status = task.Files.Any(f => f.Status == IngestionFileStatus.Imported)
                    ? IngestionTaskStatus.PartialFailed
                    : IngestionTaskStatus.Failed;
                task.FinishedAt = DateTimeOffset.Now;
            }
        });

        return new UploadDocumentsResponse
        {
            TaskId = task.Id,
            Results = task.Files.Select(f => new UploadFileResult
            {
                FileName = f.FileName,
                Status = f.Status switch
                {
                    IngestionFileStatus.Failed => "failed",
                    _ => "accepted",
                },
                Message = f.Error,
            }).ToList(),
        };
    }

    /// <summary>启动时扫描上传目录，把目录里尚未入库的文档全部导入（作为可查询的任务）</summary>
    public IngestionTask StartStartupIngestion()
    {
        DocumentsDirectory.Create();
        var files = DocumentReader.SupportedExtensions
            .SelectMany(ext => DocumentsDirectory.EnumerateFiles($"*{ext}", SearchOption.TopDirectoryOnly))
            .Select(f => new IngestionFileProgress { FileName = f.Name, DocumentId = f.Name })
            .ToList();

        var task = taskManager.CreateTask("startup", files);
        if (files.Count == 0)
        {
            task.Status = IngestionTaskStatus.Completed;
            task.FinishedAt = DateTimeOffset.Now;
            return task;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await dataIngestor.IngestFilesAsync(task, DocumentsDirectory, task.Files);
                FinishTask(task);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "启动文档导入任务 {TaskId} 异常终止。", task.Id);
                task.Error = ex.Message;
                task.Status = IngestionTaskStatus.Failed;
                task.FinishedAt = DateTimeOffset.Now;
            }
        });
        return task;
    }

    private static void FinishTask(IngestionTask task)
    {
        bool anyFailed = task.Files.Any(f => f.Status == IngestionFileStatus.Failed);
        bool anyImported = task.Files.Any(f => f.Status == IngestionFileStatus.Imported);
        task.Status = anyFailed && anyImported ? IngestionTaskStatus.PartialFailed
            : anyFailed ? IngestionTaskStatus.Failed
            : IngestionTaskStatus.Completed;
        task.FinishedAt = DateTimeOffset.Now;
        task.CurrentFile = null;
    }

    /// <summary>获取知识库全部文档列表（滚动 Qdrant 全部切块并按文档聚合）</summary>
    public async Task<List<DocumentInfo>> ListDocumentsAsync()
    {
        var points = await ScrollAllAsync();
        return points
            .GroupBy(p => GetPayloadString(p, "documentid") ?? "(未知)")
            .Select(g =>
            {
                var first = g.First();
                var meta = ParseMeta(first.Payload);
                return new DocumentInfo
                {
                    DocumentId = g.Key,
                    FileName = meta.FileName ?? g.Key,
                    FileSize = meta.FileSize,
                    UploadedAt = meta.UploadedAt,
                    ChunkCount = g.Count(),
                };
            })
            .OrderByDescending(d => d.UploadedAt)
            .ToList();
    }

    /// <summary>查看文档详情（含切块预览）</summary>
    public async Task<DocumentDetail?> GetDocumentAsync(string documentId)
    {
        var points = await ScrollAllAsync(documentId);
        var list = points.ToList();
        if (list.Count == 0)
            return null;

        var meta = ParseMeta(list[0].Payload);
        var detail = new DocumentDetail
        {
            DocumentId = documentId,
            FileName = meta.FileName ?? documentId,
            FileSize = meta.FileSize,
            UploadedAt = meta.UploadedAt,
            ChunkCount = list.Count,
            PageNumbers = list.Select(p => p.Payload.TryGetValue("page_number", out var v) && v.HasIntegerValue
                ? (int)v.IntegerValue : (int?)null).Where(v => v.HasValue).Select(v => v!.Value).Distinct().OrderBy(v => v).ToList(),
            ChunkPreviews = list.Take(5).Select(p => new DocumentChunkPreview
            {
                Key = p.Id.Uuid,
                Context = GetPayloadString(p, "context"),
                PageNumber = p.Payload.TryGetValue("page_number", out var pv) && pv.HasIntegerValue ? (int)pv.IntegerValue : null,
                Text = Truncate(GetPayloadString(p, "content") ?? string.Empty, 300),
            }).ToList(),
        };
        return detail;
    }

    /// <summary>删除文档及其全部向量，同时清理磁盘文件。返回 false 表示文档不存在</summary>
    public async Task<bool> DeleteDocumentAsync(string documentId)
    {
        int chunkCount = await dataIngestor.CountChunksAsync(documentId);
        if (chunkCount == 0)
            return false;

        await qdrantClient.DeleteAsync(QdrantChunkWriter.CollectionName, DataIngestor.DocumentFilter(documentId));

        var filePath = Path.Combine(DocumentsDirectory.FullName, documentId);
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (IOException ex)
        {
            // 向量已删干净，源文件删除失败（如被占用）只记警告，不让整个删除请求失败
            logger.LogWarning(ex, "文档 '{DocumentId}' 的向量已删除，但磁盘源文件删除失败（可能被占用）。", documentId);
        }

        logger.LogInformation("文档 '{DocumentId}' 已删除（{Count} 个切块）。", documentId, chunkCount);
        return true;
    }

    // ---------- 内部工具 ----------

    /// <summary>滚动读取集合中所有点（可按文档过滤），只取 payload 不取向量</summary>
    private async Task<IEnumerable<Qdrant.Client.Grpc.RetrievedPoint>> ScrollAllAsync(string? documentId = null)
    {
        var filter = documentId is null ? null : DataIngestor.DocumentFilter(documentId);

        var all = new List<Qdrant.Client.Grpc.RetrievedPoint>();
        PointId? offset = null;
        while (true)
        {
            Qdrant.Client.Grpc.ScrollResponse response;
            try
            {
                response = await qdrantClient.ScrollAsync(
                    QdrantChunkWriter.CollectionName,
                    filter,
                    limit: 1000,
                    offset: offset);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                // 集合还不存在（空库），视为没有文档
                return all;
            }

            var list = response.Result.ToList();
            if (list.Count == 0)
                break;

            all.AddRange(list);
            if (list.Count < 1000 || response.NextPageOffset is null)
                break;

            offset = response.NextPageOffset;
        }
        return all;
    }

    private static string? GetPayloadString(RetrievedPoint point, string key)
        => point.Payload.TryGetValue(key, out var v) && v.HasStringValue ? v.StringValue : null;

    private static (string? FileName, long FileSize, DateTimeOffset? UploadedAt) ParseMeta(
        Google.Protobuf.Collections.MapField<string, Qdrant.Client.Grpc.Value> payload)
    {
        string? fileName = payload.TryGetValue("filename", out var fn) && fn.HasStringValue ? fn.StringValue : null;
        long fileSize = payload.TryGetValue("filesize", out var fs) && fs.HasDoubleValue ? (long)fs.DoubleValue : 0;
        DateTimeOffset? uploadedAt = payload.TryGetValue("uploadedat", out var ua) && ua.HasStringValue
            && DateTimeOffset.TryParse(ua.StringValue, out var parsed) ? parsed : null;
        return (fileName, fileSize, uploadedAt);
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
