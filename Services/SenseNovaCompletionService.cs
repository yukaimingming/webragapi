using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WebRagApi.Models;

namespace WebRagApi.Services;

public enum SenseNovaDeltaKind
{
    Reasoning,
    Content
}

public readonly record struct SenseNovaDelta(SenseNovaDeltaKind Kind, string Text);

public class SenseNovaCompletionResult
{
    public string Text { get; init; } = "";
    public string Reasoning { get; init; } = "";
}

/// <summary>
/// 按商汤 SenseNova OpenAI 兼容协议调用 /chat/completions。
/// 深度思考：thinking=false 时 reasoning_effort=none；开启时传 low/medium/high，
/// 并附 thinking.type=enabled（见商汤文档与项目 scripts/verify-thinking.mjs）。
/// 流式增量同时识别 delta.reasoning / delta.reasoning_content / delta.content。
/// </summary>
public class SenseNovaCompletionService(
    IHttpClientFactory httpClientFactory,
    IOptions<AiChatOptions> options,
    ILogger<SenseNovaCompletionService> logger)
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions JsonIn = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 按前端「是否深度思考 + 推理等级」映射商汤 reasoning_effort。
    /// 关闭思考时必须显式传 none，否则商汤默认 medium，会一直吐 reasoning。
    /// </summary>
    public static string NormalizeEffort(string? value, bool thinking)
    {
        if (!thinking) return "none";
        var v = (value ?? "").Trim().ToLowerInvariant();
        return v is "low" or "medium" or "high" ? v : "medium";
    }

    public async Task<SenseNovaCompletionResult> CompleteAsync(
        IReadOnlyList<object> messages,
        bool thinking,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        await foreach (var delta in CompleteStreamAsync(messages, thinking, reasoningEffort, cancellationToken))
        {
            if (delta.Kind == SenseNovaDeltaKind.Reasoning) reasoning.Append(delta.Text);
            else text.Append(delta.Text);
        }
        return new SenseNovaCompletionResult { Text = text.ToString(), Reasoning = reasoning.ToString() };
    }


    /// <summary>
    /// 调用商汤 SenseNova 流式接口，按事件推送增量文本与 reasoning。
    /// 事件序列：delta.reasoning / delta.reasoning_content → delta.content。
    /// </summary>
    /// <param name="messages">提示词</param>
    /// <param name="thinking">是否推理</param>
    /// <param name="reasoningEffort">推理等级</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async IAsyncEnumerable<SenseNovaDelta> CompleteStreamAsync(
        IReadOnlyList<object> messages,
        bool thinking,
        string? reasoningEffort,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cfg = options.Value;
        var effort = NormalizeEffort(reasoningEffort, thinking);
        var url = ChatCompletionsUrl(cfg.Endpoint);
        var payload = new SenseNovaRequest
        {
            Model = cfg.Model,
            Messages = messages,
            Stream = true,
            Temperature = cfg.Temperature,
            ReasoningEffort = effort,
            Thinking = thinking
                ? new SenseNovaThinking { Type = "enabled", ReasoningEffort = effort }
                : null
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOut), Encoding.UTF8, "application/json");

        var client = httpClientFactory.CreateClient("SenseNova");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw MapUpstreamError(response.StatusCode, errText);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var reasoningAcc = "";
        var gotContent = false;
        var gotReasoning = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var payloadLine = trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? trimmed[5..].Trim()
                : trimmed;
            if (payloadLine is "[DONE]" or "") continue;

            JsonElement json;
            try
            {
                json = JsonSerializer.Deserialize<JsonElement>(payloadLine, JsonIn);
            }
            catch
            {
                continue;
            }

            //读取推理内容
            var think = ExtractReasoning(json);
            if (thinking && !string.IsNullOrEmpty(think))
            {
                var next = think.StartsWith(reasoningAcc, StringComparison.Ordinal) ? think : reasoningAcc + think;
                var chunk = next[reasoningAcc.Length..];
                reasoningAcc = next;
                if (chunk.Length > 0)
                {
                    gotReasoning = true;
                    yield return new SenseNovaDelta(SenseNovaDeltaKind.Reasoning, chunk);
                }
            }

            //读取最后内容
            var content = ExtractContent(json);
            if (!string.IsNullOrEmpty(content))
            {
                gotContent = true;
                yield return new SenseNovaDelta(SenseNovaDeltaKind.Content, content);
            }
        }

        if (!gotContent && !gotReasoning)
        {
            logger.LogWarning("商汤流式响应未解析到正文，回退非流式调用。");
            var fallback = await CompleteOnceAsync(messages, thinking, effort, cancellationToken);
            if (thinking && !string.IsNullOrEmpty(fallback.Reasoning))
                yield return new SenseNovaDelta(SenseNovaDeltaKind.Reasoning, fallback.Reasoning);
            if (!string.IsNullOrEmpty(fallback.Text))
                yield return new SenseNovaDelta(SenseNovaDeltaKind.Content, fallback.Text);
        }
    }

    private async Task<SenseNovaCompletionResult> CompleteOnceAsync(
        IReadOnlyList<object> messages,
        bool thinking,
        string effort,
        CancellationToken cancellationToken)
    {
        var cfg = options.Value;
        var payload = new SenseNovaRequest
        {
            Model = cfg.Model,
            Messages = messages,
            Stream = false,
            Temperature = cfg.Temperature,
            ReasoningEffort = effort,
            Thinking = thinking
                ? new SenseNovaThinking { Type = "enabled", ReasoningEffort = effort }
                : null
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl(cfg.Endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOut), Encoding.UTF8, "application/json");

        var client = httpClientFactory.CreateClient("SenseNova");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, cfg.TimeoutMs)));
        using var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw MapUpstreamError(response.StatusCode, body);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return new SenseNovaCompletionResult
        {
            Text = ExtractContent(doc.RootElement) ?? "",
            Reasoning = thinking ? ExtractReasoning(doc.RootElement) ?? "" : ""
        };
    }

    public static string ChatCompletionsUrl(string? baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) return "https://token.sensenova.cn/v1/chat/completions";
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
        return b + "/chat/completions";
    }

    private static InvalidOperationException MapUpstreamError(System.Net.HttpStatusCode status, string errText)
    {
        var snippet = (errText ?? "").Trim();
        if (snippet.Length > 240) snippet = snippet[..240];
        if ((int)status == 429)
            return new InvalidOperationException("大模型服务繁忙 (429 Rate Limit / Server Busy)，请稍候重试");
        if (status == System.Net.HttpStatusCode.Unauthorized)
            return new InvalidOperationException("大模型 API Key 无效或已过期 (401 Unauthorized)");
        return new InvalidOperationException($"模型接口返回错误 ({(int)status}): {snippet}");
    }

    private static string ExtractContent(JsonElement json)
    {
        if (TryGetChoice(json, out var choice))
        {
            if (choice.TryGetProperty("delta", out var delta))
            {
                var t = AsText(GetProp(delta, "content")) + AsText(GetProp(delta, "text"));
                if (t.Length > 0) return t;
            }
            if (choice.TryGetProperty("message", out var message))
            {
                var t = AsText(GetProp(message, "content")) + AsText(GetProp(message, "text"));
                if (t.Length > 0) return t;
            }
        }
        return AsText(GetProp(json, "content")) + AsText(GetProp(json, "output_text"));
    }

    private static string ExtractReasoning(JsonElement json)
    {
        if (TryGetChoice(json, out var choice))
        {
            foreach (var containerName in new[] { "delta", "message" })
            {
                if (!choice.TryGetProperty(containerName, out var container)) continue;
                var t = AsText(GetProp(container, "reasoning_content"))
                    + AsText(GetProp(container, "reasoning"));
                if (t.Length > 0) return t;
            }
        }
        return AsText(GetProp(json, "reasoning_content"));
    }

    private static bool TryGetChoice(JsonElement json, out JsonElement choice)
    {
        choice = default;
        if (json.ValueKind != JsonValueKind.Object) return false;
        if (!json.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return false;
        if (choices.GetArrayLength() == 0) return false;
        choice = choices[0];
        return true;
    }

    private static JsonElement? GetProp(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v : null;

    private static string AsText(JsonElement? raw)
    {
        if (raw is null) return "";
        var el = raw.Value;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Array => string.Concat(el.EnumerateArray().Select(p =>
                p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" :
                p.ValueKind == JsonValueKind.Object
                    ? AsText(GetProp(p, "text")) + AsText(GetProp(p, "content"))
                    : "")),
            JsonValueKind.Object => AsText(GetProp(el, "text")) + AsText(GetProp(el, "content")) + AsText(GetProp(el, "reasoning_content")),
            _ => ""
        };
    }

    private sealed class SenseNovaRequest
    {
        public string Model { get; set; } = "";
        public IReadOnlyList<object> Messages { get; set; } = [];
        public bool Stream { get; set; }
        public double Temperature { get; set; }
        public string ReasoningEffort { get; set; } = "medium";
        public SenseNovaThinking? Thinking { get; set; }
    }

    private sealed class SenseNovaThinking
    {
        public string Type { get; set; } = "enabled";
        public string ReasoningEffort { get; set; } = "medium";
    }
}
