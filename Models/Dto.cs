namespace WebRagApi.Models;

/// <summary>文档导入任务的总体状态</summary>
public enum IngestionTaskStatus
{
    /// <summary>排队中，尚未开始处理</summary>
    Queued,

    /// <summary>正在处理</summary>
    Processing,

    /// <summary>全部文件导入成功</summary>
    Completed,

    /// <summary>部分文件导入成功（至少一个失败）</summary>
    PartialFailed,

    /// <summary>全部失败</summary>
    Failed
}

/// <summary>单个文件的导入状态</summary>
public enum IngestionFileStatus
{
    /// <summary>等待处理</summary>
    Pending,

    /// <summary>正在处理（解析/切块/向量化）</summary>
    Processing,

    /// <summary>导入成功</summary>
    Imported,

    /// <summary>已存在被跳过</summary>
    Skipped,

    /// <summary>导入失败</summary>
    Failed
}

/// <summary>一次文档导入任务（可包含多个文件），供 /api/knowledge/tasks/{id} 查询进度</summary>
public class IngestionTask
{
    /// <summary>任务 ID（上传接口返回的 taskId）</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>任务总体状态</summary>
    public IngestionTaskStatus Status { get; set; } = IngestionTaskStatus.Queued;

    /// <summary>本次任务涉及的文件总数</summary>
    public int TotalFiles { get; set; }

    /// <summary>已处理完（成功/跳过/失败）的文件数</summary>
    public int ProcessedFiles { get; set; }

    /// <summary>当前正在处理的文件名</summary>
    public string? CurrentFile { get; set; }

    /// <summary>触发来源：upload=上传接口；startup=服务启动扫描目录</summary>
    public string? Trigger { get; set; }
    /// <summary>任务开始时间</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>任务结束时间；进行中为空</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>整体错误信息（如 Ollama/Qdrant 连接失败）</summary>
    public string? Error { get; set; }

    /// <summary>每个文件的明细进度</summary>
    public List<IngestionFileProgress> Files { get; set; } = [];
}

/// <summary>单个文件的导入进度明细</summary>
public class IngestionFileProgress
{
    /// <summary>上传的文件名</summary>
    public required string FileName { get; set; }

    /// <summary>文档标识（列表/删除仍用文件名）</summary>
    public required string DocumentId { get; set; }

    /// <summary>文件内容 SHA256，用于判断是否重复上传</summary>
    public string? ContentHash { get; set; }

    /// <summary>文件大小（字节）</summary>
    public IngestionFileStatus Status { get; set; } = IngestionFileStatus.Pending;

    /// <summary>成功导入后生成的切块数量</summary>
    public int ChunkCount { get; set; }

    /// <summary>失败或跳过原因</summary>
    public string? Error { get; set; }

    /// <summary>所属任务的引用（导入器用它推进任务级计数）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IngestionTask? Task { get; set; }
}

/// <summary>POST /api/knowledge/documents 的响应（上传结果）</summary>
public class UploadDocumentsResponse
{
    /// <summary>后台导入任务 ID，用 GET /api/knowledge/tasks/{id} 查进度</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>每个上传文件的受理结果</summary>
    public List<UploadFileResult> Results { get; set; } = [];
}

/// <summary>单个上传文件的受理结果</summary>
public class UploadFileResult
{
    /// <summary>原始文件名</summary>
    public required string FileName { get; set; }

    /// <summary>accepted=已受理进入导入队列；duplicate=内容哈希已存在被过滤；failed=校验失败</summary>
    public required string Status { get; set; }

    /// <summary>说明，如重复时已有文档的文件名</summary>
    public string? Message { get; set; }
}

/// <summary>GET /api/knowledge/documents 列表项</summary>
public class DocumentInfo
{
    /// <summary>文档标识（即文件名）</summary>
    public required string DocumentId { get; set; }

    /// <summary>显示用文件名</summary>
    public required string FileName { get; set; }
    /// <summary>源文件大小（字节）</summary>
    public long FileSize { get; set; }
    /// <summary>入库时间</summary>
    public DateTimeOffset? UploadedAt { get; set; }

    /// <summary>该文档被切成的块数</summary>
    public int ChunkCount { get; set; }
}

/// <summary>GET /api/knowledge/documents/{id} 详情</summary>
public class DocumentDetail : DocumentInfo
{
    /// <summary>文档覆盖的页码（PDF 才有）</summary>
    public List<int> PageNumbers { get; set; } = [];

    /// <summary>前几个切块的内容预览</summary>
    public List<DocumentChunkPreview> ChunkPreviews { get; set; } = [];
}

/// <summary>切块内容预览</summary>
public class DocumentChunkPreview
{
    /// <summary>切块在向量库中的点 ID</summary>
    public required string Key { get; set; }
    /// <summary>切块上下文（如章节标题）</summary>
    public string? Context { get; set; }
    /// <summary>页码；非 PDF 可为空</summary>
    public int? PageNumber { get; set; }
    /// <summary>切块正文预览</summary>
    public required string Text { get; set; }
}

/// <summary>POST /api/knowledge/search 请求</summary>
public class SearchRequest
{
    /// <summary>查询文本</summary>
    public required string Query { get; set; }

    /// <summary>返回的最相似切块数量，默认 5</summary>
    public int TopK { get; set; } = 5;

    /// <summary>可选：限定只在某个文档内检索</summary>
    public string? DocumentId { get; set; }

    /// <summary>vector=仅向量；hybrid=向量+BM25+RRF；rerank=hybrid后再重排。空则用配置默认值</summary>
    public string? Mode { get; set; }
}

/// <summary>检索单条结果</summary>
public class SearchResultItem
{
    /// <summary>切块点 ID，融合多路结果时用</summary>
    public string? ChunkId { get; set; }
    /// <summary>所属文档标识（文件名）</summary>
    public required string DocumentId { get; set; }
    /// <summary>文件名</summary>
    public required string FileName { get; set; }
    /// <summary>最终排序分（向量余弦 / RRF / 重排分，取决于 mode）</summary>
    public double Score { get; set; }
    /// <summary>稠密向量余弦分；仅向量检索或融合后带回</summary>
    public double? VectorScore { get; set; }
    /// <summary>BM25 原始分；仅混合检索或融合后带回</summary>
    public double? Bm25Score { get; set; }
    /// <summary>切块正文</summary>
    public required string Text { get; set; }
    /// <summary>切块上下文</summary>
    public string? Context { get; set; }
    /// <summary>页码；非 PDF 可为空</summary>
    public int? PageNumber { get; set; }
}

/// <summary>POST /api/chat/stream 请求</summary>
public class ChatRequest
{
    /// <summary>用户问题</summary>
    public required string Question { get; set; }

    /// <summary>检索多少个切块作为上下文，默认 8</summary>
    public int TopK { get; set; } = 8;

    /// <summary>可选：限定只在某个文档内问答</summary>
    public string? DocumentId { get; set; }

    /// <summary>
    /// 知识库未命中时是否允许大模型直接推理回答（智慧病历模式，默认 true）。
    /// 设为 false 则退回严格 RAG：知识库没有相关内容时直接提示。
    /// </summary>
    public bool? AllowModelAnswer { get; set; }

    /// <summary>历史对话（可选，多轮问答时传入）</summary>
    public List<ChatHistoryMessage>? History { get; set; }

    /// <summary>是否开启深度思考。关闭时后端向商汤传 reasoning_effort=none</summary>
    public bool? Thinking { get; set; }

    /// <summary>推理等级：low / medium / high。thinking=false 时忽略</summary>
    public string? ReasoningEffort { get; set; }
}

/// <summary>多轮对话的历史消息</summary>
public class ChatHistoryMessage
{
    /// <summary>角色：user 或 assistant</summary>
    public required string Role { get; set; }
    /// <summary>消息正文</summary>
    public required string Content { get; set; }
}

/// <summary>回答引用的来源（按文档聚合，出现在 SSE end 事件的 sources）</summary>
public class ChatSource
{
    /// <summary>文档标识（文件名）</summary>
    public required string DocumentId { get; set; }
    /// <summary>文件名</summary>
    public required string FileName { get; set; }

    /// <summary>引用内容所在的页码（无页码概念时为空）</summary>
    public List<int> PageNumbers { get; set; } = [];
}

/// <summary>流式问答的 SSE 事件（POST /api/chat/stream，text/event-stream，每事件一行 data: JSON）</summary>
public class ChatStreamEvent
{
    /// <summary>事件类型：meta=检索命中明细；mode=回答模式；reasoning=思考过程增量；delta=增量文本；end=结束</summary>
    public required string Type { get; set; }

    /// <summary>回答模式（mode/end 事件携带）：knowledge_base / model</summary>
    public string? Mode { get; set; }

    /// <summary>增量文本（delta 事件携带）</summary>
    public string? Text { get; set; }

    /// <summary>检索命中的切块明细（meta 事件携带，mode=knowledge_base 时可作为引用展示）</summary>
    public List<SearchResultItem>? References { get; set; }

    /// <summary>按文档聚合的引用来源（end 事件携带，mode=knowledge_base 时有值）</summary>
    public List<ChatSource>? Sources { get; set; }
}

/// <summary>GET /api/chat/config 响应（不含密钥）</summary>
public class ChatConfigResponse
{
    /// <summary>当前聊天模型名</summary>
    public required string Model { get; set; }

    /// <summary>是否支持深度思考</summary>
    public bool ThinkingSupported { get; set; }

    /// <summary>可用推理等级：low / medium / high</summary>
    public required string[] ReasoningEfforts { get; set; }
}

/// <summary>GET /api/update/manifest 响应。无可用更新时 LatestVersion、PackageUrl 为空串。</summary>
public class UpdateManifestResponse
{
    /// <summary>最新版本号；无更新时为空</summary>
    public string LatestVersion { get; set; } = "";

    /// <summary>客户端最低支持版本</summary>
    public string? MinSupportedVersion { get; set; }

    /// <summary>更新包下载地址；无更新时为空</summary>
    public string PackageUrl { get; set; } = "";

    /// <summary>更新包 SHA256</summary>
    public string? Sha256 { get; set; }

    /// <summary>是否强制更新</summary>
    public bool ForceUpdate { get; set; }

    /// <summary>更新说明</summary>
    public string? ReleaseNotes { get; set; }

    /// <summary>平台：win-x64 或 win32-x64</summary>
    public string? Platform { get; set; }

    /// <summary>通道，默认 stable</summary>
    public string? Channel { get; set; }
}
