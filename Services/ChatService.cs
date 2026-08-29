using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using WebRagApi.Models;
// 消除与 Microsoft.Extensions.AI.ChatResponse 的重名歧义：本项目 DTO 叫 ChatResponse
using ChatResponse = WebRagApi.Models.ChatResponse;

namespace WebRagApi.Services;

/// <summary>
/// RAG 问答服务（智慧病历模式）：
/// 检索到的切块始终交给大模型，由模型判断相关性后二选一：
/// 1. 上下文与问题真正相关 → 基于知识库回答（回答首行标记 [知识库]），返回引用来源；
/// 2. 不相关（同主题不等于能回答问题）→ 医疗健康问题由模型基于自身医学知识直接推理（首行标记 [模型]），
///    非医疗问题礼貌拒答。
/// 说明：不用余弦相似度阈值判定"是否命中"——医学文本之间的基线相似度普遍偏高（0.5+），
/// 向量分数只能做召回排序，做不了相关性判断，语义相关性由 LLM 判断更可靠。
/// </summary>
public class ChatService(
    IChatClient chatClient,
    SemanticSearch semanticSearch,
    IOptions<AiChatOptions> chatOptions,
    ILogger<ChatService> logger)
{
    /// <summary>知识库未命中时是否允许大模型直接回答的全局默认值（请求参数 allowModelAnswer 可按次覆盖）</summary>
    private readonly bool _allowModelAnswerDefault = chatOptions.Value.AllowModelAnswer;

    /// <summary>回答首行的模式标记 → 接口 mode 字段</summary>
    private const string KnowledgeBaseMarker = "[知识库]";
    private const string ModelMarker = "[模型]";

    /// <summary>
    /// 统一系统提示词：模型自行判断知识库上下文是否与问题相关，决定回答模式。
    /// 首行标记用于后端识别回答模式（解析后会把标记行剥掉再返回给前端）。
    /// </summary>
    private const string SystemPrompt = """
        你是一个接入知识库的医疗健康智能问答助手。回答用户问题时严格遵守以下规则：

        一、先判断【知识库上下文】中的内容是否与用户问题真正相关（主题一致、内容能实际用于回答问题）。
        注意：领域相同不等于相关，例如上下文讲高血压、问题问糖尿病，就是不相关。

        二、如果相关：基于知识库内容回答。
        - 回答第一行只输出标记：[知识库]
        - 不要编造知识库之外的关键事实；上下文不完整时可以补充，但要说明哪些内容是知识库之外的补充。
        - 回答末尾用一句话注明依据的文档名和页码。

        三、如果不相关：
        - 若问题属于医疗健康领域（疾病、症状、检查、用药、护理、公共卫生等）：基于你自身的医学知识直接回答，回答第一行只输出标记：[模型]；涉及用药、剂量、诊疗方案时，必须提醒"仅供参考，不能替代执业医师的诊断与处方"，必要时建议就医。
        - 若问题与医疗健康完全无关：礼貌说明你只能回答医疗健康相关的问题，第一行同样输出标记：[模型]。

        四、通用要求：
        - 回答使用简体中文，条理清晰，可以用 Markdown 组织要点。
        - 除第一行标记外，正文中不要出现"知识库未命中""没有找到相关内容"这类字眼（[知识库] 模式下上下文确实缺少部分信息时除外）。
        """;

    /// <summary>严格 RAG 模式（关闭大模型兜底）的系统提示词：只允许基于知识库回答</summary>
    private const string StrictKnowledgeBasePrompt = """
        你是一个严谨的知识库问答助手。请严格遵守以下规则：
        1. 只根据下面提供的【知识库上下文】回答用户问题，不要编造上下文之外的内容。
        2. 如果上下文中没有足够的信息回答问题，请直接说明"知识库中没有找到相关内容"，不要猜测。
        3. 回答使用简体中文，条理清晰，可以用 Markdown 列表组织要点。
        4. 回答末尾请用一句话说明该答案依据了哪些文档（引用文档名和页码）。
        """;

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        int topK = Math.Clamp(request.TopK <= 0 ? 10 : request.TopK, 1, 20);
        bool allowModelAnswer = request.AllowModelAnswer ?? _allowModelAnswerDefault;

        // 1. 向量检索相关切块（只做召回，不做相关性判定）
        var references = await semanticSearch.SearchWithScoreAsync(request.Question, request.DocumentId, topK);

        // 2. 严格 RAG 模式（请求显式关闭大模型兜底）：只允许基于知识库回答
        if (!allowModelAnswer)
        {
            var strictAnswer = references.Count == 0
                ? "知识库中还没有找到与该问题相关的内容，也无法启用大模型推理（allowModelAnswer=false）。请先上传相关文档。"
                : await GenerateAsync(BuildMessages(StrictKnowledgeBasePrompt, references, request), cancellationToken);
            logger.LogInformation("RAG 问答完成（严格知识库模式）：问题='{Question}'，检索召回 {Count} 个切块。", request.Question, references.Count);
            return new ChatResponse
            {
                Mode = "knowledge_base",
                Answer = strictAnswer,
                Sources = AggregateSources(references),
                References = references,
            };
        }

        // 3. 组装上下文并让模型回答（模型自行判断相关性，首行输出模式标记）
        var messages = BuildMessages(SystemPrompt, references, request);
        var rawAnswer = await GenerateAsync(messages, cancellationToken);

        // 4. 解析首行标记 → 回答模式；标记行剥掉后再返回
        // 注意先去掉开头的空行/空白，模型常在标记前输出换行
        string mode;
        string answer;
        var trimmed = rawAnswer.TrimStart();
        var firstLineEnd = trimmed.IndexOf('\n');
        var firstLine = (firstLineEnd < 0 ? trimmed : trimmed[..firstLineEnd]).Trim();
        if (firstLine.Contains(KnowledgeBaseMarker, StringComparison.Ordinal))
        {
            mode = "knowledge_base";
            answer = firstLineEnd < 0 ? string.Empty : trimmed[(firstLineEnd + 1)..].TrimStart();
        }
        else if (firstLine.Contains(ModelMarker, StringComparison.Ordinal))
        {
            mode = "model";
            answer = firstLineEnd < 0 ? string.Empty : trimmed[(firstLineEnd + 1)..].TrimStart();
        }
        else
        {
            // 模型没按格式输出标记：按相似度兜底判定，回答原样返回
            var maxScore = references.Count > 0 ? references.Max(r => r.Score) : 0;
            mode = maxScore >= 0.45 ? "knowledge_base" : "model";
            answer = trimmed;
            logger.LogWarning("模型回答缺少模式标记，按相似度兜底判定为 {Mode}（最高分 {MaxScore}）。", mode, maxScore);
        }

        logger.LogInformation("RAG 问答完成（{Mode}）：问题='{Question}'，检索召回 {Count} 个切块。",
            mode, request.Question, references.Count);

        return new ChatResponse
        {
            Mode = mode,
            Answer = answer,
            // 大模型推理模式没有知识库引用
            Sources = mode == "knowledge_base" ? AggregateSources(references) : [],
            References = mode == "knowledge_base" ? references : [],
        };
    }

    /// <summary>用知识库上下文构建消息（标注来源文档与页码，便于模型在回答中引用）</summary>
    private static List<ChatMessage> BuildMessages(string systemPrompt, List<SearchResultItem> chunks, ChatRequest request)
    {
        var contextBuilder = new System.Text.StringBuilder();
        if (chunks.Count == 0)
        {
            contextBuilder.AppendLine("（知识库为空或没有检索到任何内容）");
        }
        else
        {
            foreach (var (item, index) in chunks.Select((v, i) => (v, i + 1)))
            {
                var source = item.Context is { Length: > 0 } ? $"【{item.FileName} | {item.Context}】" : $"【{item.FileName}】";
                var page = item.PageNumber is not null ? $"（第 {item.PageNumber} 页）" : "";
                contextBuilder.AppendLine($"[{index}] {source}{page}");
                contextBuilder.AppendLine(item.Text);
                contextBuilder.AppendLine();
            }
        }

        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        if (request.History is { Count: > 0 })
        {
            foreach (var h in request.History.TakeLast(6))
                messages.Add(new ChatMessage(h.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, h.Content));
        }
        messages.Add(new ChatMessage(ChatRole.User,
            $"""
            【知识库上下文】
            {contextBuilder}

            【用户问题】
            {request.Question}
            """));
        return messages;
    }

    private async Task<string> GenerateAsync(List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var answer = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        return answer.Text ?? string.Empty;
    }

    /// <summary>来源按文档聚合（去重，附页码列表）</summary>
    private static List<ChatSource> AggregateSources(List<SearchResultItem> references)
        => references
            .GroupBy(r => r.DocumentId)
            .Select(g => new ChatSource
            {
                DocumentId = g.Key,
                FileName = g.First().FileName,
                PageNumbers = g.Where(r => r.PageNumber.HasValue)
                    .Select(r => r.PageNumber!.Value).Distinct().OrderBy(v => v).ToList(),
            })
            .ToList();
}
