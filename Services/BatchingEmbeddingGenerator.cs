using Microsoft.Extensions.AI;

namespace WebRagApi.Services;

/// <summary>
/// 把大批量 embedding 请求自动拆分成小批次。
/// Ollama 在单次请求包含数百条输入时 runner 会崩溃（返回 400，内含 /tokenize connectex refused），
/// 该装饰器保证每次只发送 batchSize 条，避免压垮本地服务。（与 AIChatApp 相同的实现）
/// </summary>
public sealed class BatchingEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
    int batchSize = 16) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values as IReadOnlyList<string> ?? [.. values];
        if (inputs.Count <= batchSize)
            return await innerGenerator.GenerateAsync(inputs, options, cancellationToken);

        var results = new List<Embedding<float>>(inputs.Count);
        foreach (var batch in inputs.Chunk(batchSize))
        {
            var batchResults = await innerGenerator.GenerateAsync(batch, options, cancellationToken);
            results.AddRange(batchResults);
        }

        return new GeneratedEmbeddings<Embedding<float>>(results);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => innerGenerator.GetService(serviceType, serviceKey);

    public void Dispose() => innerGenerator.Dispose();
}
