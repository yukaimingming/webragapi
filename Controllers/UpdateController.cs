using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebRagApi.Models;

namespace WebRagApi.Controllers;

/// <summary>客户端自动更新清单接口。</summary>
[ApiController]
[Route("api/update")]
[Produces("application/json")]
public sealed class UpdateController(IOptionsMonitor<UpdateOptions> options, ILogger<UpdateController> logger,
    IWebHostEnvironment env) : ControllerBase
{
    // 没有可用更新时返回空清单而不是 404：客户端把空 latestVersion 视为“无更新”静默跳过，
    // 避免每次启动都弹“更新失败”。
    private static readonly object EmptyManifest = new { latestVersion = "", packageUrl = "" };

    /// <summary>返回 WPF/Win32 客户端可用的最新更新清单。</summary>
    [HttpGet("manifest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Manifest([FromQuery] string? platform = null, [FromQuery] string? channel = null)
    {
        // 清单要求实时性，禁止中间层缓存。
        Response.Headers.CacheControl = "no-store";

        var update = options.CurrentValue;
        var isWin32 = string.Equals(platform, "win32-x64", StringComparison.OrdinalIgnoreCase);
        var enabled = isWin32 ? update.Win32.Enabled : update.Enabled;
        var latestVersion = isWin32 ? update.Win32.LatestVersion : update.LatestVersion;
        var minSupportedVersion = isWin32 ? update.Win32.MinSupportedVersion : update.MinSupportedVersion;
        var packageUrl = isWin32 ? update.Win32.PackageUrl : update.PackageUrl;
        var sha256 = isWin32 ? update.Win32.Sha256 : update.Sha256;
        var forceUpdate = isWin32 ? update.Win32.ForceUpdate : update.ForceUpdate;
        var releaseNotes = isWin32 ? update.Win32.ReleaseNotes : update.ReleaseNotes;
        if (!enabled || string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(packageUrl))
            return Ok(EmptyManifest);

        // 客户端不支持相对地址，按当前请求补全为绝对地址。
        if (packageUrl.StartsWith('/'))
            packageUrl = $"{Request.Scheme}://{Request.Host}{packageUrl}";

        // 更新包文件不存在时同样返回空清单，避免客户端拿到 404 的下载地址报“更新失败”。
        if (!PackageFileExists(packageUrl))
        {
            logger.LogWarning("更新包文件不存在：{PackageUrl}，已按无可用更新返回", packageUrl);
            return Ok(EmptyManifest);
        }

        return Ok(new
        {
            latestVersion,
            minSupportedVersion,
            packageUrl,
            sha256,
            forceUpdate,
            releaseNotes,
            platform = string.IsNullOrWhiteSpace(platform) ? "win-x64" : platform,
            channel = string.IsNullOrWhiteSpace(channel) ? "stable" : channel
        });
    }

    /// <summary>更新包约定放在 wwwroot/updates 下；能映射到本地文件时做存在性校验。</summary>
    private bool PackageFileExists(string packageUrl)
    {
        var webRoot = env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot)) return true; // 未托管静态目录时无法校验，交给客户端
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri)) return true;

        // 非本机部署的远端地址无法校验，直接放行。
        if (!HttpContext.Request.Host.Value.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase) &&
            !uri.IsLoopback)
            return true;

        var relative = uri.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var file = Path.Combine(webRoot, relative);
        return System.IO.File.Exists(file);
    }
}
