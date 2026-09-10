using System.Security.Cryptography;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 文档内容 SHA256（小写十六进制）。用作知识库去重的唯一标识：内容相同即视为同一文档，与文件名无关。
/// </summary>
internal static class ContentHash
{
    public static string Sha256Hex(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;

        var hash = SHA256.HashData(stream);
        if (stream.CanSeek)
            stream.Position = 0;

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Sha256Hex(fs);
    }
}
