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

    /// <summary>边读边写边哈希，避免大文件整份进内存。</summary>
    public static async Task<string> CopyAndHashAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (true)
        {
            int n = await source.ReadAsync(buffer, cancellationToken);
            if (n == 0)
                break;
            sha.AppendData(buffer.AsSpan(0, n));
            await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
