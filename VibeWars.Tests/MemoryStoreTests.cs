using System.Text.Json;
using Moq;
using VibeWars.Clients;
using VibeWars.Models;
using Amazon.S3;
using Amazon.S3.Model;

namespace VibeWars.Tests;

// ──────────────────────────────────────────────────────────────────────────────
// SqliteMemoryStore tests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class SqliteMemoryStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vibewars_test_{Guid.NewGuid():N}.db");
    private readonly SqliteMemoryStore _store;

    public SqliteMemoryStoreTests() => _store = new SqliteMemoryStore(_dbPath);

    [Fact]
    public async Task SaveSession_Then_GetSessionEntries_ReturnsAllEntries()
    {
        var session = MakeSession();
        var entries = new[]
        {
            MakeEntry("Bot A", "test topic", 1, "assistant", "Some content about AI"),
            MakeEntry("Bot B", "test topic", 1, "assistant", "Counter argument about AI"),
        };

        await _store.SaveSessionAsync(session, entries);

        var retrieved = await _store.GetSessionEntriesAsync(session.SessionId);

        Assert.Equal(2, retrieved.Count);
        Assert.Contains(retrieved, e => e.Content == "Some content about AI");
        Assert.Contains(retrieved, e => e.Content == "Counter argument about AI");
    }

    [Fact]
    public async Task SearchAsync_ReturnsMatchingEntries()
    {
        var session = MakeSession();
        var entries = new[]
        {
            MakeEntry("Bot A", "climate change", 1, "assistant", "Carbon emissions are the primary driver"),
            MakeEntry("Bot B", "climate change", 1, "assistant", "Renewable energy offers a solution"),
        };

        await _store.SaveSessionAsync(session, entries);

        var results = await _store.SearchAsync("Carbon");

        Assert.NotEmpty(results);
        Assert.Contains(results, e => e.Content.Contains("Carbon"));
    }

    [Fact]
    public async Task SearchAsync_WhenNoMatch_ReturnsEmpty()
    {
        var session = MakeSession();
        var entries = new[] { MakeEntry("Bot A", "history", 1, "assistant", "Ancient Rome was powerful") };

        await _store.SaveSessionAsync(session, entries);

        var results = await _store.SearchAsync("QuantumPhysicsXYZ");

        Assert.Empty(results);
    }

    [Fact]
    public async Task ListSessionsAsync_ReturnsMostRecentFirst()
    {
        var older = MakeSession(DateTimeOffset.UtcNow.AddDays(-2));
        var newer = MakeSession(DateTimeOffset.UtcNow.AddDays(-1));

        await _store.SaveSessionAsync(older, []);
        await _store.SaveSessionAsync(newer, []);

        var sessions = await _store.ListSessionsAsync(10);

        Assert.True(sessions.Count >= 2);
        Assert.True(sessions[0].StartedAt >= sessions[1].StartedAt);
    }

    [Fact]
    public async Task CountEntriesForTopic_ReturnsCorrectCount()
    {
        var session = MakeSession();
        var entries = new[]
        {
            MakeEntry("Bot A", "philosophy", 1, "assistant", "Plato believed..."),
            MakeEntry("Bot B", "philosophy", 1, "assistant", "Aristotle countered..."),
            MakeEntry("Judge",  "philosophy", 1, "assistant", "Both perspectives..."),
        };

        await _store.SaveSessionAsync(session, entries);

        var count = _store.CountEntriesForTopic("philosophy");

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task ClearAll_RemovesAllData()
    {
        var session = MakeSession();
        await _store.SaveSessionAsync(session, [MakeEntry("Bot A", "test", 1, "assistant", "hello")]);

        _store.ClearAll();

        var sessions = await _store.ListSessionsAsync();
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task SaveSession_Duplicate_EntryIgnored()
    {
        var session = MakeSession();
        var entry   = MakeEntry("Bot A", "deduplication", 1, "assistant", "First content");

        await _store.SaveSessionAsync(session, [entry]);
        await _store.SaveSessionAsync(session, [entry]);   // save the same entry twice

        var entries = await _store.GetSessionEntriesAsync(session.SessionId);
        Assert.Single(entries);
    }

    [Fact]
    public async Task SearchAsync_PercentWildcard_DoesNotMatchEverything()
    {
        // A query of "%" should be treated as a literal percent sign,
        // not as a SQL LIKE wildcard that matches all content.
        var session = MakeSession();
        var entries = new[]
        {
            MakeEntry("Bot A", "literal", 1, "assistant", "no percent here"),
            MakeEntry("Bot B", "literal", 1, "assistant", "also no percent"),
        };
        await _store.SaveSessionAsync(session, entries);

        // Searching for "%" as a literal should return no matches
        // (neither entry contains the character '%').
        var results = await _store.SearchAsync("%");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_UnderscoreWildcard_DoesNotActAsWildcard()
    {
        // A query of "_" should be treated as a literal underscore,
        // not as a SQL LIKE wildcard that matches any single character.
        var session = MakeSession();
        var entries = new[]
        {
            MakeEntry("Bot A", "underscore_test", 1, "assistant", "content without underscore"),
        };
        await _store.SaveSessionAsync(session, entries);

        // Searching for "_" as literal should return no results
        // (the content does not contain '_').
        var results = await _store.SearchAsync("_");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_LiteralPercent_MatchesWhenPresent()
    {
        // A literal "%" in content should be found when searching for "%".
        var session = MakeSession();
        var entries = new[]
        {
            MakeEntry("Bot A", "percentage", 1, "assistant", "growth rate was 50% last year"),
        };
        await _store.SaveSessionAsync(session, entries);

        var results = await _store.SearchAsync("%");

        // The entry contains a literal "%", so it should be found.
        Assert.NotEmpty(results);
        Assert.Contains(results, e => e.Content.Contains('%'));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static DebateSession MakeSession(DateTimeOffset? startedAt = null) =>
        new(Guid.NewGuid(), "test topic", startedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "Bot A", "Good debate.");

    private static MemoryEntry MakeEntry(string bot, string topic, int round, string role, string content) =>
        new(Guid.NewGuid(), bot, topic, round, role, content, DateTimeOffset.UtcNow, [topic]);

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// S3MemoryStore tests (mocked IAmazonS3)
// ──────────────────────────────────────────────────────────────────────────────

public sealed class S3MemoryStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task SaveSessionAsync_CallsPutObjectWithCorrectKey()
    {
        var mockS3 = new Mock<IAmazonS3>();
        mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PutObjectResponse());

        using var store = new S3MemoryStore(mockS3.Object, bucket: "my-bucket", prefix: "vw/");

        var session = MakeSession();
        await store.SaveSessionAsync(session, []);

        mockS3.Verify(s => s.PutObjectAsync(
            It.Is<PutObjectRequest>(r =>
                r.BucketName == "my-bucket" &&
                r.Key        == $"vw/{session.SessionId}.json" &&
                r.ContentType == "application/json" &&
                r.ServerSideEncryptionMethod == ServerSideEncryptionMethod.AES256),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SaveSessionAsync_ContentBodyIsValidJson()
    {
        PutObjectRequest? captured = null;
        var mockS3 = new Mock<IAmazonS3>();
        mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
              .Callback<PutObjectRequest, CancellationToken>((r, _) => captured = r)
              .ReturnsAsync(new PutObjectResponse());

        using var store = new S3MemoryStore(mockS3.Object, bucket: "b", prefix: "p/");
        var entry   = MakeEntry("Bot A", "AI", 1, "assistant", "Interesting perspective");
        var session = MakeSession();

        await store.SaveSessionAsync(session, [entry]);

        Assert.NotNull(captured);
        var doc = JsonDocument.Parse(captured!.ContentBody);
        Assert.True(doc.RootElement.TryGetProperty("session",  out _));
        Assert.True(doc.RootElement.TryGetProperty("entries",  out _));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static DebateSession MakeSession() =>
        new(Guid.NewGuid(), "AI ethics", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "Tie", "Good points made.");

    private static MemoryEntry MakeEntry(string bot, string topic, int round, string role, string content) =>
        new(Guid.NewGuid(), bot, topic, round, role, content, DateTimeOffset.UtcNow, [topic]);
}

// ──────────────────────────────────────────────────────────────────────────────
// HybridMemoryStore tests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class HybridMemoryStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vibewars_hybrid_{Guid.NewGuid():N}.db");
    private readonly SqliteMemoryStore _sqlite;
    private readonly Mock<IAmazonS3>   _mockS3;
    private readonly S3MemoryStore     _s3Store;
    private readonly HybridMemoryStore _hybrid;

    public HybridMemoryStoreTests()
    {
        _sqlite  = new SqliteMemoryStore(_dbPath);
        _mockS3  = new Mock<IAmazonS3>();
        _mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new PutObjectResponse());
        _s3Store = new S3MemoryStore(_mockS3.Object, bucket: "test", prefix: "vw/");
        _hybrid  = new HybridMemoryStore(_sqlite, _s3Store);
    }

    [Fact]
    public async Task SaveSessionAsync_WritesBothToSqliteAndS3()
    {
        var session = MakeSession();
        var entry   = MakeEntry("Bot A", "topic", 1, "assistant", "hybrid test");

        await _hybrid.SaveSessionAsync(session, [entry]);

        // Give the fire-and-forget S3 write time to complete
        await Task.Delay(200);

        // SQLite write should be synchronous
        var sqliteEntries = await _sqlite.GetSessionEntriesAsync(session.SessionId);
        Assert.Single(sqliteEntries);

        // S3 write should eventually be called
        _mockS3.Verify(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_UsesSqlite()
    {
        var session = MakeSession();
        var entry   = MakeEntry("Bot A", "search topic", 1, "assistant", "unique search content xyz");

        await _hybrid.SaveSessionAsync(session, [entry]);
        await Task.Delay(100);

        var results = await _hybrid.SearchAsync("unique search content xyz");

        Assert.NotEmpty(results);
        // S3 SearchAsync should NOT have been called (prefer SQLite)
        _mockS3.Verify(s => s.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListSessionsAsync_UsesSqlite()
    {
        var session = MakeSession();
        await _hybrid.SaveSessionAsync(session, []);
        await Task.Delay(100);

        var sessions = await _hybrid.ListSessionsAsync(10);

        Assert.NotEmpty(sessions);
        _mockS3.Verify(s => s.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSessionEntriesAsync_PrefersSqlite_DoesNotCallS3WhenFoundLocally()
    {
        var session = MakeSession();
        var entry   = MakeEntry("Bot A", "local topic", 1, "assistant", "local content");

        await _hybrid.SaveSessionAsync(session, [entry]);
        await Task.Delay(100);

        var entries = await _hybrid.GetSessionEntriesAsync(session.SessionId);

        Assert.Single(entries);
        _mockS3.Verify(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static DebateSession MakeSession() =>
        new(Guid.NewGuid(), "hybrid topic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "Bot A", "Synthesis.");

    private static MemoryEntry MakeEntry(string bot, string topic, int round, string role, string content) =>
        new(Guid.NewGuid(), bot, topic, round, role, content, DateTimeOffset.UtcNow, [topic]);

    public void Dispose()
    {
        _hybrid.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
