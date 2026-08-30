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
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public IngestionTaskStatus Status { get; set; } = IngestionTaskStatus.Queued;

    /// <summary>本次任务涉及的文件总数</summary>
    public int TotalFiles { get; set; }

    /// <summary>已处理完（成功/跳过/失败）的文件数</summary>
    public int ProcessedFiles { get; set; }

    /// <summary>当前正在处理的文件名</summary>
    public string? CurrentFile { get; set; }

    public string? Trigger { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>整体错误信息（如 Ollama/Qdrant 连接失败）</summary>
    public string? Error { get; set; }

    /// <summary>每个文件的明细进度</summary>
    public List<IngestionFileProgress> Files { get; set; } = [];
}

/// <summary>单个文件的导入进度明细</summary>
public class IngestionFileProgress
{
    public required string FileName { get; set; }
    public required string DocumentId { get; set; }
    public IngestionFileStatus Status { get; set; } = IngestionFileStatus.Pending;

    /// <summary>成功导入后生成的切块数量</summary>
    public int ChunkCount { get; set; }

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

    public List<UploadFileResult> Results { get; set; } = [];
}

/// <summary>单个上传文件的受理结果</summary>
public class UploadFileResult
{
    public required string FileName { get; set; }

    /// <summary>accepted=已受理进入导入队列；duplicate=知识库已存在同名文档被过滤；unsupported=类型不支持</summary>
    public required string Status { get; set; }

    public string? Message { get; set; }
}

/// <summary>GET /api/knowledge/documents 列表项</summary>
public class DocumentInfo
{
    /// <summary>文档标识（即文件名）</summary>
    public required string DocumentId { get; set; }

    public required string FileName { get; set; }
    public long FileSize { get; set; }
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
    public required string Key { get; set; }
    public string? Context { get; set; }
    public int? PageNumber { get; set; }
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
}

/// <summary>向量检索单条结果</summary>
public class SearchResultItem
{
    public required string DocumentId { get; set; }
    public required string FileName { get; set; }
    public double Score { get; set; }
    public required string Text { get; set; }
    public string? Context { get; set; }
    public int? PageNumber { get; set; }
}

/// <summary>POST /api/chat 请求</summary>
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
}

/// <summary>多轮对话的历史消息</summary>
public class ChatHistoryMessage
{
    /// <summary>角色：user 或 assistant</summary>
    public required string Role { get; set; }
    public required string Content { get; set; }
}

/// <summary>POST /api/chat 响应：答案 + 引用来源</summary>
public class ChatResponse
{
    /// <summary>回答模式：knowledge_base=基于知识库回答（带引用）；model=知识库未命中，由大模型直接推理</summary>
    public required string Mode { get; set; }

    /// <summary>AI 生成的回答（Markdown 格式）</summary>
    public required string Answer { get; set; }

    /// <summary>回答引用的来源文档（按文档去重；model 模式下为空）</summary>
    public required List<ChatSource> Sources { get; set; }

    /// <summary>本次检索命中的切块明细（含相似度得分；model 模式下为空）</summary>
    public required List<SearchResultItem> References { get; set; }
}

/// <summary>回答引用的来源（按文档聚合）</summary>
public class ChatSource
{
    public required string DocumentId { get; set; }
    public required string FileName { get; set; }

    /// <summary>引用内容所在的页码（无页码概念时为空）</summary>
    public List<int> PageNumbers { get; set; } = [];
}

/// <summary>流式问答的 SSE 事件（POST /api/chat/stream，text/event-stream，每事件一行 data: JSON）</summary>
public class ChatStreamEvent
{
    /// <summary>事件类型：meta=检索命中明细；mode=回答模式（解析到首行标记后立即推送）；delta=增量文本；end=结束（附最终完整数据）</summary>
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
