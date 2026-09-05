using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebRagApi.Models;

namespace WebRagApi.Controllers;

/// <summary>客户端自动更新清单接口。</summary>
[ApiController]
[Route("api/update")]
[Produces("application/json")]
public sealed class UpdateController(IOptionsMonitor<UpdateOptions> options) : ControllerBase
{
    /// <summary>返回 WPF/Win32 客户端可用的最新更新清单。</summary>
    [HttpGet("manifest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Manifest([FromQuery] string? platform = null, [FromQuery] string? channel = null)
    {
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
            return NotFound(new { message = "当前没有可用的客户端更新。" });

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
}
