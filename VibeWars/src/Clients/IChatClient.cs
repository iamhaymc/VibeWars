using VibeWars.Models;

namespace VibeWars.Clients;

/// <summary>
/// Common interface for chat model clients (OpenRouter and AWS Bedrock).
/// </summary>
public interface IChatClient : IDisposable
{
    /// <summary>The display name of the provider (e.g. "OpenRouter", "Bedrock").</summary>
    string ProviderName { get; }

    /// <summary>The model identifier being used.</summary>
    string ModelId { get; }

    /// <summary>
    /// Sends a conversation history with a system prompt and returns the assistant reply and token usage.
    /// </summary>
    Task<(string Reply, TokenUsage Usage)> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default);

    /// <summary>
    /// Streams the assistant reply as an async sequence of string chunks.
    /// Implement on top of the provider's streaming endpoint.
    /// </summary>
    IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default);
}
