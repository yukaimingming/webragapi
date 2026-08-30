using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using WebRagApi.Models;
using WebRagApi.Services;

namespace WebRagApi.Controllers;

/// <summary>
/// AI 问答接口：基于知识库的 RAG 问答，返回回答与引用来源。
/// </summary>
[ApiController]
[Route("api/chat")]
[Produces("application/json")]
public class ChatController(ChatService chatService) : ControllerBase
{
    // camelCase + 中文不转义，SSE 事件里中文直接可读
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// AI 问答：先做向量检索找相关内容，再由大模型生成回答并返回引用来源（一次性返回完整 JSON）。
    /// </summary>
    /// <param name="request">问题与可选参数（TopK、文档过滤、多轮历史）</param>
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

    /// <summary>
    /// AI 流式问答（SSE）：响应为 text/event-stream，按事件推送检索结果、回答模式与增量文本。
    /// 事件序列：meta（检索命中明细）→ mode（回答模式）→ delta（增量文本，多条）→ end（结束，附最终完整数据）。
    /// </summary>
    [HttpPost("stream")]
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
            buffer.Append("data: ").Append(JsonSerializer.Serialize(evt, SseJsonOptions)).Append("\n\n");
            await Response.WriteAsync(buffer.ToString(), cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            buffer.Clear();
        }
    }
}
