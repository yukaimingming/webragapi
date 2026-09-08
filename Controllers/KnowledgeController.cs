using Microsoft.AspNetCore.Mvc;
using WebRagApi.Models;
using WebRagApi.Services;

namespace WebRagApi.Controllers;

/// <summary>
/// 知识库管理接口：文档上传（增量去重）、列表、详情、删除、导入进度查询、向量检索测试。
/// </summary>
[ApiController]
[Route("api/knowledge")]
[Produces("application/json")]
public class KnowledgeController(KnowledgeService knowledgeService, SemanticSearch semanticSearch, IngestionTaskManager taskManager) : ControllerBase
{
    /// <summary>
    /// 上传文档（支持 pdf / doc / docx / md，以及扫描件 png/jpg/jpeg/tif/tiff/bmp，可多文件）。
    /// 无文字层的扫描件 PDF / 图片会自动 OCR。
    /// 同名文档已在知识库中的会被自动过滤；上传后后台异步导入，用返回的 taskId 查询进度。
    /// </summary>
    [HttpPost("documents")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadDocumentsResponse), StatusCodes.Status200OK)]
    public IActionResult UploadDocuments([FromForm] IFormFileCollection files)
    {
        if (files.Count == 0)
            return BadRequest(new { message = "请至少上传一个文件（字段名用 files 或 file）。" });

        var response = knowledgeService.Upload(files);
        return Ok(response);
    }

    /// <summary>获取知识库文档列表（含切块数量、文件大小、上传时间）</summary>
    [HttpGet("documents")]
    [ProducesResponseType(typeof(List<DocumentInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDocuments()
    {
        var documents = await knowledgeService.ListDocumentsAsync();
        return Ok(documents);
    }

    /// <summary>查看文档详情（元数据 + 覆盖页码 + 前几个切块预览）</summary>
    [HttpGet("documents/{id}")]
    [ProducesResponseType(typeof(DocumentDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDocument(string id)
    {
        var detail = await knowledgeService.GetDocumentAsync(id);
        return detail is null
            ? NotFound(new { message = $"文档 '{id}' 不存在。" })
            : Ok(detail);
    }

    /// <summary>删除文档：清除该文档的全部向量数据，并删除磁盘上的源文件</summary>
    [HttpDelete("documents/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDocument(string id)
    {
        var deleted = await knowledgeService.DeleteDocumentAsync(id);
        return deleted ? NoContent() : NotFound(new { message = $"文档 '{id}' 不存在。" });
    }

    /// <summary>查询文档导入进度（taskId 来自上传接口的返回值）</summary>
    [HttpGet("tasks/{id}")]
    [ProducesResponseType(typeof(IngestionTask), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetTask(string id)
    {
        var task = taskManager.Get(id);
        return task is null
            ? NotFound(new { message = $"任务 '{id}' 不存在。" })
            : Ok(task);
    }

    /// <summary>列出最近的导入任务（含进行中的），页面刷新后也能看到导入进度</summary>
    [HttpGet("tasks")]
    [ProducesResponseType(typeof(List<IngestionTask>), StatusCodes.Status200OK)]
    public IActionResult ListTasks() => Ok(taskManager.List());

    /// <summary>
    /// 检索测试：默认向量 + BM25 + RRF，再 Cross-Encoder 重排。
    /// mode=vector 仅向量；hybrid 融合两路；rerank 融合后再重排。
    /// </summary>
    [HttpPost("search")]
    [ProducesResponseType(typeof(List<SearchResultItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromBody] SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { message = "查询内容不能为空。" });

        int topK = Math.Clamp(request.TopK <= 0 ? 5 : request.TopK, 1, 50);
        var results = await semanticSearch.SearchWithScoreAsync(request.Query, request.DocumentId, topK, request.Mode);
        return Ok(results);
    }
}
