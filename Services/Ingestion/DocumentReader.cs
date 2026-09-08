using Microsoft.Extensions.DataIngestion;

namespace WebRagApi.Services.Ingestion;

/// <summary>
/// 统一文档读取入口：按扩展名分发到具体解析器（pdf / docx / doc / md / 扫描件图片）。
/// identifier 统一规整为相对上传目录的路径（即文件名），作为 Qdrant 中的文档标识。
/// </summary>
internal sealed class DocumentReader(DirectoryInfo rootDirectory, ILoggerFactory? loggerFactory = null) : IngestionDocumentReader
{
    private const string DocxMediaType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private readonly MarkdownReader _markdownReader = new();
    private readonly PdfPigReader _pdfReader = new(loggerFactory?.CreateLogger<PdfPigReader>());
    private readonly DocxReader _docxReader = new();
    private readonly BinaryDocReader _docReader = new();
    private readonly ImageOcrReader _imageReader = new(loggerFactory?.CreateLogger<ImageOcrReader>());

    public override Task<IngestionDocument> ReadAsync(FileInfo source, string identifier, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        if (Path.IsPathFullyQualified(identifier))
        {
            // 把绝对路径规整为相对路径，保证同一文件在任何机器上的文档标识一致
            identifier = Path.GetRelativePath(rootDirectory.FullName, identifier);
        }

        mediaType = GetCustomMediaType(source) ?? mediaType;
        return base.ReadAsync(source, identifier, mediaType, cancellationToken);
    }

    public override Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
        => mediaType switch
        {
            "application/pdf" => _pdfReader.ReadAsync(source, identifier, mediaType, cancellationToken),
            "text/markdown" => _markdownReader.ReadAsync(source, identifier, mediaType, cancellationToken),
            DocxMediaType => _docxReader.ReadAsync(source, identifier, mediaType, cancellationToken),
            "application/msword" => _docReader.ReadAsync(source, identifier, mediaType, cancellationToken),
            "image/png" or "image/jpeg" or "image/tiff" or "image/bmp" =>
                _imageReader.ReadAsync(source, identifier, mediaType, cancellationToken),
            _ => throw new InvalidOperationException($"不支持的媒体类型 '{mediaType}'"),
        };

    /// <summary>按扩展名映射媒体类型（上传的文件没有标准 mime 信息，这里做兜底）</summary>
    public static string? GetCustomMediaType(FileInfo source)
        => source.Extension.ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".docx" => DocxMediaType,
            ".doc" => "application/msword",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".tif" or ".tiff" => "image/tiff",
            ".bmp" => "image/bmp",
            _ => null
        };

    /// <summary>服务支持的文档扩展名（供上传校验用）</summary>
    public static readonly string[] SupportedExtensions =
        [".pdf", ".doc", ".docx", ".md", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"];
}
