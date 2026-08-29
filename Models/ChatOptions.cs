namespace WebRagApi.Models;

/// <summary>
/// 问答策略配置（appsettings.json 的 "Chat" 节），通过 IOptions 强类型注入。
/// 命名为 AiChatOptions 以避开 Microsoft.Extensions.AI.ChatOptions 的重名冲突。
/// </summary>
public class AiChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>知识库未命中时是否允许大模型直接推理回答（智慧病历模式）的默认值</summary>
    public bool AllowModelAnswer { get; set; } = true;
}
