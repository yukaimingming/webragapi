using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace WebRagApi.Services;

/// <summary>
/// 商汤等第三方 OpenAI 兼容接口会在流式/非流式响应中返回空的或未知的 finish_reason（如 ""），
/// OpenAI SDK 解析这些值时会抛出 "Unknown ChatFinishReason value" 异常。
/// 该处理器在 HTTP 响应流层面把非法的 finish_reason 改写为 "stop"，仅作用于 /chat/completions 请求。
/// </summary>
public class FinishReasonRewriteHandler : DelegatingHandler
{
    private static readonly Regex FinishReasonRegex = new(
        "\"finish_reason\"\\s*:\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled);

    // 商汤在 tool_calls 中返回空的 type 字段（如 "type":""），OpenAI SDK 解析会抛
    // "Unknown ChatToolCallKind value" 异常，改写为标准值 "function"
    private static readonly Regex EmptyToolCallTypeRegex = new(
        "\"type\"\\s*:\\s*(?:\"\"|null)",
        RegexOptions.Compiled);

    // OpenAI SDK 能识别的全部 finish_reason 值
    private static readonly HashSet<string> KnownReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "length", "tool_calls", "function_call", "content_filter"
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        var path = request.RequestUri?.PathAndQuery ?? "";
        if (response.Content is not null && path.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var rewritten = new LineRewriteStream(stream, RewriteLine);
            var newContent = new StreamContent(rewritten);
            foreach (var header in response.Content.Headers)
                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = newContent;
        }

        return response;
    }

    private static string RewriteLine(string line)
    {
        if (!line.Contains("finish_reason", StringComparison.Ordinal) &&
            !line.Contains("tool_call", StringComparison.Ordinal))
            return line;

        if (line.Contains("tool_call", StringComparison.Ordinal))
            line = EmptyToolCallTypeRegex.Replace(line, "\"type\":\"function\"");

        return FinishReasonRegex.Replace(line, match =>
            KnownReasons.Contains(match.Groups["value"].Value)
                ? match.Value
                : "\"finish_reason\":\"stop\"");
    }

    /// <summary>逐行读取底层流并对每行做字符串改写（SSE 每个事件一行，JSON 响应整体为一行）。</summary>
    private sealed class LineRewriteStream(Stream inner, Func<string, string> rewrite) : Stream
    {
        private byte[]? _pending;
        private int _pendingOffset;
        private readonly MemoryStream _lineBuffer = new();
        private bool _innerEof;

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (_pending is not null && _pendingOffset < _pending.Length)
                {
                    int n = Math.Min(count, _pending.Length - _pendingOffset);
                    Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset, n);
                    _pendingOffset += n;
                    if (_pendingOffset >= _pending.Length)
                    {
                        _pending = null;
                        _pendingOffset = 0;
                    }
                    return n;
                }

                if (_innerEof)
                    return 0;

                int b = inner.ReadByte();
                if (b == -1)
                {
                    _innerEof = true;
                    // 冲刷最后一行（流末尾可能没有换行符）
                    if (_lineBuffer.Length > 0)
                        FlushLine(finalLine: true);
                    continue;
                }

                _lineBuffer.WriteByte((byte)b);
                if (b == '\n')
                    FlushLine(finalLine: false);
            }
        }

        private void FlushLine(bool finalLine)
        {
            var lineBytes = _lineBuffer.ToArray();
            _lineBuffer.SetLength(0);

            var text = Encoding.UTF8.GetString(lineBytes);
            var rewritten = rewrite(text);
            _pending = Encoding.UTF8.GetBytes(rewritten);
            _pendingOffset = 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await Task.Run(() => Read(buffer, offset, count), cancellationToken);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
