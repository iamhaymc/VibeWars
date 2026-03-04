using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Scripted;

/// <summary>
/// A scripted debate defines exact bot responses per round, bypassing LLM calls.
/// Useful for deterministic testing of downstream features.
/// </summary>
public record ScriptedRound(string BotAResponse, string BotBResponse, string JudgeResponse);

public record ScriptedDebateSpec(string Topic, IReadOnlyList<ScriptedRound> Rounds);

/// <summary>
/// An IChatClient that returns predefined responses from a script.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<string> _responses;
    private readonly string _name;

    public string ProviderName => "Scripted";
    public string ModelId      => _name;

    public ScriptedChatClient(string name, IEnumerable<string> responses)
    {
        _name      = name;
        _responses = new Queue<string>(responses);
    }

    public Task<(string Reply, TokenUsage Usage)> ChatAsync(
        string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var reply = _responses.Count > 0 ? _responses.Dequeue() : "No more scripted responses.";
        return Task.FromResult((reply, TokenUsage.Empty));
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt, IReadOnlyList<ChatMessage> history,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (reply, _) = await ChatAsync(systemPrompt, history, ct);
        yield return reply;
    }

    public void Dispose() { }
}
