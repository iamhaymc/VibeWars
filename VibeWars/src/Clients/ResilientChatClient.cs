using Polly;
using VibeWars.Models;
using VibeWars.Resilience;

namespace VibeWars.Clients;

/// <summary>
/// Wraps an <see cref="IChatClient"/> with Polly retry + circuit-breaker resilience.
/// </summary>
public sealed class ResilientChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ResiliencePipeline<(string Reply, TokenUsage Usage)> _pipeline;

    public string ProviderName => _inner.ProviderName;
    public string ModelId      => _inner.ModelId;

    public ResilientChatClient(IChatClient inner, int retryMax = 4, int baseDelayMs = 1000)
    {
        _inner = inner;
        _pipeline = ResilienceHelper.BuildChatPipeline<(string Reply, TokenUsage Usage)>(retryMax, baseDelayMs);
    }

    public Task<(string Reply, TokenUsage Usage)> ChatAsync(
        string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        => _pipeline.ExecuteAsync(
            async token => await _inner.ChatAsync(systemPrompt, history, token),
            ct).AsTask();

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Stream directly; resilience for streaming is limited
        await foreach (var chunk in _inner.ChatStreamAsync(systemPrompt, history, ct))
            yield return chunk;
    }

    public void Dispose() => _inner.Dispose();
}
