namespace WebRagApi.Models;

/// <summary>
/// 聊天模型与问答策略配置（appsettings.json 的 "Chat" 节），通过 IOptions 强类型注入。
/// 命名为 AiChatOptions 以避开 Microsoft.Extensions.AI.ChatOptions 的重名冲突。
/// API Key / Endpoint / Model 只放后端，前端不再持有商汤凭证。
/// </summary>
public class AiChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>OpenAI 兼容网关地址，商汤为 https://token.sensenova.cn/v1</summary>
    public string Endpoint { get; set; } = "https://token.sensenova.cn/v1";

    /// <summary>商汤 API Key。优先从 user-secrets / 环境变量 Chat__ApiKey 读取</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>聊天模型名</summary>
    public string Model { get; set; } = "sensenova-6.8-flash-lite";

    /// <summary>采样温度</summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>非流式调用超时（毫秒）。流式调用不设整体超时</summary>
    public int TimeoutMs { get; set; } = 120000;

    /// <summary>是否改写商汤空 finish_reason，避免 OpenAI SDK 解析失败</summary>
    public bool RewriteFinishReason { get; set; } = true;

    /// <summary>知识库未命中时是否允许大模型直接推理回答（智慧病历模式）的默认值</summary>
    public bool AllowModelAnswer { get; set; } = true;
}
