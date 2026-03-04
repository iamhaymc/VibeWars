namespace VibeWars.Knowledge;

public record KnowledgePassage(string Title, string Content, string Source);

/// <summary>Interface for external knowledge sources used by retrieval-augmented debating.</summary>
public interface IKnowledgeSource
{
    Task<IReadOnlyList<KnowledgePassage>> SearchAsync(string query, int topK = 3, CancellationToken ct = default);
}
