namespace WebRagApi.Models;

/// <summary>检索管道配置（appsettings.json 的 Retrieval 节）</summary>
public class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>vector=仅向量；hybrid=向量+BM25+RRF；rerank=hybrid 后再 Cross-Encoder 重排</summary>
    public string DefaultMode { get; set; } = "rerank";

    /// <summary>融合/重排前每路多取几倍候选</summary>
    public int CandidateMultiplier { get; set; } = 4;

    /// <summary>RRF 常数 k，常用 60</summary>
    public int RrfK { get; set; } = 60;
}
