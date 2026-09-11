using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using WebRagApi.Models;

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
    SenseNovaCompletionService senseNova,
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
    /// 统一系统提示词：先看知识库能不能答（含用户上传的非医疗文档），能答必须走 [知识库]；
    /// 只有知识库答不了时才区分医疗推理 / 非医疗拒答。首行标记解析后会剥掉再返回前端。
    /// </summary>
    private const string SystemPrompt = """
        你是一个接入知识库的医疗健康智能问答助手。回答用户问题时严格按下面的优先级，不得跳步：

        一、先判断【知识库上下文】是否能实际用于回答用户问题（主题一致、材料能支撑答案）。
        注意：领域相同不等于相关（例如上下文讲高血压、问题问糖尿病，就是不相关）。
        注意：用户上传进知识库的材料不限于医疗，公文、人事批复、教材、技术手册等只要能回答该问题，就算相关。

        二、如果相关：必须基于知识库内容回答。这是最高优先级。
        - 回答第一行只输出标记：[知识库]
        - 禁止以“只能回答医疗健康相关问题”拒绝。知识库里有答案就必须答，与是否医疗无关。
        - 正例：问辞职批复，上下文有卫生局文件 → [知识库]；问量子比特，上下文有量子计算手册 → [知识库]。
        - 不要编造知识库之外的关键事实；上下文不完整时可以补充，但要说明哪些是知识库之外的补充。
        - 回答末尾用一句话注明依据的文档名和页码。

        三、仅当知识库上下文无法回答该问题时，才看问题本身是不是医疗健康：
        - 属于医疗健康（疾病、症状、检查、用药、护理、公共卫生等）：基于自身医学知识回答，第一行只输出 [模型]；涉及用药、剂量、诊疗方案时，必须提醒“仅供参考，不能替代执业医师的诊断与处方”，必要时建议就医。
        - 与医疗健康完全无关（且知识库也答不了）：礼貌说明你主要回答医疗健康问题，以及知识库中已有文档的内容；第一行输出 [模型]。此时才允许说范围限制。

        四、通用要求：
        - 回答使用简体中文，条理清晰，可以用 Markdown 组织要点。
        - 除第一行标记外，正文中不要出现"知识库未命中""没有找到相关内容"这类字眼（[知识库] 模式下上下文确实缺少部分信息时除外）。

        五、生成病历时（用户提供病情要点、要求整理/生成病历、或像门诊文书那样描述患者）：
        - 第一行仍然只输出 [知识库] 或 [模型]。
        - 从第二行起只输出病历正文，使用以下 Markdown 二级标题，顺序固定：
          ## 主诉
          ## 现病史
          ## 既往史
          ## 个人史
          ## 家族史
          ## 体格检查
          ## 辅助检查
          ## 初步诊断
          ## 处置建议
          ## 质控提示
        - 知识库相关内容优先写入初步诊断、处置建议和质控提示；未提供的体征写“待测”。
        """;

    /// <summary>严格 RAG 模式（关闭大模型兜底）的系统提示词：只允许基于知识库回答</summary>
    private const string StrictKnowledgeBasePrompt = """
        你是一个严谨的知识库问答助手。请严格遵守以下规则：
        1. 只根据下面提供的【知识库上下文】回答用户问题，不要编造上下文之外的内容。
        2. 如果上下文中没有足够的信息回答问题，请直接说明"知识库中没有找到相关内容"，不要猜测。
        3. 回答使用简体中文，条理清晰，可以用 Markdown 列表组织要点。
        4. 回答末尾请用一句话说明该答案依据了哪些文档（引用文档名和页码）。
        """;

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

    private static List<object> ToLlmMessages(List<ChatMessage> messages)
    {
        var list = new List<object>(messages.Count);
        foreach (var m in messages)
        {
            var role = m.Role == ChatRole.System ? "system"
                : m.Role == ChatRole.Assistant ? "assistant"
                : "user";
            list.Add(new Dictionary<string, object?> { ["role"] = role, ["content"] = m.Text ?? "" });
        }
        return list;
    }

    /// <summary>
    /// 来源按文档聚合（去重，附页码列表）。
    /// 只取与最高分切块接近的文档作为回答依据（分差 ≤0.15 且分数 ≥0.5）：
    /// 向量检索召回的 top-K 里常混有低分的无关文档（同类中文文本基线相似度不低），
    /// 它们只是"凑数"命中，不能算作回答依据。
    /// </summary>
    public static List<ChatSource> AggregateSources(List<SearchResultItem> references)
    {
        if (references.Count == 0)
            return [];

        // 引用过滤用向量余弦（0~1），不用 RRF/重排分，避免尺子错位
        var cutoff = Math.Max(0.5, references.Max(CitationScore) - 0.15);
        return references
            .Where(r => CitationScore(r) >= cutoff)
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

    // ================= 流式问答（SSE） =================

    /// <summary>
    /// 流式版问答：事件序列为 meta（检索命中）→ mode（解析到首行标记后立即推送）→ delta（增量文本）→ end（最终完整数据）。
    /// 首行 [知识库]/[模型] 标记会先缓冲、解析并剥离，不会出现在推给前端的正文里。
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> ChatStreamAsync(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int topK = Math.Clamp(request.TopK <= 0 ? 8 : request.TopK, 1, 20);
        bool allowModelAnswer = request.AllowModelAnswer ?? _allowModelAnswerDefault;

        // 1. 检索并推送命中明细
        var references = await semanticSearch.SearchWithScoreAsync(request.Question, request.DocumentId, topK, cancellationToken);
        yield return new ChatStreamEvent { Type = "meta", References = references };

        string mode = "model";
        var buffer = new System.Text.StringBuilder();        // 待推送的未决文本
        var fullAnswer = new System.Text.StringBuilder();    // 完整回答（end 事件携带）

        if (!allowModelAnswer)
        {
            // 严格 RAG 模式：不做标记协议，直接按知识库模式流式输出
            mode = "knowledge_base";
            yield return new ChatStreamEvent { Type = "mode", Mode = mode };

            if (references.Count == 0)
            {
                var emptyText = "知识库中还没有找到与该问题相关的内容，也无法启用大模型推理（allowModelAnswer=false）。请先上传相关文档。";
                yield return new ChatStreamEvent { Type = "delta", Text = emptyText };
                yield return new ChatStreamEvent { Type = "end", Mode = mode, Text = emptyText, Sources = [], References = [] };
                yield break;
            }

            var strictMessages = BuildMessages(StrictKnowledgeBasePrompt, references, request);
            await foreach (var update in StreamModelAsync(strictMessages, request, cancellationToken))
            {
                if (update.Kind == SenseNovaDeltaKind.Reasoning)
                {
                    yield return new ChatStreamEvent { Type = "reasoning", Text = update.Text };
                    continue;
                }
                var piece = update.Text;
                if (string.IsNullOrEmpty(piece))
                    continue;
                buffer.Append(piece);
                yield return new ChatStreamEvent { Type = "delta", Text = piece };
            }

            logger.LogInformation("RAG 流式问答完成（严格知识库模式）：问题='{Question}'。", request.Question);
            yield return new ChatStreamEvent
            {
                Type = "end",
                Mode = mode,
                Text = buffer.ToString(),
                Sources = AggregateSources(references),
                References = references,
            };
            yield break;
        }

        // 2. 智慧病历模式：流式输出，先缓冲首行解析 [知识库]/[模型] 标记
        var smartMessages = BuildMessages(SystemPrompt, references, request);
        bool modeResolved = false;
        await foreach (var update in StreamModelAsync(smartMessages, request, cancellationToken))
        {
            if (update.Kind == SenseNovaDeltaKind.Reasoning)
            {
                yield return new ChatStreamEvent { Type = "reasoning", Text = update.Text };
                continue;
            }
            var piece = update.Text;
            if (string.IsNullOrEmpty(piece))
                continue;

            buffer.Append(piece);

            if (!modeResolved)
            {
                var text = buffer.ToString().TrimStart();
                if (text.Length == 0)
                    continue;

                if (IsIncompleteMarkerPrefix(text))
                    continue;

                var parsed = ParseModeAndAnswer(text, references);
                mode = parsed.Mode;
                modeResolved = true;
                yield return new ChatStreamEvent { Type = "mode", Mode = mode };
                if (parsed.Answer.Length > 0)
                {
                    fullAnswer.Append(parsed.Answer);
                    yield return new ChatStreamEvent { Type = "delta", Text = parsed.Answer };
                }
                buffer.Clear();
            }
            else if (buffer.Length > 0)
            {
                fullAnswer.Append(buffer);
                yield return new ChatStreamEvent { Type = "delta", Text = buffer.ToString() };
                buffer.Clear();
            }
        }

        if (!modeResolved)
        {
            var parsed = ParseModeAndAnswer(buffer.ToString(), references);
            mode = parsed.Mode;
            modeResolved = true;
            yield return new ChatStreamEvent { Type = "mode", Mode = mode };
            if (parsed.Answer.Length > 0)
            {
                fullAnswer.Append(parsed.Answer);
                yield return new ChatStreamEvent { Type = "delta", Text = parsed.Answer };
            }
            buffer.Clear();
        }

        if (buffer.Length > 0)
        {
            fullAnswer.Append(buffer);
            yield return new ChatStreamEvent { Type = "delta", Text = buffer.ToString() };
        }

        logger.LogInformation("RAG 流式问答完成（{Mode}）：问题='{Question}'，检索召回 {Count} 个切块。",
            mode, request.Question, references.Count);
        yield return new ChatStreamEvent
        {
            Type = "end",
            Mode = mode,
            Text = fullAnswer.ToString(), // 完整回答（前端也可以自己聚合 delta，这里作为兜底）
            Sources = mode == "knowledge_base" ? AggregateSources(references) : [],
            References = mode == "knowledge_base" ? references : [],
        };
    }

    private IAsyncEnumerable<SenseNovaDelta> StreamModelAsync(
        List<ChatMessage> messages, ChatRequest request, CancellationToken cancellationToken)
    {
        var thinking = request.Thinking == true;
        return senseNova.CompleteStreamAsync(ToLlmMessages(messages), thinking, request.ReasoningEffort, cancellationToken);
    }

    /// <summary>标记可在行首，同一行后面的正文保留；未输出标记时用向量余弦兜底。</summary>
    private (string Mode, string Answer) ParseModeAndAnswer(string raw, List<SearchResultItem> references)
    {
        var text = (raw ?? string.Empty).TrimStart();
        if (text.StartsWith(KnowledgeBaseMarker, StringComparison.Ordinal))
            return ("knowledge_base", StripMarker(text, KnowledgeBaseMarker));
        if (text.StartsWith(ModelMarker, StringComparison.Ordinal))
            return ("model", StripMarker(text, ModelMarker));

        var maxScore = references.Count > 0 ? references.Max(CitationScore) : 0;
        var fallback = maxScore >= 0.45 ? "knowledge_base" : "model";
        logger.LogWarning("模型回答缺少模式标记，按向量分兜底判定为 {Mode}（最高分 {MaxScore}）。", fallback, maxScore);
        return (fallback, text);
    }

    private static string StripMarker(string text, string marker)
        => text[marker.Length..].TrimStart('\r', '\n', ' ', '\t');

    private static bool IsIncompleteMarkerPrefix(string text)
        => KnowledgeBaseMarker.StartsWith(text, StringComparison.Ordinal)
           || ModelMarker.StartsWith(text, StringComparison.Ordinal);

    /// <summary>引用/兜底判定用向量余弦；没有向量分时才退回最终 Score。</summary>
    private static double CitationScore(SearchResultItem r) => r.VectorScore ?? r.Score;
}
