using Microsoft.Extensions.DataIngestion;
using OpenMcdf;
using System.Text;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 读取 .doc（Word 97-2003 二进制格式）。
/// 自行解析 OLE 复合文档中的 WordDocument 流与 piece table（CLX）提取正文文本，
/// 不依赖本机安装的 Office，也不依赖 .NET Framework 专属的 Tika/IKVM。
/// 解析出的文本按段落构建 IngestionDocument；超长段落按 token 拆小，保证切块器能容纳。
/// </summary>
internal sealed class BinaryDocReader : IngestionDocumentReader
{
    static BinaryDocReader()
    {
        // .doc 的 ANSI 压缩块可能使用 GBK（cp936）编码，.NET Core 需要注册 CodePages 提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public override Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        string text = ExtractText(source, identifier);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"Word 文档 '{identifier}' 没有提取到任何文本内容。");

        var document = new IngestionDocument(identifier);
        var section = new IngestionDocumentSection();
        document.Sections.Add(section);

        foreach (var raw in text.Split('\r'))
        {
            // 清理换行残留、单元格分隔符（\x07）与空白段落
            var paragraph = raw.Replace("\x07", " ").Trim('\u000b', ' ', '\t').Trim();
            if (paragraph.Length == 0)
                continue;

            if (DocxReader.CountTokens(paragraph) > DocxReader.MaxTokensPerElement)
            {
                foreach (var piece in DocxReader.SplitByTokens(paragraph, DocxReader.MaxTokensPerElement))
                    section.Elements.Add(new IngestionDocumentParagraph(piece));
            }
            else
            {
                section.Elements.Add(new IngestionDocumentParagraph(paragraph));
            }
        }

        if (section.Elements.Count == 0)
            throw new InvalidOperationException($"Word 文档 '{identifier}' 没有解析出有效段落。");

        return Task.FromResult(document);
    }

    /// <summary>从 .doc 二进制流提取全部正文文本</summary>
    private static string ExtractText(Stream source, string identifier)
    {
        using var root = RootStorage.Open(source, StorageModeFlags.LeaveOpen);
        byte[] word;
        using (var wordStream = root.OpenStream("WordDocument"))
        {
            using var ms = new MemoryStream();
            wordStream.CopyTo(ms);
            word = ms.ToArray();
        }

        // FIB：wIdent 必须是 0xA5EC（Word 二进制格式魔数）
        if (word.Length < 0x200 || BitConverter.ToUInt16(word, 0) != 0xA5EC)
            throw new InvalidOperationException($"文档 '{identifier}' 不是有效的 Word 97-2003 二进制格式。");

        // FibBase(32) + csw(2) + fibRgW(28) + cslw(2) + fibRgLw(88) + cbRgFcLcb(2) 之后是 fibRgFcLcb
        int rgFcLcbOffset = 32 + 2 + 28 + 2 + 88 + 2;
        // fcClx 是 fibRgFcLcb97 的第 33 对（0 基）
        const int clxPairIndex = 33;
        int fcClx = BitConverter.ToInt32(word, rgFcLcbOffset + clxPairIndex * 8);
        int lcbClx = BitConverter.ToInt32(word, rgFcLcbOffset + clxPairIndex * 8 + 4);

        // fWhichTblStm 标志（FibBase 偏移 0x0A 的 bit9）决定表流名是 1Table 还是 0Table
        ushort flags = BitConverter.ToUInt16(word, 0x0A);
        string tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
        byte[] table = ReadStream(root, tableName) ?? ReadStream(root, "1Table") ?? ReadStream(root, "0Table")
            ?? throw new InvalidOperationException($"文档 '{identifier}' 缺少 {tableName} 表流。");

        if (lcbClx <= 0 || fcClx + lcbClx > table.Length)
            throw new InvalidOperationException($"文档 '{identifier}' 缺少 piece table（CLX）。");

        var sb = new StringBuilder();
        int pos = fcClx;
        // 跳过 CLX 中的 Prc 块（tag=1），直到找到 piece table（tag=2）
        while (pos < fcClx + lcbClx)
        {
            byte tag = table[pos];
            if (tag == 1)
            {
                // Prc：1 字节 tag + 2 字节 cbGrpprl + grpprl
                short cb = BitConverter.ToInt16(table, pos + 1);
                pos += 3 + cb;
            }
            else if (tag == 2)
            {
                // Piece table：4 字节 lcb + PlcPcd（n+1 个 CP + n 个 PCD）
                int lcb = BitConverter.ToInt32(table, pos + 1);
                int plcStart = pos + 5;
                int n = (lcb - 4) / 12;
                for (int i = 0; i < n; i++)
                {
                    int charCount = BitConverter.ToInt32(table, plcStart + (i + 1) * 4)
                                  - BitConverter.ToInt32(table, plcStart + i * 4);
                    // PCD：2 字节 flag + 4 字节 fc + 2 字节 prm
                    int fc = BitConverter.ToInt32(table, plcStart + (n + 1) * 4 + i * 8 + 2);
                    bool compressed = (fc & 0x40000000) != 0;
                    int dataOffset = fc & 0x3FFFFFFF;

                    if (compressed)
                    {
                        // 压缩块：每字符 1 字节，使用 Word 生成时的 ANSI 代码页（中文一般为 cp936）
                        dataOffset /= 2;
                        int end = Math.Min(dataOffset + charCount, word.Length);
                        if (dataOffset < end)
                            sb.Append(Encoding.GetEncoding(936).GetString(word, dataOffset, end - dataOffset));
                    }
                    else
                    {
                        // 非压缩块：UTF-16LE，每字符 2 字节
                        int end = Math.Min(dataOffset + charCount * 2, word.Length);
                        if (dataOffset < end)
                            sb.Append(Encoding.Unicode.GetString(word, dataOffset, end - dataOffset));
                    }
                }
                break;
            }
            else
            {
                break; // 未知结构，停止解析
            }
        }

        return sb.ToString();
    }

    /// <summary>打开 OLE 复合文档中的流并读出全部字节（不存在时返回 null）</summary>
    private static byte[]? ReadStream(RootStorage root, string name)
    {
        try
        {
            if (!root.TryOpenStream(name, out var stream) || stream is null)
                return null;

            using (stream)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
        catch
        {
            return null;
        }
    }
}
