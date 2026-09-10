using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WebRagApi.Models;
using WebRagApi.Services;

namespace WebRagApi.Controllers;

/// <summary>
/// AI 问答：基于知识库的 RAG（一次性 JSON 与 SSE 流式）。
/// </summary>
/// <remarks>
/// 默认检索管道：向量 + BM25 → RRF 融合 → Cross-Encoder 形态重排。
/// 知识库能答则走 knowledge_base（含用户上传的非医疗文档）；答不了时医疗问题由模型推理，非医疗则提示范围限制。
/// </remarks>
[ApiController]
[Route("api/chat")]
[Produces("application/json")]
[Tags("AI 问答")]
public class ChatController(ChatService chatService) : ControllerBase
{
    // camelCase + 中文不转义，SSE 事件里中文直接可读
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>前端可公开读取的模型能力（不含 API Key / Endpoint）。</summary>
    /// <remarks>用于 Demo / 客户端判断是否展示深度思考开关。</remarks>
    /// <response code="200">返回模型名与推理等级列表。</response>
    [HttpGet("config")]
    [ProducesResponseType(typeof(ChatConfigResponse), StatusCodes.Status200OK)]
    public IActionResult Config([FromServices] IOptions<AiChatOptions> options)
    {
        var cfg = options.Value;
        return Ok(new ChatConfigResponse
        {
            Model = cfg.Model,
            ThinkingSupported = true,
            ReasoningEfforts = ["low", "medium", "high"],
        });
    }

    /// <summary>AI 问答：检索相关切块后由大模型生成回答，一次性返回完整 JSON。</summary>
    /// <param name="request">问题与可选参数（TopK、文档过滤、多轮历史、深度思考）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回答、模式（knowledge_base / model）、引用来源与检索明细。</returns>
    /// <response code="200">问答成功。</response>
    /// <response code="400">问题为空。</response>
    [HttpPost]
    [ProducesResponseType(typeof(ChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { message = "问题不能为空。" });

        var response = await chatService.ChatAsync(request, cancellationToken);
        return Ok(response);
    }

    /// <summary>AI 流式问答（SSE）。</summary>
    /// <remarks>
    /// 响应 Content-Type 为 text/event-stream。事件顺序：
    /// meta（检索命中）→ mode（回答模式）→ reasoning（可选思考增量）→ delta（正文增量，多条）→ end（完整结果）。
    /// </remarks>
    /// <param name="request">与非流式问答相同的请求体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">SSE 事件流。</response>
    /// <response code="400">问题为空。</response>
    [HttpPost("stream")]
    [Produces("text/event-stream", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task StreamChat([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { message = "问题不能为空。" }, cancellationToken);
            return;
        }

        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        var buffer = new StringBuilder();
        await foreach (var evt in chatService.ChatStreamAsync(request, cancellationToken))
        {
            buffer.Append("event: ").Append(evt.Type).Append('\n');
            buffer.Append("data: ").Append(JsonSerializer.Serialize(evt, SseJsonOptions)).Append("\n\n");
            await Response.WriteAsync(buffer.ToString(), cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            buffer.Clear();
        }
    }
}
