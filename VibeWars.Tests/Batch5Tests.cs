using VibeWars.ArgumentGraph;
using VibeWars.Models;
using VibeWars.Tournament;
using VibeWars.Scripted;
using VibeWars.Clients;

namespace VibeWars.Tests;

// ── Argument Graph Tests ──────────────────────────────────────────────────────

public sealed class ArgumentGraphServiceTests
{
    [Fact]
    public void ParseClaims_ValidJson_ReturnsCorrectNodes()
    {
        var json = "[{\"text\":\"AI poses risks\",\"type\":\"Assertion\"},{\"text\":\"Studies show 50% error rate\",\"type\":\"Evidence\"}]";
        var sessionId = Guid.NewGuid();
        var nodes = ArgumentGraphService.ParseClaims(json, sessionId, 1, "Bot A");
        Assert.Equal(2, nodes.Count);
        Assert.Equal(ClaimType.Assertion, nodes[0].ClaimType);
        Assert.Equal(ClaimType.Evidence,  nodes[1].ClaimType);
    }

    [Fact]
    public void ParseClaims_MalformedJson_ReturnsEmpty()
    {
        var nodes = ArgumentGraphService.ParseClaims("not json", Guid.NewGuid(), 1, "Bot A");
        Assert.Empty(nodes);
    }

    [Fact]
    public void ParseRelations_ValidJson_ReturnsEdges()
    {
        var sessionId = Guid.NewGuid();
        var newClaims  = new[] { new ArgumentNode(Guid.NewGuid(), sessionId, 2, "Bot B", "Counter", ClaimType.Rebuttal) };
        var prevClaims = new[] { new ArgumentNode(Guid.NewGuid(), sessionId, 1, "Bot A", "Claim",   ClaimType.Assertion) };
        var json = "[{\"fromIndex\":0,\"toIndex\":0,\"relation\":\"Challenges\"}]";
        var edges = ArgumentGraphService.ParseRelations(json, newClaims, prevClaims);
        Assert.Single(edges);
        Assert.Equal(RelationType.Challenges, edges[0].Relation);
    }

    [Fact]
    public void ToMermaid_ProducesValidMermaidSyntax()
    {
        var sessionId = Guid.NewGuid();
        var nodes = new[] { new ArgumentNode(Guid.NewGuid(), sessionId, 1, "Bot A", "Test claim", ClaimType.Assertion) };
        var mermaid = ArgumentGraphService.ToMermaid(nodes, []);
        Assert.Contains("graph TD", mermaid);
        Assert.Contains("Test claim", mermaid);
    }

    [Fact]
    public void ToDot_ProducesValidDotSyntax()
    {
        var sessionId = Guid.NewGuid();
        var nodes = new[] { new ArgumentNode(Guid.NewGuid(), sessionId, 1, "Bot B", "Evidence claim", ClaimType.Evidence) };
        var dot = ArgumentGraphService.ToDot(nodes, []);
        Assert.Contains("digraph", dot);
        Assert.Contains("Evidence claim", dot);
    }

    [Fact]
    public void ComputeStats_CalculatesCorrectly()
    {
        var sessionId = Guid.NewGuid();
        var nodes = new[]
        {
            new ArgumentNode(Guid.NewGuid(), sessionId, 1, "Bot A", "Claim 1",       ClaimType.Assertion),
            new ArgumentNode(Guid.NewGuid(), sessionId, 1, "Bot B", "Counter",        ClaimType.Rebuttal),
            new ArgumentNode(Guid.NewGuid(), sessionId, 2, "Bot A", "Concession",     ClaimType.Concession),
        };
        var (total, _, concessions, _) = ArgumentGraphService.ComputeStats(nodes, []);
        Assert.Equal(3, total);
        Assert.True(concessions.ContainsKey("Bot A"));
        Assert.Equal(1, concessions["Bot A"]);
    }
}

// ── Tournament Tests ──────────────────────────────────────────────────────────

public sealed class TournamentBracketTests
{
    [Fact]
    public void GenerateRounds_EvenContestants_CorrectMatchCount()
    {
        var contestants = Enumerable.Range(1, 4)
            .Select(i => new TournamentContestant($"Bot{i}", "bedrock", "model", "Pragmatist"))
            .ToList();
        var bracket = new TournamentBracket(contestants);
        var rounds = bracket.GenerateRounds();
        Assert.Single(rounds);
        Assert.Equal(2, rounds[0].Count); // 4 contestants = 2 matches
    }

    [Fact]
    public void GenerateRounds_TwoContestants_OneMatch()
    {
        var contestants = new[]
        {
            new TournamentContestant("Bot1", "bedrock", "model", "Pragmatist"),
            new TournamentContestant("Bot2", "bedrock", "model", "Idealist"),
        };
        var bracket = new TournamentBracket(contestants);
        var rounds = bracket.GenerateRounds();
        Assert.Single(rounds);
        Assert.Single(rounds[0]);
    }

    [Fact]
    public void GetWinners_ReturnsWinnersFromResults()
    {
        var cA = new TournamentContestant("A", "bedrock", "model", "Pragmatist");
        var cB = new TournamentContestant("B", "bedrock", "model", "Idealist");
        var bracket = new TournamentBracket([cA, cB]);
        var match   = new TournamentMatch(1, cA, cB);
        var results = new[] { new TournamentResult(match, cA, cB, 2, 1) };
        var winners = bracket.GetWinners(results);
        Assert.Single(winners);
        Assert.Equal("A", winners[0].Name);
    }

    [Fact]
    public void RenderBracket_ContainsContestantNames()
    {
        var contestants = new[]
        {
            new TournamentContestant("AlphaBot", "bedrock", "model", "Pragmatist"),
            new TournamentContestant("BetaBot",  "bedrock", "model", "Idealist"),
        };
        var rendered = TournamentBracket.RenderBracket(contestants);
        Assert.Contains("AlphaBot", rendered);
        Assert.Contains("BetaBot", rendered);
    }

    [Fact]
    public void RenderBracket_BoxLinesHaveConsistentWidth()
    {
        var contestants = new[]
        {
            new TournamentContestant("BotOne", "bedrock", "model", "Pragmatist"),
            new TournamentContestant("BotTwo", "bedrock", "model", "Idealist"),
        };
        var rendered = TournamentBracket.RenderBracket(contestants);
        // Every non-empty line in the box must have the same character width
        // (box-drawing characters are single code points, same as ASCII for width purposes).
        var lines = rendered.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
        var widths = lines.Select(l => l.Length).Distinct().ToArray();
        Assert.Single(widths); // all lines must be the same width
    }
}

// ── Scripted Debate Tests ─────────────────────────────────────────────────────

public sealed class ScriptedChatClientTests
{
    [Fact]
    public async Task ChatAsync_ReturnsScriptedResponses_InOrder()
    {
        using var client = new ScriptedChatClient("test", ["First reply", "Second reply"]);
        var (r1, _) = await client.ChatAsync("sys", []);
        var (r2, _) = await client.ChatAsync("sys", []);
        Assert.Equal("First reply",  r1);
        Assert.Equal("Second reply", r2);
    }

    [Fact]
    public async Task ChatAsync_WhenExhausted_ReturnsFallback()
    {
        using var client = new ScriptedChatClient("test", []);
        var (reply, _) = await client.ChatAsync("sys", []);
        Assert.Equal("No more scripted responses.", reply);
    }

    [Fact]
    public async Task ChatStreamAsync_YieldsAllChunksAsOne()
    {
        using var client = new ScriptedChatClient("test", ["Streaming response"]);
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in client.ChatStreamAsync("sys", []))
            sb.Append(chunk);
        Assert.Equal("Streaming response", sb.ToString());
    }

    [Fact]
    public void ScriptedClient_Properties()
    {
        using var client = new ScriptedChatClient("mock-model", []);
        Assert.Equal("Scripted", client.ProviderName);
        Assert.Equal("mock-model", client.ModelId);
    }
}
