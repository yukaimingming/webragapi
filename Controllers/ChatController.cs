using Microsoft.AspNetCore.Mvc;
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
    /// <summary>
    /// AI 问答：先做向量检索找相关内容，再由大模型生成回答并返回引用来源。
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
}
