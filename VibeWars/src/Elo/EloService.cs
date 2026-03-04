using Microsoft.Data.Sqlite;

namespace VibeWars.Elo;

/// <summary>
/// Persistent ELO rating record for a debate contestant.
/// ContestantId format: "{provider}/{model}/{persona}"
/// </summary>
public class EloRecord
{
    public string ContestantId { get; set; } = "";
    public double Rating { get; set; } = 1200;
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Manages ELO ratings for debate contestants persisted in SQLite.
/// </summary>
public class EloService
{
    private readonly SqliteConnection _db;

    public EloService(SqliteConnection db)
    {
        _db = db;
        CreateTables();
    }

    private void CreateTables()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS EloRecords (
                ContestantId TEXT PRIMARY KEY,
                Rating       REAL    NOT NULL DEFAULT 1200,
                Wins         INTEGER NOT NULL DEFAULT 0,
                Losses       INTEGER NOT NULL DEFAULT 0,
                Draws        INTEGER NOT NULL DEFAULT 0,
                LastUpdated  TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS EloHistory (
                ContestantId TEXT NOT NULL,
                Rating       REAL NOT NULL,
                UpdatedAt    TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Task<EloRecord> GetOrCreateAsync(string contestantId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT ContestantId, Rating, Wins, Losses, Draws, LastUpdated FROM EloRecords WHERE ContestantId = @id;";
        cmd.Parameters.AddWithValue("@id", contestantId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return Task.FromResult(new EloRecord
            {
                ContestantId = reader.GetString(0),
                Rating       = reader.GetDouble(1),
                Wins         = reader.GetInt32(2),
                Losses       = reader.GetInt32(3),
                Draws        = reader.GetInt32(4),
                LastUpdated  = DateTimeOffset.Parse(reader.GetString(5)),
            });
        }

        // Create new record seeded at 1200
        var now = DateTimeOffset.UtcNow;
        using var ins = _db.CreateCommand();
        ins.CommandText = """
            INSERT OR IGNORE INTO EloRecords (ContestantId, Rating, Wins, Losses, Draws, LastUpdated)
            VALUES (@id, 1200, 0, 0, 0, @now);
            """;
        ins.Parameters.AddWithValue("@id",  contestantId);
        ins.Parameters.AddWithValue("@now", now.ToString("O"));
        ins.ExecuteNonQuery();

        return Task.FromResult(new EloRecord { ContestantId = contestantId, LastUpdated = now });
    }

    public async Task UpdateRatingsAsync(
        string winnerContestantId,
        string loserContestantId,
        bool isDraw,
        bool isTournament,
        double kMultiplier = 1.0)
    {
        int kFactor = (int)Math.Round((isTournament ? 16 : 32) * kMultiplier);

        var winner = await GetOrCreateAsync(winnerContestantId);
        var loser  = await GetOrCreateAsync(loserContestantId);

        double scoreW = isDraw ? 0.5 : 1.0;
        double scoreL = isDraw ? 0.5 : 0.0;

        double deltaW = ComputeEloDelta(winner.Rating, loser.Rating, scoreW, kFactor);
        double deltaL = ComputeEloDelta(loser.Rating, winner.Rating, scoreL, kFactor);

        winner.Rating += deltaW;
        loser.Rating  += deltaL;

        var now = DateTimeOffset.UtcNow;
        winner.LastUpdated = now;
        loser.LastUpdated  = now;

        if (isDraw) { winner.Draws++; loser.Draws++; }
        else        { winner.Wins++;  loser.Losses++; }

        UpsertRecord(winner);
        UpsertRecord(loser);

        AppendHistory(winnerContestantId, winner.Rating, now);
        AppendHistory(loserContestantId,  loser.Rating,  now);
    }

    private void UpsertRecord(EloRecord record)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO EloRecords (ContestantId, Rating, Wins, Losses, Draws, LastUpdated)
            VALUES (@id, @rating, @wins, @losses, @draws, @updated);
            """;
        cmd.Parameters.AddWithValue("@id",      record.ContestantId);
        cmd.Parameters.AddWithValue("@rating",  record.Rating);
        cmd.Parameters.AddWithValue("@wins",    record.Wins);
        cmd.Parameters.AddWithValue("@losses",  record.Losses);
        cmd.Parameters.AddWithValue("@draws",   record.Draws);
        cmd.Parameters.AddWithValue("@updated", record.LastUpdated.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private void AppendHistory(string contestantId, double rating, DateTimeOffset updatedAt)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO EloHistory (ContestantId, Rating, UpdatedAt) VALUES (@id, @rating, @at);";
        cmd.Parameters.AddWithValue("@id",     contestantId);
        cmd.Parameters.AddWithValue("@rating", rating);
        cmd.Parameters.AddWithValue("@at",     updatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public Task<List<EloRecord>> GetLeaderboardAsync(
        int topN = 20,
        string? personaFilter = null,
        string? providerFilter = null)
    {
        using var cmd = _db.CreateCommand();

        var conditions = new List<string>();
        if (personaFilter  != null) conditions.Add("ContestantId LIKE @persona");
        if (providerFilter != null) conditions.Add("ContestantId LIKE @provider");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        cmd.CommandText = $"""
            SELECT ContestantId, Rating, Wins, Losses, Draws, LastUpdated
            FROM EloRecords
            {where}
            ORDER BY Rating DESC
            LIMIT @topN;
            """;

        cmd.Parameters.AddWithValue("@topN", topN);
        if (personaFilter  != null) cmd.Parameters.AddWithValue("@persona",  $"%/{personaFilter}");
        if (providerFilter != null) cmd.Parameters.AddWithValue("@provider", $"{providerFilter}/%");

        var results = new List<EloRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EloRecord
            {
                ContestantId = reader.GetString(0),
                Rating       = reader.GetDouble(1),
                Wins         = reader.GetInt32(2),
                Losses       = reader.GetInt32(3),
                Draws        = reader.GetInt32(4),
                LastUpdated  = DateTimeOffset.Parse(reader.GetString(5)),
            });
        }

        return Task.FromResult(results);
    }

    public Task<List<(DateTimeOffset Date, double Rating)>> GetRatingHistoryAsync(string contestantId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT UpdatedAt, Rating FROM EloHistory
            WHERE ContestantId = @id
            ORDER BY UpdatedAt ASC;
            """;
        cmd.Parameters.AddWithValue("@id", contestantId);

        var results = new List<(DateTimeOffset, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((DateTimeOffset.Parse(reader.GetString(0)), reader.GetDouble(1)));

        return Task.FromResult(results);
    }

    /// <summary>Returns true when a contestant has fewer than 5 matches total.</summary>
    public bool IsUnrated(EloRecord record) =>
        record.Wins + record.Losses + record.Draws < 5;

    /// <summary>
    /// Computes the ELO rating change for player A given the outcome.
    /// score: 1.0 = win, 0.5 = draw, 0.0 = loss.
    /// </summary>
    public static double ComputeEloDelta(double ratingA, double ratingB, double score, int kFactor)
    {
        double expected = 1.0 / (1.0 + Math.Pow(10.0, (ratingB - ratingA) / 400.0));
        return kFactor * (score - expected);
    }

    /// <summary>Converts a list of ratings to an ASCII sparkline using block characters.</summary>
    public static string RatingToSparkline(IList<double> ratings)
    {
        if (ratings.Count == 0) return "";

        const string bars = "▁▂▃▄▅▆▇█";
        double min = ratings.Min();
        double max = ratings.Max();
        double range = max - min;

        var sb = new System.Text.StringBuilder();
        foreach (var r in ratings)
        {
            int idx = range < 0.0001
                ? 3
                : (int)Math.Round((r - min) / range * (bars.Length - 1));
            idx = Math.Clamp(idx, 0, bars.Length - 1);
            sb.Append(bars[idx]);
        }
        return sb.ToString();
    }
}
