using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Strategy;

public sealed class StrategyEngine
{
    private readonly SqliteConnection _db;

    private const string StrategistSystem = """
You are a master debate tactician. Review the opponent's past arguments and the debater's own history. Select the single most damaging rhetorical tactic for the next turn. Output JSON: {"tactic": "...", "target_weakness": "...", "execution_hint": "...", "confidence": 0.0}
""";

    public StrategyEngine(SqliteConnection db)
    {
        _db = db;
        CreateTables();
    }

    private void CreateTables()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS StrategyRecords (
                ContestantId TEXT NOT NULL,
                TacticName   TEXT NOT NULL,
                UsedInRound  INTEGER NOT NULL,
                SessionId    TEXT NOT NULL,
                RoundWon     INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<DebateStrategy> SelectStrategyAsync(
        IChatClient client,
        string opponentHistory,
        string selfHistory,
        IReadOnlyList<StrategyRecord> pastSuccesses,
        CancellationToken ct = default)
    {
        try
        {
            var rateHint = "";
            if (pastSuccesses.Count > 0)
            {
                var rates = GetHistoricalTacticSuccessRates(pastSuccesses);
                if (rates.Count > 0)
                {
                    var best = rates.OrderByDescending(kv => kv.Value).First();
                    rateHint = $" Historically, '{best.Key}' has been most effective with a {best.Value:P0} win rate.";
                }
            }

            var prompt = $"Opponent history:\n{opponentHistory}\n\nYour history:\n{selfHistory}\n{rateHint}";
            var (reply, _) = await client.ChatAsync(StrategistSystem, [new ChatMessage("user", prompt)], ct);
            return ParseStrategy(reply);
        }
        catch
        {
            return new DebateStrategy("Adaptive", "", "Adapt to the opponent's most recent argument.", 0.5);
        }
    }

    public static DebateStrategy ParseStrategy(string json)
    {
        try
        {
            var trimmed = json.Trim();
            if (trimmed.StartsWith("```"))
            {
                var start = trimmed.IndexOf('{');
                var end   = trimmed.LastIndexOf('}');
                if (start >= 0 && end > start) trimmed = trimmed[start..(end + 1)];
            }
            using var doc  = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var tactic    = root.TryGetProperty("tactic",           out var t) ? t.GetString() ?? "Adaptive" : "Adaptive";
            var weakness  = root.TryGetProperty("target_weakness",  out var w) ? w.GetString() ?? "" : "";
            var hint      = root.TryGetProperty("execution_hint",   out var h) ? h.GetString() ?? "" : "";
            var confidence = root.TryGetProperty("confidence",      out var c) ? c.GetDouble() : 0.5;
            return new DebateStrategy(tactic, weakness, hint, Math.Clamp(confidence, 0.0, 1.0));
        }
        catch
        {
            return new DebateStrategy("Adaptive", "", "Adapt to the opponent's most recent argument.", 0.5);
        }
    }

    public void RecordOutcome(string contestantId, string tacticName, int round, string sessionId, int roundWon)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO StrategyRecords (ContestantId, TacticName, UsedInRound, SessionId, RoundWon)
            VALUES (@id, @tactic, @round, @session, @won);
            """;
        cmd.Parameters.AddWithValue("@id",      contestantId);
        cmd.Parameters.AddWithValue("@tactic",  tacticName);
        cmd.Parameters.AddWithValue("@round",   round);
        cmd.Parameters.AddWithValue("@session", sessionId);
        cmd.Parameters.AddWithValue("@won",     roundWon);
        cmd.ExecuteNonQuery();
    }

    public List<StrategyRecord> GetPastSuccesses(string contestantId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT ContestantId, TacticName, UsedInRound, SessionId, RoundWon
            FROM StrategyRecords WHERE ContestantId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", contestantId);
        var results = new List<StrategyRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new StrategyRecord(
                reader.GetString(0), reader.GetString(1),
                reader.GetInt32(2),  reader.GetString(3), reader.GetInt32(4)));
        return results;
    }

    public static Dictionary<string, double> GetHistoricalTacticSuccessRates(IReadOnlyList<StrategyRecord> records)
    {
        return records
            .GroupBy(r => r.TacticName)
            .ToDictionary(
                g => g.Key,
                g => (double)g.Sum(r => r.RoundWon) / g.Count());
    }

    public static string FormatStrategyHint(DebateStrategy strategy)
        => $"[STRATEGY] Your planned tactic: {strategy.TacticName} — {strategy.ExecutionHint}. Execute this subtly.";

    // ── Opponent modeling ─────────────────────────────────────────────────────

    public void RecordOpponentOutcome(string botId, string opponentId, string tacticName, bool won)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO OpponentProfiles (BotId, OpponentId, TacticName, TimesUsed, TimesWon)
            VALUES (@bot, @opp, @tactic, 1, @won)
            ON CONFLICT(BotId, OpponentId, TacticName) DO UPDATE SET
                TimesUsed = TimesUsed + 1,
                TimesWon  = TimesWon + @won;
            """;
        cmd.Parameters.AddWithValue("@bot", botId);
        cmd.Parameters.AddWithValue("@opp", opponentId);
        cmd.Parameters.AddWithValue("@tactic", tacticName);
        cmd.Parameters.AddWithValue("@won", won ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, double> GetOpponentTacticRates(string botId, string opponentId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT TacticName, TimesUsed, TimesWon
            FROM OpponentProfiles WHERE BotId = @bot AND OpponentId = @opp;
            """;
        cmd.Parameters.AddWithValue("@bot", botId);
        cmd.Parameters.AddWithValue("@opp", opponentId);
        var results = new Dictionary<string, double>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var tactic = reader.GetString(0);
            var used = reader.GetInt32(1);
            var won = reader.GetInt32(2);
            if (used > 0) results[tactic] = (double)won / used;
        }
        return results;
    }
}
