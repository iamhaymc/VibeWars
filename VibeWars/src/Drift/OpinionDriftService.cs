using Microsoft.Data.Sqlite;

namespace VibeWars.Drift;

/// <summary>Records one bot's stance change across a single debate session.</summary>
public class OpinionDriftRecord
{
    public Guid SessionId { get; set; }
    public string Topic { get; set; } = "";
    public string BotName { get; set; } = "";
    public string Model { get; set; } = "";
    public string Persona { get; set; } = "";
    public int InitialStance { get; set; }
    public int FinalStance { get; set; }
    /// <summary>Absolute value of FinalStance - InitialStance.</summary>
    public int StanceDelta { get; set; }
    public DateTimeOffset SessionDate { get; set; } = DateTimeOffset.UtcNow;
}

public enum DriftTrend
{
    Converging,
    Diverging,
    Stable,
}

/// <summary>
/// Persists and analyzes cross-session opinion drift data in SQLite.
/// </summary>
public class OpinionDriftService
{
    private readonly SqliteConnection _db;

    public OpinionDriftService(SqliteConnection db)
    {
        _db = db;
        CreateTable();
    }

    private void CreateTable()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS OpinionDriftRecords (
                SessionId    TEXT NOT NULL,
                Topic        TEXT NOT NULL,
                BotName      TEXT NOT NULL,
                Model        TEXT NOT NULL,
                Persona      TEXT NOT NULL,
                InitialStance INTEGER NOT NULL,
                FinalStance   INTEGER NOT NULL,
                StanceDelta   INTEGER NOT NULL,
                SessionDate   TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Task SaveDriftRecordAsync(OpinionDriftRecord record)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO OpinionDriftRecords
                (SessionId, Topic, BotName, Model, Persona, InitialStance, FinalStance, StanceDelta, SessionDate)
            VALUES
                (@sid, @topic, @bot, @model, @persona, @initial, @final, @delta, @date);
            """;
        cmd.Parameters.AddWithValue("@sid",     record.SessionId.ToString());
        cmd.Parameters.AddWithValue("@topic",   record.Topic);
        cmd.Parameters.AddWithValue("@bot",     record.BotName);
        cmd.Parameters.AddWithValue("@model",   record.Model);
        cmd.Parameters.AddWithValue("@persona", record.Persona);
        cmd.Parameters.AddWithValue("@initial", record.InitialStance);
        cmd.Parameters.AddWithValue("@final",   record.FinalStance);
        cmd.Parameters.AddWithValue("@delta",   record.StanceDelta);
        cmd.Parameters.AddWithValue("@date",    record.SessionDate.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<List<OpinionDriftRecord>> GetDriftRecordsAsync(string topic)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT SessionId, Topic, BotName, Model, Persona, InitialStance, FinalStance, StanceDelta, SessionDate
            FROM OpinionDriftRecords
            WHERE Topic = @topic
            ORDER BY SessionDate ASC;
            """;
        cmd.Parameters.AddWithValue("@topic", topic);

        var results = new List<OpinionDriftRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new OpinionDriftRecord
            {
                SessionId    = Guid.Parse(reader.GetString(0)),
                Topic        = reader.GetString(1),
                BotName      = reader.GetString(2),
                Model        = reader.GetString(3),
                Persona      = reader.GetString(4),
                InitialStance = reader.GetInt32(5),
                FinalStance  = reader.GetInt32(6),
                StanceDelta  = reader.GetInt32(7),
                SessionDate  = DateTimeOffset.Parse(reader.GetString(8)),
            });
        }

        return Task.FromResult(results);
    }

    /// <summary>
    /// Computes drift velocity as total stance change divided by number of sessions.
    /// Returns 0 when records is empty.
    /// </summary>
    public static double ComputeDriftVelocity(IList<OpinionDriftRecord> records)
    {
        if (records.Count == 0) return 0;
        double totalChange = records.Sum(r => (double)(r.FinalStance - r.InitialStance));
        return totalChange / records.Count;
    }

    /// <summary>
    /// Classifies a drift velocity as Stable, Converging, or Diverging.
    /// Stable: <c>|velocity| &lt; 0.5</c>
    /// Converging: <c>velocity &lt; -0.5</c> (stances moving toward zero)
    /// Diverging: <c>velocity &gt; 0.5</c>
    /// </summary>
    public static DriftTrend ClassifyTrend(double velocity) => velocity switch
    {
        < -0.5 => DriftTrend.Converging,
        > 0.5  => DriftTrend.Diverging,
        _      => DriftTrend.Stable,
    };

    /// <summary>
    /// Renders an ASCII timeline showing initial and final stance per session.
    /// </summary>
    public static string RenderDriftTimeline(IList<OpinionDriftRecord> records)
    {
        if (records.Count == 0) return "(no drift records)";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("── Opinion Drift Timeline ──────────────────────────────────");
        sb.AppendLine($"{"Session",-8} {"Bot",-12} {"Initial",8} {"Final",6} {"Delta",6}");
        sb.AppendLine(new string('─', 52));

        foreach (var r in records)
        {
            string arrow = r.FinalStance > r.InitialStance ? "▲" :
                           r.FinalStance < r.InitialStance ? "▼" : "─";
            sb.AppendLine($"{r.SessionDate:MM/dd}    {r.BotName,-12} {r.InitialStance,8} {r.FinalStance,6} {arrow}{Math.Abs(r.StanceDelta),5}");
        }

        double velocity = ComputeDriftVelocity(records);
        var trend = ClassifyTrend(velocity);
        sb.AppendLine(new string('─', 52));
        sb.AppendLine($"Drift velocity: {velocity:+0.00;-0.00;0.00}  Trend: {trend}");

        return sb.ToString().TrimEnd();
    }
}
