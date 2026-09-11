using Qdrant.Client;
using Qdrant.Client.Grpc;
using WebRagApi.Models;
using WebRagApi.Services.Ingestion;

namespace WebRagApi.Services.Retrieval;

/// <summary>
/// 文档列表缓存：只滚元数据 payload（不含正文），导入/删除后失效。
/// </summary>
public sealed class DocumentCatalog(QdrantClient qdrantClient, ILogger<DocumentCatalog> logger)
{
    private readonly object _gate = new();
    private List<DocumentInfo>? _cache;
    private bool _dirty = true;

    public void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            _cache = null;
        }
    }

    public async Task<List<DocumentInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_dirty && _cache is not null)
                return _cache;
        }

        var points = await ScrollMetaAsync(cancellationToken);
        var list = points
            .GroupBy(p => Payload(p, "documentid") ?? "(未知)")
            .Select(g =>
            {
                var first = g.First();
                string? fileName = Payload(first, "filename");
                long fileSize = first.Payload.TryGetValue("filesize", out var fs) && fs.HasDoubleValue
                    ? (long)fs.DoubleValue : 0;
                DateTimeOffset? uploadedAt = first.Payload.TryGetValue("uploadedat", out var ua)
                    && ua.HasStringValue && DateTimeOffset.TryParse(ua.StringValue, out var parsed)
                    ? parsed : null;
                return new DocumentInfo
                {
                    DocumentId = g.Key,
                    FileName = fileName ?? g.Key,
                    FileSize = fileSize,
                    UploadedAt = uploadedAt,
                    ChunkCount = g.Count(),
                };
            })
            .OrderByDescending(d => d.UploadedAt)
            .ToList();

        lock (_gate)
        {
            _cache = list;
            _dirty = false;
        }
        logger.LogDebug("文档列表缓存已刷新，共 {Count} 篇。", list.Count);
        return list;
    }

    private async Task<List<RetrievedPoint>> ScrollMetaAsync(CancellationToken ct)
    {
        var include = new PayloadIncludeSelector();
        include.Fields.Add("documentid");
        include.Fields.Add("filename");
        include.Fields.Add("filesize");
        include.Fields.Add("uploadedat");
        var payloadSelector = new WithPayloadSelector { Include = include };

        var all = new List<RetrievedPoint>();
        PointId? offset = null;
        while (true)
        {
            ScrollResponse response;
            try
            {
                response = await qdrantClient.ScrollAsync(
                    QdrantChunkWriter.CollectionName,
                    filter: null,
                    limit: 1000,
                    offset: offset,
                    payloadSelector: payloadSelector,
                    cancellationToken: ct);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
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

    private static string? Payload(RetrievedPoint point, string key)
        => point.Payload.TryGetValue(key, out var v) && v.HasStringValue ? v.StringValue : null;
}
