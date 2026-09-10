using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebRagApi.Models;

namespace WebRagApi.Controllers;

/// <summary>客户端自动更新：返回 WPF / Win32 可用的最新包清单。</summary>
/// <remarks>
/// 未开启更新或包文件不存在时返回空清单（latestVersion 为空），客户端应静默跳过，不要当错误弹窗。
/// WPF 与 Win32 用 platform 参数区分，避免下错包。
/// </remarks>
[ApiController]
[Route("api/update")]
[Produces("application/json")]
[Tags("客户端更新")]
public sealed class UpdateController(IOptionsMonitor<UpdateOptions> options, ILogger<UpdateController> logger,
    IWebHostEnvironment env) : ControllerBase
{
    // 没有可用更新时返回空清单而不是 404：客户端把空 latestVersion 视为“无更新”静默跳过
    private static readonly UpdateManifestResponse EmptyManifest = new()
    {
        LatestVersion = "",
        PackageUrl = "",
    };

    /// <summary>获取客户端更新清单。</summary>
    /// <param name="platform">平台：省略或 win-x64 为 WPF；win32-x64 为 Win32 客户端。</param>
    /// <param name="channel">通道，默认 stable。</param>
    /// <response code="200">有更新时含版本与下载地址；无更新时 latestVersion / packageUrl 为空串。</response>
    [HttpGet("manifest")]
    [ProducesResponseType(typeof(UpdateManifestResponse), StatusCodes.Status200OK)]
    public IActionResult Manifest([FromQuery] string? platform = null, [FromQuery] string? channel = null)
    {
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

        if (packageUrl.StartsWith('/'))
            packageUrl = $"{Request.Scheme}://{Request.Host}{packageUrl}";

        if (!PackageFileExists(packageUrl))
        {
            logger.LogWarning("更新包文件不存在：{PackageUrl}，已按无可用更新返回", packageUrl);
            return Ok(EmptyManifest);
        }

        return Ok(new UpdateManifestResponse
        {
            LatestVersion = latestVersion,
            MinSupportedVersion = minSupportedVersion,
            PackageUrl = packageUrl,
            Sha256 = sha256,
            ForceUpdate = forceUpdate,
            ReleaseNotes = releaseNotes,
            Platform = string.IsNullOrWhiteSpace(platform) ? "win-x64" : platform,
            Channel = string.IsNullOrWhiteSpace(channel) ? "stable" : channel,
        });
    }

    /// <summary>更新包约定放在 wwwroot/updates 下；能映射到本地文件时做存在性校验。</summary>
    private bool PackageFileExists(string packageUrl)
    {
        var webRoot = env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot)) return true;
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri)) return true;

        if (!HttpContext.Request.Host.Value.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase) &&
            !uri.IsLoopback)
            return true;

        var relative = uri.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var file = Path.Combine(webRoot, relative);
        return System.IO.File.Exists(file);
    }
}
