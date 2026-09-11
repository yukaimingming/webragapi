using Qdrant.Client;
using Qdrant.Client.Grpc;
using WebRagApi.Models;
using WebRagApi.Services.Ingestion;

namespace WebRagApi.Services.Retrieval;

/// <summary>
/// 内存 BM25 倒排索引。切块来自 Qdrant payload，导入/删除后标记脏数据，下次检索时重建。
/// 不改 Qdrant 集合结构，已有向量数据可直接用。
/// </summary>
public sealed class Bm25Index(QdrantClient qdrantClient, ILogger<Bm25Index> logger)
{
    // BM25 经典参数：k1 控制词频饱和，b 控制文档长度归一
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly ReaderWriterLockSlim _lock = new();
    private List<ChunkDoc> _docs = [];
    private Dictionary<string, List<(int Doc, int Tf)>> _postings = new(StringComparer.Ordinal);
    private double _avgdl;
    private bool _dirty = true;
    private int _epoch;

    /// <summary>导入或删除切块后调用，下次检索会从 Qdrant 重建倒排。</summary>
    public void MarkDirty()
    {
        _lock.EnterWriteLock();
        try
        {
            _dirty = true;
            _epoch++;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>若索引已脏则重建。加载期间若再次 MarkDirty，会再扫一轮，避免用过期快照清掉脏标记。</summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            int epoch;
            _lock.EnterReadLock();
            try
            {
                if (!_dirty)
                    return;
                epoch = _epoch;
            }
            finally { _lock.ExitReadLock(); }

            var docs = await LoadChunksAsync(cancellationToken);
            _lock.EnterWriteLock();
            try
            {
                RebuildUnlocked(docs);
                if (_epoch == epoch)
                {
                    _dirty = false;
                    logger.LogInformation("BM25 索引已重建，共 {Count} 个切块。", docs.Count);
                    return;
                }
            }
            finally { _lock.ExitWriteLock(); }
        }
    }

    /// <summary>BM25 检索，返回切块与原始 BM25 分数。</summary>
    public List<SearchResultItem> Search(string query, int topK, string? documentIdFilter)
    {
        var qTokens = ChineseLexicalTokenizer.Tokenize(query);
        if (qTokens.Count == 0 || topK <= 0)
            return [];

        _lock.EnterReadLock();
        try
        {
            if (_docs.Count == 0)
                return [];

            var tfq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in qTokens)
            {
                tfq[t] = tfq.TryGetValue(t, out var n) ? n + 1 : 1;
            }

            var scores = new Dictionary<int, double>();
            int nDocs = _docs.Count;
            foreach (var (term, _) in tfq)
            {
                if (!_postings.TryGetValue(term, out var posting))
                    continue;

                // Lucene 风格 IDF，加 1 避免稀有词为负
                double idf = Math.Log(1 + (nDocs - posting.Count + 0.5) / (posting.Count + 0.5));
                foreach (var (doc, tf) in posting)
                {
                    if (documentIdFilter is { Length: > 0 }
                        && !string.Equals(_docs[doc].DocumentId, documentIdFilter, StringComparison.Ordinal))
                        continue;

                    double dl = _docs[doc].Length;
                    double denom = tf + K1 * (1 - B + B * dl / Math.Max(_avgdl, 1));
                    double add = idf * (tf * (K1 + 1)) / denom;
                    scores[doc] = scores.TryGetValue(doc, out var s) ? s + add : add;
                }
            }

            return scores
                .OrderByDescending(kv => kv.Value)
                .Take(topK)
                .Select(kv => ToItem(_docs[kv.Key], kv.Value))
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>用切块词频表重建倒排表与平均文档长度（调用方须已持有写锁）。</summary>
    private void RebuildUnlocked(List<ChunkDoc> docs)
    {
        var postings = new Dictionary<string, List<(int Doc, int Tf)>>(StringComparer.Ordinal);
        double totalLen = 0;
        for (int i = 0; i < docs.Count; i++)
        {
            totalLen += docs[i].Length;
            foreach (var (term, tf) in docs[i].Tf)
            {
                if (!postings.TryGetValue(term, out var list))
                {
                    list = [];
                    postings[term] = list;
                }
                list.Add((i, tf));
            }
        }

        _docs = docs;
        _postings = postings;
        _avgdl = docs.Count == 0 ? 1 : totalLen / docs.Count;
    }

    /// <summary>只滚 payload、不取向量，避免重建 BM25 时把 embedding 全载入内存。</summary>
    private async Task<List<ChunkDoc>> LoadChunksAsync(CancellationToken ct)
    {
        var all = new List<ChunkDoc>();
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
                    cancellationToken: ct);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                return all;
            }

            var list = response.Result.ToList();
            if (list.Count == 0)
                break;

            foreach (var p in list)
            {
                var text = Payload(p, "content") ?? string.Empty;
                var tokens = ChineseLexicalTokenizer.Tokenize(text);
                var tf = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var t in tokens)
                    tf[t] = tf.TryGetValue(t, out var n) ? n + 1 : 1;

                all.Add(new ChunkDoc
                {
                    ChunkId = p.Id.Uuid,
                    DocumentId = Payload(p, "documentid") ?? "(未知)",
                    FileName = Payload(p, "filename") ?? Payload(p, "documentid") ?? "(未知)",
                    Text = text,
                    Context = Payload(p, "context"),
                    PageNumber = p.Payload.TryGetValue("page_number", out var pv) && pv.HasIntegerValue
                        ? (int)pv.IntegerValue : null,
                    Tf = tf,
                    Length = Math.Max(tokens.Count, 1),
                });
            }

            if (list.Count < 1000 || response.NextPageOffset is null)
                break;
            offset = response.NextPageOffset;
        }
        return all;
    }

    private static SearchResultItem ToItem(ChunkDoc doc, double bm25)
        => new()
        {
            ChunkId = doc.ChunkId,
            DocumentId = doc.DocumentId,
            FileName = doc.FileName,
            Score = bm25,
            Bm25Score = bm25,
            Text = doc.Text,
            Context = string.IsNullOrEmpty(doc.Context) ? null : doc.Context,
            PageNumber = doc.PageNumber,
        };

    private static string? Payload(RetrievedPoint point, string key)
        => point.Payload.TryGetValue(key, out var v) && v.HasStringValue && v.StringValue.Length > 0
            ? v.StringValue : null;

    private sealed class ChunkDoc
    {
        public required string ChunkId { get; init; }
        public required string DocumentId { get; init; }
        public required string FileName { get; init; }
        public required string Text { get; init; }
        public string? Context { get; init; }
        public int? PageNumber { get; init; }
        public required Dictionary<string, int> Tf { get; init; }
        public required int Length { get; init; }
    }
}
