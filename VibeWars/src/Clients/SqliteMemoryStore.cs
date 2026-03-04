using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Microsoft.Data.Sqlite;
using VibeWars.Models;

namespace VibeWars.Clients;

/// <summary>
/// <see cref="IMemoryStore"/> implementation backed by a local SQLite database.
/// Uses an FTS5 virtual table for efficient keyword search over entry content.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore, IDisposable
{
    private const string DefaultFormat = "Freeform";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy()
        }
    };

    private readonly SqliteConnection _db;

    /// <param name="dbPath">
    /// Path to the SQLite file.  Defaults to <c>~/.vibewars/memory.db</c>.
    /// Override with the <c>VIBEWARS_DB_PATH</c> environment variable.
    /// </param>
    public SqliteMemoryStore(string? dbPath = null)
    {
        dbPath ??= Environment.GetEnvironmentVariable("VIBEWARS_DB_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vibewars", "memory.db");

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            // Restrict the directory to the current user on Unix-like systems
            // to prevent other local users from reading debate history.
            if (!OperatingSystem.IsWindows())
            {
                try { System.IO.File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch { /* non-fatal: best-effort */ }
            }
        }

        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();

        // Enable WAL for better concurrent read performance.
        using var wal = _db.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();

        Migrate();
    }

    // ── Schema migrations ─────────────────────────────────────────────────────

    private void Migrate()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaVersion (version INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO SchemaVersion VALUES (0);

            CREATE TABLE IF NOT EXISTS DebateSessions (
                SessionId      TEXT PRIMARY KEY,
                Topic          TEXT NOT NULL,
                StartedAt      TEXT NOT NULL,
                EndedAt        TEXT NOT NULL,
                OverallWinner  TEXT NOT NULL,
                FinalSynthesis TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MemoryEntries (
                Id        TEXT PRIMARY KEY,
                BotName   TEXT NOT NULL,
                Topic     TEXT NOT NULL,
                Round     INTEGER NOT NULL,
                Role      TEXT NOT NULL,
                Content   TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                Tags      TEXT NOT NULL,
                SessionId TEXT NOT NULL,
                FOREIGN KEY (SessionId) REFERENCES DebateSessions(SessionId)
            );

            CREATE INDEX IF NOT EXISTS idx_entries_bot_topic
                ON MemoryEntries(BotName, Topic);

            CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts
                USING fts5(Id UNINDEXED, Content, Tags, content=MemoryEntries, content_rowid=rowid);

            CREATE TRIGGER IF NOT EXISTS entries_ai AFTER INSERT ON MemoryEntries BEGIN
                INSERT INTO memory_fts(rowid, Id, Content, Tags)
                VALUES (new.rowid, new.Id, new.Content, new.Tags);
            END;

            CREATE TRIGGER IF NOT EXISTS entries_ad AFTER DELETE ON MemoryEntries BEGIN
                INSERT INTO memory_fts(memory_fts, rowid, Id, Content, Tags)
                VALUES ('delete', old.rowid, old.Id, old.Content, old.Tags);
            END;

            CREATE TRIGGER IF NOT EXISTS entries_au AFTER UPDATE ON MemoryEntries BEGIN
                INSERT INTO memory_fts(memory_fts, rowid, Id, Content, Tags)
                VALUES ('delete', old.rowid, old.Id, old.Content, old.Tags);
                INSERT INTO memory_fts(rowid, Id, Content, Tags)
                VALUES (new.rowid, new.Id, new.Content, new.Tags);
            END;
            """;
        cmd.ExecuteNonQuery();

        // Migration v1: add Embedding column if missing
        using var versionCmd = _db.CreateCommand();
        versionCmd.CommandText = "SELECT version FROM SchemaVersion;";
        var version = Convert.ToInt32(versionCmd.ExecuteScalar());

        if (version < 1)
        {
            using var addColCmd = _db.CreateCommand();
            try
            {
                addColCmd.CommandText = "ALTER TABLE MemoryEntries ADD COLUMN Embedding BLOB;";
                addColCmd.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* column already exists */ }

            using var updateVersionCmd = _db.CreateCommand();
            updateVersionCmd.CommandText = "UPDATE SchemaVersion SET version = 1;";
            updateVersionCmd.ExecuteNonQuery();
        }

        if (version < 2)
        {
            using var addFormatCmd = _db.CreateCommand();
            try
            {
                addFormatCmd.CommandText = "ALTER TABLE DebateSessions ADD COLUMN Format TEXT NOT NULL DEFAULT 'Freeform';";
                addFormatCmd.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* column already exists */ }

            using var updateVersionCmd2 = _db.CreateCommand();
            updateVersionCmd2.CommandText = "UPDATE SchemaVersion SET version = 2;";
            updateVersionCmd2.ExecuteNonQuery();
        }

        if (version < 3)
        {
            // Add TotalTokens, EstimatedCostUsd, and Complexity columns to DebateSessions
            string[] v3Columns =
            [
                "ALTER TABLE DebateSessions ADD COLUMN TotalTokens INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE DebateSessions ADD COLUMN EstimatedCostUsd REAL;",
                "ALTER TABLE DebateSessions ADD COLUMN Complexity TEXT NOT NULL DEFAULT 'Standard';"
            ];
            foreach (var sql in v3Columns)
            {
                using var colCmd = _db.CreateCommand();
                try { colCmd.CommandText = sql; colCmd.ExecuteNonQuery(); }
                catch (Microsoft.Data.Sqlite.SqliteException) { /* column already exists */ }
            }

            using var updateVersionCmd3 = _db.CreateCommand();
            updateVersionCmd3.CommandText = "UPDATE SchemaVersion SET version = 3;";
            updateVersionCmd3.ExecuteNonQuery();
        }

        if (version < 4)
        {
            // Wave 5: Opponent modeling table
            using var oppCmd = _db.CreateCommand();
            oppCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS OpponentProfiles (
                    BotId       TEXT NOT NULL,
                    OpponentId  TEXT NOT NULL,
                    TacticName  TEXT NOT NULL,
                    TimesUsed   INTEGER NOT NULL DEFAULT 0,
                    TimesWon    INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (BotId, OpponentId, TacticName)
                );
                """;
            oppCmd.ExecuteNonQuery();

            // Wave 6: PersonalityTraits table (created by PersonalityEvolutionService
            // but we ensure it exists here for consistency)
            using var persCmd = _db.CreateCommand();
            persCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS PersonalityTraits (
                    ContestantId TEXT NOT NULL,
                    Trait        TEXT NOT NULL,
                    Intensity    REAL NOT NULL DEFAULT 0.0,
                    LastUpdated  TEXT NOT NULL,
                    PRIMARY KEY (ContestantId, Trait)
                );
                """;
            persCmd.ExecuteNonQuery();

            using var updateVersionCmd4 = _db.CreateCommand();
            updateVersionCmd4.CommandText = "UPDATE SchemaVersion SET version = 4;";
            updateVersionCmd4.ExecuteNonQuery();
        }
    }

    // ── IMemoryStore ──────────────────────────────────────────────────────────

    public Task SaveSessionAsync(DebateSession session, IEnumerable<MemoryEntry> entries, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var tx = _db.BeginTransaction();
        try
        {
            // Upsert session row
            using var sessCmd = _db.CreateCommand();
            sessCmd.Transaction = tx;
            sessCmd.CommandText = """
                INSERT OR REPLACE INTO DebateSessions
                    (SessionId, Topic, StartedAt, EndedAt, OverallWinner, FinalSynthesis, Format, TotalTokens, EstimatedCostUsd, Complexity)
                VALUES
                    (@sid, @topic, @start, @end, @winner, @synthesis, @format, @tokens, @cost, @complexity);
                """;
            sessCmd.Parameters.AddWithValue("@sid",        session.SessionId.ToString());
            sessCmd.Parameters.AddWithValue("@topic",      session.Topic);
            sessCmd.Parameters.AddWithValue("@start",      session.StartedAt.ToString("O"));
            sessCmd.Parameters.AddWithValue("@end",        session.EndedAt.ToString("O"));
            sessCmd.Parameters.AddWithValue("@winner",     session.OverallWinner);
            sessCmd.Parameters.AddWithValue("@synthesis",  session.FinalSynthesis);
            sessCmd.Parameters.AddWithValue("@format",     session.Format ?? DefaultFormat);
            sessCmd.Parameters.AddWithValue("@tokens",     session.TotalTokens);
            sessCmd.Parameters.AddWithValue("@cost",       session.EstimatedCostUsd.HasValue ? (object)session.EstimatedCostUsd.Value : DBNull.Value);
            sessCmd.Parameters.AddWithValue("@complexity", session.Complexity ?? "Standard");
            sessCmd.ExecuteNonQuery();

            // Insert entry rows
            using var entCmd = _db.CreateCommand();
            entCmd.Transaction = tx;
            entCmd.CommandText = """
                INSERT OR IGNORE INTO MemoryEntries
                    (Id, BotName, Topic, Round, Role, Content, Timestamp, Tags, SessionId)
                VALUES
                    (@id, @bot, @topic, @round, @role, @content, @ts, @tags, @sid);
                """;

            var pId      = entCmd.Parameters.Add("@id",      SqliteType.Text);
            var pBot     = entCmd.Parameters.Add("@bot",     SqliteType.Text);
            var pTopic   = entCmd.Parameters.Add("@topic",   SqliteType.Text);
            var pRound   = entCmd.Parameters.Add("@round",   SqliteType.Integer);
            var pRole    = entCmd.Parameters.Add("@role",    SqliteType.Text);
            var pContent = entCmd.Parameters.Add("@content", SqliteType.Text);
            var pTs      = entCmd.Parameters.Add("@ts",      SqliteType.Text);
            var pTags    = entCmd.Parameters.Add("@tags",    SqliteType.Text);
            var pSid     = entCmd.Parameters.Add("@sid",     SqliteType.Text);

            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                pId.Value      = e.Id.ToString();
                pBot.Value     = e.BotName;
                pTopic.Value   = e.Topic;
                pRound.Value   = e.Round;
                pRole.Value    = e.Role;
                pContent.Value = e.Content;
                pTs.Value      = e.Timestamp.ToString("O");
                pTags.Value    = JsonConvert.SerializeObject(e.Tags, JsonSettings);
                pSid.Value     = session.SessionId.ToString();
                entCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // FTS5 match on content + tags; fall back to LIKE if the query has
        // characters that would break FTS5 syntax (e.g. bare special chars).
        IReadOnlyList<MemoryEntry> results;
        try
        {
            results = FtsSearch(query, topK);
        }
        catch (SqliteException)
        {
            results = LikeSearch(query, topK);
        }

        return Task.FromResult(results);
    }

    private IReadOnlyList<MemoryEntry> FtsSearch(string query, int topK)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.BotName, e.Topic, e.Round, e.Role, e.Content, e.Timestamp, e.Tags
            FROM memory_fts f
            JOIN MemoryEntries e ON e.rowid = f.rowid
            WHERE memory_fts MATCH @q
            ORDER BY rank
            LIMIT @topK;
            """;
        cmd.Parameters.AddWithValue("@q",    query);
        cmd.Parameters.AddWithValue("@topK", topK);
        return ReadEntries(cmd);
    }

    private IReadOnlyList<MemoryEntry> LikeSearch(string query, int topK)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT Id, BotName, Topic, Round, Role, Content, Timestamp, Tags
            FROM MemoryEntries
            WHERE Content LIKE @q ESCAPE '\' OR Tags LIKE @q ESCAPE '\'
            ORDER BY Timestamp DESC
            LIMIT @topK;
            """;
        // Escape LIKE metacharacters so they are treated as literals.
        var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.AddWithValue("@q",    $"%{escaped}%");
        cmd.Parameters.AddWithValue("@topK", topK);
        return ReadEntries(cmd);
    }

    public Task<IReadOnlyList<DebateSession>> ListSessionsAsync(int limit = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT SessionId, Topic, StartedAt, EndedAt, OverallWinner, FinalSynthesis,
                   Format, TotalTokens, EstimatedCostUsd, Complexity
            FROM DebateSessions
            ORDER BY StartedAt DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var sessions = new List<DebateSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new DebateSession(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? DefaultFormat : reader.GetString(6),
                reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : (decimal?)reader.GetDouble(8),
                reader.IsDBNull(9) ? "Standard" : reader.GetString(9)
            ));
        }

        return Task.FromResult<IReadOnlyList<DebateSession>>(sessions);
    }

    public Task<IReadOnlyList<MemoryEntry>> GetSessionEntriesAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT Id, BotName, Topic, Round, Role, Content, Timestamp, Tags
            FROM MemoryEntries
            WHERE SessionId = @sid
            ORDER BY Round, Timestamp;
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId.ToString());
        return Task.FromResult(ReadEntries(cmd));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<MemoryEntry> ReadEntries(SqliteCommand cmd)
    {
        var entries = new List<MemoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var tagsJson = reader.GetString(7);
            var tags = JsonConvert.DeserializeObject<string[]>(tagsJson, JsonSettings) ?? [];
            entries.Add(new MemoryEntry(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                tags
            ));
        }
        return entries;
    }

    /// <summary>
    /// Returns the total number of stored entries for a given topic (all bots combined).
    /// Used by the auto-summarization threshold check.
    /// </summary>
    public int CountEntriesForTopic(string topic)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM MemoryEntries WHERE Topic = @topic;";
        cmd.Parameters.AddWithValue("@topic", topic);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Deletes all sessions and entries from the database.</summary>
    public void ClearAll()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM MemoryEntries;
            DELETE FROM DebateSessions;
            INSERT INTO memory_fts(memory_fts) VALUES('rebuild');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Saves a precomputed embedding for a memory entry.</summary>
    public Task SaveEmbeddingAsync(Guid entryId, float[] embedding, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE MemoryEntries SET Embedding = @emb WHERE Id = @id;";
        var bytes = new byte[embedding.Length * 4];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        cmd.Parameters.AddWithValue("@emb", bytes);
        cmd.Parameters.AddWithValue("@id",  entryId.ToString());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>Returns entries ranked by cosine similarity to the query embedding.</summary>
    public Task<IReadOnlyList<MemoryEntry>> SemanticSearchAsync(float[] queryEmbedding, int topK = 10, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT Id, BotName, Topic, Round, Role, Content, Timestamp, Tags, Embedding FROM MemoryEntries WHERE Embedding IS NOT NULL;";

        var candidates = new List<(MemoryEntry Entry, float Similarity)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var tagsJson = reader.GetString(7);
            var tags = JsonConvert.DeserializeObject<string[]>(tagsJson, JsonSettings) ?? [];
            var entry = new MemoryEntry(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)), tags);

            if (!reader.IsDBNull(8))
            {
                var bytes     = (byte[])reader[8];
                var embedding = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
                var similarity = EmbeddingHelper.CosineSimilarity(queryEmbedding, embedding);
                candidates.Add((entry, similarity));
            }
        }

        var results = candidates
            .OrderByDescending(c => c.Similarity)
            .Take(topK)
            .Select(c => c.Entry)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemoryEntry>>(results);
    }

    /// <summary>Returns up to 100 entries that have no stored embedding.</summary>
    public Task<IReadOnlyList<(Guid Id, string Content)>> GetEntriesWithoutEmbeddingsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT Id, Content FROM MemoryEntries WHERE Embedding IS NULL LIMIT 100;";
        var results = new List<(Guid, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return Task.FromResult<IReadOnlyList<(Guid, string)>>(results);
    }

    public void Dispose()
    {
        _db.Dispose();
        // On Windows, WAL mode keeps the file locked via the connection pool
        // even after disposing the connection. Clearing the pool releases all
        // file handles so callers (e.g. tests) can delete the database file.
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Returns the underlying SQLite connection for use by Wave 3 services that manage their own tables.</summary>
    public SqliteConnection GetConnection() => _db;
}
