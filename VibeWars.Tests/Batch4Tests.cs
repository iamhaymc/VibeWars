using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VibeWars.Clients;
using VibeWars.JudgePanel;
using VibeWars.Models;
using VibeWars.StanceTracker;

namespace VibeWars.Tests;

// ─── Feature 14: Judge Panel Tests ───────────────────────────────────────────

public sealed class JudgePanelTests
{
    [Fact]
    public void Aggregate_UnanimousVerdicts_ReturnsCorrectWinner()
    {
        var verdicts = new List<JudgeVerdict>
        {
            new("Bot A", "Better arguments", ""),
            new("Bot A", "More persuasive", ""),
            new("Bot A", "Stronger evidence", ""),
        };
        var result = JudgePanelService.Aggregate(verdicts);
        Assert.Equal("Bot A", result.Winner);
        Assert.Contains("consensus", result.Reasoning);
    }

    [Fact]
    public void Aggregate_SplitVerdict_ReturnsMajority()
    {
        var verdicts = new List<JudgeVerdict>
        {
            new("Bot A", "Reason 1", ""),
            new("Bot B", "Reason 2", ""),
            new("Bot A", "Reason 3", ""),
        };
        var result = JudgePanelService.Aggregate(verdicts);
        Assert.Equal("Bot A", result.Winner);
    }

    [Fact]
    public void Aggregate_ThreeWayTie_ReturnsTie()
    {
        var verdicts = new List<JudgeVerdict>
        {
            new("Bot A", "R1", "idea1"),
            new("Bot B", "R2", "idea2"),
            new("Tie",   "R3", "idea3"),
        };
        var result = JudgePanelService.Aggregate(verdicts);
        Assert.Equal("Tie", result.Winner);
    }

    [Fact]
    public void Aggregate_NewIdeas_AreUnioned()
    {
        var verdicts = new List<JudgeVerdict>
        {
            new("Bot A", "R1", "explore ethics; discuss costs"),
            new("Bot A", "R2", "discuss costs; examine history"),
        };
        var result = JudgePanelService.Aggregate(verdicts);
        Assert.Contains("ethics", result.NewIdeas);
        Assert.Contains("history", result.NewIdeas);
    }
}

// ─── Feature 15: Stance Meter Tests ──────────────────────────────────────────

public sealed class StanceMeterTests
{
    [Fact]
    public void ParseEntry_ValidJson_ReturnsCorrectStance()
    {
        var entry = StanceMeterService.ParseEntry("{\"stance\": 3, \"concessions\": [\"I agree on point 1\"]}", 2);
        Assert.Equal(3, entry.Stance);
        Assert.Equal(2, entry.Round);
        Assert.Single(entry.Concessions);
    }

    [Fact]
    public void ParseEntry_MalformedJson_ReturnsDefaultEntry()
    {
        var entry = StanceMeterService.ParseEntry("not json", 1);
        Assert.Equal(0, entry.Stance);
        Assert.Equal(1, entry.Round);
        Assert.Empty(entry.Concessions);
    }

    [Fact]
    public void ParseEntry_StanceClampedToRange()
    {
        var entry = StanceMeterService.ParseEntry("{\"stance\": 100, \"concessions\": []}", 1);
        Assert.Equal(5, entry.Stance);
    }

    [Fact]
    public void StanceTimeline_StanceDelta_CalculatedCorrectly()
    {
        var timeline = new StanceTimeline("Bot A");
        timeline.Add(new StanceEntry(1, 3, []));
        timeline.Add(new StanceEntry(2, 1, []));
        timeline.Add(new StanceEntry(3, -1, ["Concession 1"]));
        Assert.Equal(4, timeline.StanceDelta); // |(-1) - 3| = 4
        Assert.Equal(1, timeline.ConcessionCount);
    }

    [Fact]
    public void CalculateIntellectualProgressScore_ComputesCorrectly()
    {
        var botA = new StanceTimeline("Bot A");
        botA.Add(new StanceEntry(1, 3, []));
        botA.Add(new StanceEntry(2, 1, [])); // delta = 2

        var botB = new StanceTimeline("Bot B");
        botB.Add(new StanceEntry(1, -4, []));
        botB.Add(new StanceEntry(2, -2, ["concession 1"])); // delta = 2, 1 concession

        var score = StanceMeterService.CalculateIntellectualProgressScore(botA, botB, 3);
        // (2 + 2 + 0.5) / 3 = 1.5
        Assert.Equal(1.5, score, precision: 1);
    }
}

// ─── Feature 3: Embedding Helper Tests ───────────────────────────────────────

public sealed class EmbeddingHelperTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1f, 0f, 0f };
        var sim = EmbeddingHelper.CosineSimilarity(v, v);
        Assert.InRange(sim, 0.99f, 1.01f);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1f, 0f, 0f };
        var b = new float[] { 0f, 1f, 0f };
        var sim = EmbeddingHelper.CosineSimilarity(a, b);
        Assert.InRange(sim, -0.01f, 0.01f);
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_ReturnsZero()
    {
        var a = new float[] { 0f, 0f, 0f };
        var b = new float[] { 1f, 2f, 3f };
        var sim = EmbeddingHelper.CosineSimilarity(a, b);
        Assert.Equal(0f, sim);
    }
}

// ─── Feature 3: SqliteMemoryStore Embedding Tests ────────────────────────────

public sealed class SqliteMemoryStoreEmbeddingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMemoryStore _store;

    public SqliteMemoryStoreEmbeddingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"vw_embed_{Guid.NewGuid():N}.db");
        _store  = new SqliteMemoryStore(_dbPath);
    }

    [Fact]
    public async Task SaveEmbeddingAsync_ThenSemanticSearch_ReturnsCorrectEntry()
    {
        var session = new DebateSession(Guid.NewGuid(), "test", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1), "Tie", "");
        var entry = new MemoryEntry(Guid.NewGuid(), "Bot A", "test", 1, "assistant",
            "Machine learning models can be biased", DateTimeOffset.UtcNow, ["test"]);
        await _store.SaveSessionAsync(session, [entry]);

        // Save a fake embedding for the entry
        var embedding = new float[] { 0.1f, 0.9f, 0.2f };
        await _store.SaveEmbeddingAsync(entry.Id, embedding);

        // Search with a similar embedding
        var queryEmbedding = new float[] { 0.15f, 0.85f, 0.25f };
        var results = await _store.SemanticSearchAsync(queryEmbedding, topK: 5);

        Assert.NotEmpty(results);
        Assert.Contains(results, e => e.Id == entry.Id);
    }

    [Fact]
    public async Task GetEntriesWithoutEmbeddings_ReturnsEntriesWithNullEmbedding()
    {
        var session = new DebateSession(Guid.NewGuid(), "test2", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1), "Tie", "");
        var entry = new MemoryEntry(Guid.NewGuid(), "Bot B", "test2", 1, "assistant",
            "No embedding yet", DateTimeOffset.UtcNow, ["test2"]);
        await _store.SaveSessionAsync(session, [entry]);

        var pending = await _store.GetEntriesWithoutEmbeddingsAsync();
        Assert.Contains(pending, e => e.Id == entry.Id);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
