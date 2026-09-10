using Microsoft.AspNetCore.Mvc;
using WebRagApi.Models;
using WebRagApi.Services;

namespace WebRagApi.Controllers;

/// <summary>
/// 知识库：文档上传、列表、详情、删除、导入进度、检索测试。
/// </summary>
/// <remarks>
/// 上传后后台异步导入（解析 → 切块 → 批量向量化入库）。
/// 去重按文件内容 SHA256；扫描件无文字层会 OCR；检索默认向量+BM25+RRF+重排。
/// </remarks>
[ApiController]
[Route("api/knowledge")]
[Produces("application/json")]
[Tags("知识库")]
public class KnowledgeController(KnowledgeService knowledgeService, SemanticSearch semanticSearch, IngestionTaskManager taskManager) : ControllerBase
{
    /// <summary>上传文档（可多文件），受理后后台导入。</summary>
    /// <remarks>
    /// 支持 pdf / doc / docx / md，以及扫描件 png/jpg/jpeg/tif/tiff/bmp。
    /// 无文字层的扫描件 PDF / 图片会自动 OCR。
    /// 按内容 SHA256 去重：内容相同即使文件名不同也会跳过（status=duplicate）；
    /// 同名但内容变了会覆盖导入。用返回的 taskId 调 GET /api/knowledge/tasks/{id} 查进度。
    /// </remarks>
    /// <param name="files">表单字段名 files（或 file），可多选。</param>
    /// <response code="200">已受理；单个文件可能是 accepted / duplicate / failed。</response>
    /// <response code="400">未上传任何文件。</response>
    [HttpPost("documents")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadDocumentsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadDocuments([FromForm] IFormFileCollection files)
    {
        if (files.Count == 0)
            return BadRequest(new { message = "请至少上传一个文件（字段名用 files 或 file）。" });

        var response = await knowledgeService.UploadAsync(files);
        return Ok(response);
    }

    /// <summary>获取知识库文档列表。</summary>
    /// <remarks>含切块数量、文件大小、上传时间。数据来自 Qdrant payload 聚合。</remarks>
    /// <response code="200">文档列表；空库返回空数组。</response>
    [HttpGet("documents")]
    [ProducesResponseType(typeof(List<DocumentInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDocuments()
    {
        var documents = await knowledgeService.ListDocumentsAsync();
        return Ok(documents);
    }

    /// <summary>查看文档详情。</summary>
    /// <param name="id">文档标识，即文件名（需 URL 编码）。</param>
    /// <response code="200">元数据、覆盖页码、前几个切块预览。</response>
    /// <response code="404">文档不存在。</response>
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

    /// <summary>删除文档及其全部向量，并删除磁盘源文件。</summary>
    /// <param name="id">文档标识，即文件名（需 URL 编码）。</param>
    /// <response code="204">已删除。</response>
    /// <response code="404">文档不存在。</response>
    [HttpDelete("documents/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDocument(string id)
    {
        var deleted = await knowledgeService.DeleteDocumentAsync(id);
        return deleted ? NoContent() : NotFound(new { message = $"文档 '{id}' 不存在。" });
    }

    /// <summary>查询一次导入任务的进度。</summary>
    /// <param name="id">任务 ID，来自上传接口返回的 taskId。</param>
    /// <response code="200">任务状态与每个文件的明细。</response>
    /// <response code="404">任务不存在。</response>
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

    /// <summary>列出最近的导入任务（含进行中的）。</summary>
    /// <remarks>页面刷新后仍可看到后台导入进度。</remarks>
    /// <response code="200">任务列表，按时间倒序。</response>
    [HttpGet("tasks")]
    [ProducesResponseType(typeof(List<IngestionTask>), StatusCodes.Status200OK)]
    public IActionResult ListTasks() => Ok(taskManager.List());

    /// <summary>检索测试：返回最相关切块及得分。</summary>
    /// <remarks>
    /// mode 取值：vector=仅向量；hybrid=向量+BM25+RRF；rerank=hybrid 后再 Cross-Encoder 形态重排（默认）。
    /// 问答接口内部默认走 rerank。当前重排尚未加载 bge-reranker 神经网络权重。
    /// </remarks>
    /// <param name="request">查询文本、TopK、可选文档过滤与检索模式。</param>
    /// <response code="200">按相关度排序的切块列表。</response>
    /// <response code="400">查询内容为空。</response>
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
