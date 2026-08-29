using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace WebRagApi.Services;

/// <summary>
/// 全局异常处理中间件：任何未被控制器捕获的异常都在这里统一兜底，
/// 记录日志并返回标准 ProblemDetails（RFC 7807），避免向调用方暴露堆栈等内部信息。
/// </summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "未处理异常：{Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

        var (status, title) = exception switch
        {
            Grpc.Core.RpcException ex when ex.StatusCode == Grpc.Core.StatusCode.NotFound
                => (StatusCodes.Status503ServiceUnavailable, "向量数据库集合尚不存在或服务未就绪"),
            Grpc.Core.RpcException => (StatusCodes.Status503ServiceUnavailable, "向量数据库（Qdrant）连接失败，请确认容器已启动"),
            HttpRequestException => (StatusCodes.Status503ServiceUnavailable, "外部依赖服务（Ollama/商汤接口）连接失败"),
            InvalidOperationException => (StatusCodes.Status400BadRequest, exception.Message),
            _ => (StatusCodes.Status500InternalServerError, "服务器内部错误，请查看服务端日志"),
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Instance = httpContext.Request.Path,
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
