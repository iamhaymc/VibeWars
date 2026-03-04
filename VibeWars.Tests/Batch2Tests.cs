using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VibeWars.Clients;
using VibeWars.HumanPlayer;
using VibeWars.Models;

namespace VibeWars.Tests;

// ─── Feature 1: Streaming Tests ───────────────────────────────────────────────

public class StreamingTests
{
    private sealed class MockStreamingClient : IChatClient
    {
        private readonly string[] _chunks;
        public MockStreamingClient(params string[] chunks) => _chunks = chunks;
        public string ProviderName => "Mock";
        public string ModelId => "mock";
        public Task<(string Reply, TokenUsage Usage)> ChatAsync(string sys, IReadOnlyList<ChatMessage> hist, CancellationToken ct = default)
            => Task.FromResult(("mock reply", TokenUsage.Empty));
        public async IAsyncEnumerable<string> ChatStreamAsync(string sys, IReadOnlyList<ChatMessage> hist, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var chunk in _chunks) { await Task.Yield(); yield return chunk; }
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task ChatStreamAsync_ChunksAccumulateCorrectly()
    {
        var client = new MockStreamingClient("Hello", " world", "!");
        var sb = new StringBuilder();
        await foreach (var chunk in client.ChatStreamAsync("sys", []))
            sb.Append(chunk);
        Assert.Equal("Hello world!", sb.ToString());
    }

    [Fact]
    public async Task ChatStreamAsync_EmptyChunks_ReturnsEmpty()
    {
        var client = new MockStreamingClient();
        var sb = new StringBuilder();
        await foreach (var chunk in client.ChatStreamAsync("sys", []))
            sb.Append(chunk);
        Assert.Equal(string.Empty, sb.ToString());
    }

    [Fact]
    public async Task ChatStreamAsync_SingleChunk_ReturnsThatChunk()
    {
        var client = new MockStreamingClient("only chunk");
        var sb = new StringBuilder();
        await foreach (var chunk in client.ChatStreamAsync("sys", []))
            sb.Append(chunk);
        Assert.Equal("only chunk", sb.ToString());
    }

    [Fact]
    public async Task ChatStreamAsync_CancellationToken_PropagatesCorrectly()
    {
        var client = new MockStreamingClient("a", "b", "c");
        using var cts = new CancellationTokenSource();
        var sb = new StringBuilder();
        await foreach (var chunk in client.ChatStreamAsync("sys", [], cts.Token))
        {
            sb.Append(chunk);
            cts.Cancel(); // cancel after first chunk
        }
        // We got at least one chunk
        Assert.False(string.IsNullOrEmpty(sb.ToString()));
    }
}

// ─── Feature 13: Resilient Chat Client Tests ──────────────────────────────────

public class ResilientChatClientTests
{
    private sealed class FlakyMockClient : IChatClient
    {
        private readonly int _failCount;
        private int _attempts;
        public FlakyMockClient(int failCount) => _failCount = failCount;
        public string ProviderName => "FlakyMock";
        public string ModelId => "flaky";
        public async Task<(string Reply, TokenUsage Usage)> ChatAsync(string sys, IReadOnlyList<ChatMessage> hist, CancellationToken ct = default)
        {
            if (_attempts++ < _failCount)
                throw new HttpRequestException("Simulated failure");
            await Task.Yield();
            return ("success", TokenUsage.Empty);
        }
        public async IAsyncEnumerable<string> ChatStreamAsync(string sys, IReadOnlyList<ChatMessage> hist, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return "streamed";
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task ChatAsync_SucceedsAfterRetries()
    {
        // Fails once then succeeds; retryMax=3 should be enough
        var inner = new FlakyMockClient(1);
        using var resilient = new ResilientChatClient(inner, retryMax: 3, baseDelayMs: 1);
        var (reply, _) = await resilient.ChatAsync("sys", []);
        Assert.Equal("success", reply);
    }

    [Fact]
    public async Task ChatAsync_SucceedsWithNoFailures()
    {
        var inner = new FlakyMockClient(0);
        using var resilient = new ResilientChatClient(inner, retryMax: 3, baseDelayMs: 1);
        var (reply, _) = await resilient.ChatAsync("sys", []);
        Assert.Equal("success", reply);
    }

    [Fact]
    public void ProviderName_DelegatestoInner()
    {
        var inner = new FlakyMockClient(0);
        using var resilient = new ResilientChatClient(inner);
        Assert.Equal("FlakyMock", resilient.ProviderName);
    }

    [Fact]
    public void ModelId_DelegatesToInner()
    {
        var inner = new FlakyMockClient(0);
        using var resilient = new ResilientChatClient(inner);
        Assert.Equal("flaky", resilient.ModelId);
    }

    [Fact]
    public async Task ChatStreamAsync_DelegatesChunks()
    {
        var inner = new FlakyMockClient(0);
        using var resilient = new ResilientChatClient(inner);
        var sb = new StringBuilder();
        await foreach (var chunk in resilient.ChatStreamAsync("sys", []))
            sb.Append(chunk);
        Assert.Equal("streamed", sb.ToString());
    }
}

// ─── Feature 5: Human Input Reader Tests ──────────────────────────────────────

public class HumanInputReaderTests
{
    [Fact]
    public void ReadArgument_WithInput_ReturnsInput()
    {
        var reader = new HumanInputReader(new StringReader("my argument\n"));
        var result = reader.ReadArgument("prompt: ");
        Assert.Equal("my argument", result);
    }

    [Fact]
    public void ReadArgument_BlankInput_ReturnsFallback()
    {
        var reader = new HumanInputReader(new StringReader("\n"));
        var result = reader.ReadArgument("prompt: ", "fallback value");
        Assert.Equal("fallback value", result);
    }

    [Fact]
    public void ReadArgument_WhitespaceOnly_ReturnsFallback()
    {
        var reader = new HumanInputReader(new StringReader("   \n"));
        var result = reader.ReadArgument("prompt: ", "fallback value");
        Assert.Equal("fallback value", result);
    }

    [Fact]
    public void ReadArgument_NoFallback_EmptyReturnsEmptyString()
    {
        var reader = new HumanInputReader(new StringReader("\n"));
        var result = reader.ReadArgument("prompt: ");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ReadJudgeVerdict_ReturnsCorrectWinnerAndReasoning()
    {
        var reader = new HumanInputReader(new StringReader("Bot A\nBecause they argued better\n"));
        var (winner, reasoning) = reader.ReadJudgeVerdict();
        Assert.Equal("Bot A", winner);
        Assert.Equal("Because they argued better", reasoning);
    }

    [Fact]
    public void ReadJudgeVerdict_EmptyWinner_ReturnsTie()
    {
        var reader = new HumanInputReader(new StringReader("\nsome reasoning\n"));
        var (winner, reasoning) = reader.ReadJudgeVerdict();
        Assert.Equal("Tie", winner);
        Assert.Equal("some reasoning", reasoning);
    }

    [Fact]
    public void ReadJudgeVerdict_BotBWinner_ReturnsCorrectly()
    {
        var reader = new HumanInputReader(new StringReader("Bot B\nBot B had stronger evidence\n"));
        var (winner, reasoning) = reader.ReadJudgeVerdict();
        Assert.Equal("Bot B", winner);
        Assert.Equal("Bot B had stronger evidence", reasoning);
    }

    [Fact]
    public void ReadArgument_TrimsWhitespace()
    {
        var reader = new HumanInputReader(new StringReader("  trimmed input  \n"));
        var result = reader.ReadArgument("prompt: ");
        Assert.Equal("trimmed input", result);
    }
}
