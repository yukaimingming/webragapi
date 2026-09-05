namespace WebRagApi.Models;

/// <summary>客户端自动更新清单配置。</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    /// <summary>是否开放更新检测接口。</summary>
    public bool Enabled { get; set; }

    /// <summary>最新发布版本号，使用三段式版本号，例如 1.0.1。</summary>
    public string LatestVersion { get; set; } = "";

    /// <summary>最低支持版本号，低于此版本时客户端应强制更新。</summary>
    public string MinSupportedVersion { get; set; } = "";

    /// <summary>更新包下载地址。</summary>
    public string PackageUrl { get; set; } = "";

    /// <summary>更新包 SHA256 校验值。</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>是否强制更新。</summary>
    public bool ForceUpdate { get; set; }

    /// <summary>更新说明。</summary>
    public string ReleaseNotes { get; set; } = "";

    /// <summary>Win32 客户端独立更新清单；未配置时不影响现有 WPF 清单。</summary>
    public ClientUpdateOptions Win32 { get; set; } = new();
}

/// <summary>指定客户端平台的更新配置。</summary>
public sealed class ClientUpdateOptions
{
    public bool Enabled { get; set; }
    public string LatestVersion { get; set; } = "";
    public string MinSupportedVersion { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool ForceUpdate { get; set; }
    public string ReleaseNotes { get; set; } = "";
}
