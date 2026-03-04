using VibeWars.Models;

namespace VibeWars.Clients;

/// <summary>
/// <see cref="IMemoryStore"/> that writes to both <see cref="SqliteMemoryStore"/>
/// (fast local index) and <see cref="S3MemoryStore"/> (durable cloud archive).
/// <para>
/// Reads and searches prefer SQLite for speed.
/// <see cref="GetSessionEntriesAsync"/> falls back to S3 when the session is not
/// found in the local database.
/// </para>
/// <para>
/// S3 writes are fire-and-forget to avoid blocking the main debate flow;
/// any S3 write failures are logged to stderr but do not propagate.
/// </para>
/// </summary>
public sealed class HybridMemoryStore : IMemoryStore, IDisposable
{
    private readonly SqliteMemoryStore _sqlite;
    private readonly S3MemoryStore _s3;

    /// <summary>Exposes the underlying SQLite store for features that need direct access (ELO, drift, strategy, etc.).</summary>
    public SqliteMemoryStore SqliteStore => _sqlite;

    public HybridMemoryStore(SqliteMemoryStore sqlite, S3MemoryStore s3)
    {
        _sqlite = sqlite;
        _s3     = s3;
    }

    public async Task SaveSessionAsync(DebateSession session, IEnumerable<MemoryEntry> entries, CancellationToken ct = default)
    {
        // Materialise the sequence once so both stores see the same data.
        var entryList = entries as IReadOnlyList<MemoryEntry> ?? entries.ToList();

        // SQLite write is synchronous and must succeed.
        await _sqlite.SaveSessionAsync(session, entryList, ct).ConfigureAwait(false);

        // S3 write is best-effort; fire and forget.
        _ = Task.Run(async () =>
        {
            try
            {
                await _s3.SaveSessionAsync(session, entryList).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HybridMemoryStore] S3 write failed (session {session.SessionId}): {ex.Message}");
            }
        }, CancellationToken.None);
    }

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, CancellationToken ct = default)
        => _sqlite.SearchAsync(query, topK, ct);

    public Task<IReadOnlyList<DebateSession>> ListSessionsAsync(int limit = 50, CancellationToken ct = default)
        => _sqlite.ListSessionsAsync(limit, ct);

    public async Task<IReadOnlyList<MemoryEntry>> GetSessionEntriesAsync(Guid sessionId, CancellationToken ct = default)
    {
        var localEntries = await _sqlite.GetSessionEntriesAsync(sessionId, ct).ConfigureAwait(false);
        if (localEntries.Count > 0)
            return localEntries;

        // Session not in SQLite — fall back to S3.
        return await _s3.GetSessionEntriesAsync(sessionId, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _sqlite.Dispose();
        _s3.Dispose();
    }
}
