namespace VibeWars.Clients;

/// <summary>
/// Abstraction for generating dense vector embeddings from text.
/// </summary>
public interface IEmbeddingClient : IDisposable
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
}
