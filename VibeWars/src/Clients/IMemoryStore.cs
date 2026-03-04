using VibeWars.Models;

namespace VibeWars.Clients;

/// <summary>
/// Abstraction over the long-term memory store used by VibeWars bots.
/// </summary>
public interface IMemoryStore : IDisposable
{
    /// <summary>Persist a completed debate session and all of its memory entries atomically.</summary>
    Task SaveSessionAsync(DebateSession session, IEnumerable<MemoryEntry> entries, CancellationToken ct = default);

    /// <summary>Full-text search over stored entries; returns the top-K most relevant results.</summary>
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);

    /// <summary>Return the most-recent sessions, newest first.</summary>
    Task<IReadOnlyList<DebateSession>> ListSessionsAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>Return all memory entries that belong to a specific session.</summary>
    Task<IReadOnlyList<MemoryEntry>> GetSessionEntriesAsync(Guid sessionId, CancellationToken ct = default);
}
