namespace VibeWars.Clients;

public static class EmbeddingHelper
{
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0f : dot / denom;
    }

    public static IEmbeddingClient? CreateEmbeddingClient(string backend, string? apiKey, string region, string model)
        => backend.ToLowerInvariant() switch
        {
            "openrouter" when apiKey is not null => new OpenRouterEmbeddingClient(apiKey, model),
            "bedrock"                            => new BedrockEmbeddingClient(region, model),
            _                                    => null, // "none" — no embeddings
        };
}
