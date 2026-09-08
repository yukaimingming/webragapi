using System.Text;

namespace WebRagApi.Services.Retrieval;

/// <summary>
/// 中英混合词法切分：拉丁词按单词，汉字用单字 + 相邻二字，便于 BM25 命中药名/人名/术语。
/// </summary>
internal static class ChineseLexicalTokenizer
{
    /// <summary>切出 BM25 用的 token：英文小写词，汉字单字及其相邻二字（如 高、血、压、高血、血压）。</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var latin = new StringBuilder();
        char prevCjk = '\0';

        void FlushLatin()
        {
            if (latin.Length == 0)
                return;
            tokens.Add(latin.ToString());
            latin.Clear();
        }

        foreach (var c in text)
        {
            if (IsAsciiWordChar(c))
            {
                prevCjk = '\0';
                latin.Append(char.ToLowerInvariant(c));
                continue;
            }

            FlushLatin();
            if (c is >= '\u4e00' and <= '\u9fff')
            {
                tokens.Add(c.ToString());
                if (prevCjk != '\0')
                    tokens.Add(string.Concat(prevCjk, c));
                prevCjk = c;
            }
            else
            {
                prevCjk = '\0';
            }
        }

        FlushLatin();
        return tokens;
    }

    private static bool IsAsciiWordChar(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}
