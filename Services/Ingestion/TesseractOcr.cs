using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Tesseract;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 本地 Tesseract OCR（默认 chi_sim）。TesseractEngine 非线程安全，所有识别串行执行。
/// 中文扫描件不要默认开 eng：chi_sim+eng 会把整行中文认成无意义英文（如 HAUSA TT BLESS）。
/// </summary>
internal static class TesseractOcr
{
    private static readonly object Gate = new();
    private static TesseractEngine? _engine;
    private static string? _initError;
    private static readonly Regex CjkSpace = new(@"([\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", RegexOptions.Compiled);
    private static readonly Regex PercentOcr = new(@"不超过([1-9]\d)9(?=[。．]|$)", RegexOptions.Compiled);

    /// <summary>对图片字节做 OCR，返回规整后的文本；失败返回空串。</summary>
    public static string Recognize(byte[] imageBytes, ILogger? logger = null)
    {
        if (imageBytes.Length == 0)
            return string.Empty;

        try
        {
            var png = ToPng(imageBytes);
            lock (Gate)
            {
                var engine = GetEngine(logger);
                if (engine is null)
                    return string.Empty;

                var best = RecognizeBest(engine, png);
                return Normalize(best);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "OCR 识别失败。");
            return string.Empty;
        }
    }

    /// <summary>Auto / SingleBlock 两种版面各跑一遍，按中文质量分选更好的结果。</summary>
    private static string RecognizeBest(TesseractEngine engine, byte[] png)
    {
        string? bestText = null;
        float bestScore = float.MinValue;
        foreach (var psm in new[] { PageSegMode.Auto, PageSegMode.SingleBlock })
        {
            using var pix = Pix.LoadFromMemory(png);
            using var page = engine.Process(pix, psm);
            var text = page.GetText() ?? string.Empty;
            var score = Score(text, page.GetMeanConfidence());
            if (score > bestScore)
            {
                bestScore = score;
                bestText = text;
            }
        }
        return bestText ?? string.Empty;
    }

    /// <summary>置信度 + 汉字数，扣无意义纯拉丁行（chi_sim+eng 时常见的 HAUSA 类噪声）。</summary>
    private static float Score(string text, float confidence)
    {
        int cjk = 0, latin = 0;
        foreach (var c in text)
        {
            if (c is >= '\u4e00' and <= '\u9fff') cjk++;
            else if (char.IsAsciiLetter(c)) latin++;
        }
        int garbageLines = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (IsGarbageLatinLine(line))
                garbageLines++;
        }
        return confidence * 100 + cjk * 2 - latin - garbageLines * 80;
    }

    private static bool IsGarbageLatinLine(string line)
    {
        var t = line.Trim();
        if (t.Length < 6)
            return false;
        int cjk = 0, letters = 0;
        foreach (var c in t)
        {
            if (c is >= '\u4e00' and <= '\u9fff') cjk++;
            else if (char.IsAsciiLetter(c)) letters++;
        }
        return cjk == 0 && letters >= 6;
    }

    /// <summary>把 OCR 文本按空行拆成段落，去掉空白段。</summary>
    public static IEnumerable<string> SplitParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var buffer = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
                continue;
            }

            if (buffer.Length > 0)
                buffer.Append('\n');
            buffer.Append(line);
        }

        if (buffer.Length > 0)
            yield return buffer.ToString();
    }

    public static int CountVisibleChars(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        int n = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
                n++;
        }
        return n;
    }

    private static TesseractEngine? GetEngine(ILogger? logger)
    {
        if (_engine is not null)
            return _engine;
        if (_initError is not null)
            return null;

        var tessData = ResolveTessDataPath();
        if (tessData is null)
        {
            _initError = "未找到 tessdata 目录（需要 chi_sim.traineddata 与 eng.traineddata）";
            logger?.LogError("{Error}", _initError);
            return null;
        }

        try
        {
            // 中文病历/指南扫描件用 chi_sim；混开 eng 会把整行汉字认成无意义英文
            _engine = new TesseractEngine(tessData, "chi_sim", EngineMode.LstmOnly);
            _engine.SetVariable("user_defined_dpi", "300");
            logger?.LogInformation("已加载 Tesseract OCR（chi_sim，tessdata: {Path}）。", tessData);
            return _engine;
        }
        catch (Exception ex)
        {
            _initError = ex.Message;
            logger?.LogError(ex, "初始化 Tesseract 失败（tessdata: {Path}）。", tessData);
            return null;
        }
    }

    private static string? ResolveTessDataPath()
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "tessdata"),
                     Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
                 })
        {
            if (File.Exists(Path.Combine(dir, "chi_sim.traineddata")))
                return dir;
        }
        return null;
    }

    /// <summary>解码为灰度 PNG，最长边限制 2600，避免超大扫描件拖垮 OCR。</summary>
    private static byte[] ToPng(byte[] imageBytes)
    {
        using var bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap is null)
            return imageBytes;

        const int maxEdge = 2600;
        SKBitmap working = bitmap;
        SKBitmap? scaled = null;
        try
        {
            int edge = Math.Max(bitmap.Width, bitmap.Height);
            if (edge > maxEdge)
            {
                float scale = maxEdge / (float)edge;
                var info = new SKImageInfo(
                    Math.Max(1, (int)(bitmap.Width * scale)),
                    Math.Max(1, (int)(bitmap.Height * scale)));
                scaled = bitmap.Resize(info, SKSamplingOptions.Default);
                if (scaled is not null)
                    working = scaled;
            }

            using var gray = ToGrayscale(working);
            using var image = SKImage.FromBitmap(gray);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>转灰度，去掉彩色底纹对 Tesseract 的干扰。</summary>
    private static SKBitmap ToGrayscale(SKBitmap source)
    {
        var gray = new SKBitmap(source.Width, source.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(gray);
        canvas.DrawBitmap(source, 0, 0);
        return gray;
    }

    /// <summary>去掉纯拉丁噪声行，合并汉字间空格，并把「不超过259」这类误识别修成 25%。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                    lines.Add(string.Empty);
                continue;
            }
            if (IsGarbageLatinLine(line))
                continue;
            lines.Add(line);
        }

        var joined = string.Join('\n', lines).Trim();
        joined = CjkSpace.Replace(joined, "$1");
        // 中文模型常把「25%」末尾的 % 认成 9
        joined = PercentOcr.Replace(joined, "不超过$1%");
        return joined;
    }
}
