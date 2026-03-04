using Microsoft.Data.Sqlite;

namespace VibeWars.Personality;

public record PersonalityTrait(string Name, double Intensity);

public record PersonalityProfile(string ContestantId, IReadOnlyList<PersonalityTrait> Traits);

/// <summary>
/// Tracks emergent personality traits that develop across debates based on
/// win/loss patterns. Traits are injected as subtle system prompt modifiers.
/// </summary>
public sealed class PersonalityEvolutionService
{
    private readonly SqliteConnection _db;

    public PersonalityEvolutionService(SqliteConnection db)
    {
        _db = db;
        CreateTable();
    }

    private void CreateTable()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS PersonalityTraits (
                ContestantId TEXT NOT NULL,
                Trait        TEXT NOT NULL,
                Intensity    REAL NOT NULL DEFAULT 0.0,
                LastUpdated  TEXT NOT NULL,
                PRIMARY KEY (ContestantId, Trait)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static readonly string[] AllTraits = ["Aggressive", "Defensive", "Overconfident", "Cautious", "Adaptable", "Stubborn"];

    public void UpdateAfterDebate(string contestantId, bool won, bool wasUpset, int consecutiveWins, int consecutiveLosses, string tacticUsed)
    {
        var adjustments = new Dictionary<string, double>();

        if (won)
        {
            adjustments["Overconfident"] = consecutiveWins >= 3 ? 0.1 : 0.02;
            adjustments["Cautious"] = -0.05;
            adjustments["Aggressive"] = 0.03;
        }
        else
        {
            adjustments["Cautious"] = consecutiveLosses >= 2 ? 0.1 : 0.03;
            adjustments["Overconfident"] = -0.1;
            adjustments["Defensive"] = 0.05;
        }

        if (wasUpset && won)
        {
            adjustments["Adaptable"] = 0.15;
            adjustments["Aggressive"] = 0.05;
        }
        else if (wasUpset && !won)
        {
            adjustments["Cautious"] = 0.1;
            adjustments["Stubborn"] = -0.05;
        }

        foreach (var (trait, delta) in adjustments)
            AdjustTrait(contestantId, trait, delta);
    }

    private void AdjustTrait(string contestantId, string trait, double delta)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PersonalityTraits (ContestantId, Trait, Intensity, LastUpdated)
            VALUES (@id, @trait, MAX(-1.0, MIN(1.0, @delta)), @now)
            ON CONFLICT(ContestantId, Trait) DO UPDATE SET
                Intensity = MAX(-1.0, MIN(1.0, Intensity + @delta)),
                LastUpdated = @now;
            """;
        cmd.Parameters.AddWithValue("@id", contestantId);
        cmd.Parameters.AddWithValue("@trait", trait);
        cmd.Parameters.AddWithValue("@delta", delta);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public PersonalityProfile GetProfile(string contestantId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT Trait, Intensity FROM PersonalityTraits WHERE ContestantId = @id AND ABS(Intensity) > 0.05 ORDER BY ABS(Intensity) DESC;";
        cmd.Parameters.AddWithValue("@id", contestantId);
        var traits = new List<PersonalityTrait>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            traits.Add(new PersonalityTrait(reader.GetString(0), reader.GetDouble(1)));
        return new PersonalityProfile(contestantId, traits);
    }

    /// <summary>Generates a system prompt supplement based on the bot's evolved personality traits.</summary>
    public static string FormatTraitInjection(PersonalityProfile profile)
    {
        if (profile.Traits.Count == 0) return "";
        var dominant = profile.Traits.Where(t => Math.Abs(t.Intensity) > 0.2).Take(2).ToList();
        if (dominant.Count == 0) return "";

        var descriptions = dominant.Select(t => t.Name switch
        {
            "Overconfident" when t.Intensity > 0.2 => "You're on a hot streak and feel untouchable. Let your confidence show.",
            "Cautious" when t.Intensity > 0.2 => "You've been losing lately. Be methodical and careful with your claims.",
            "Aggressive" when t.Intensity > 0.2 => "You play to win. Go on the offensive and press your advantages hard.",
            "Defensive" when t.Intensity > 0.2 => "Protect your position carefully. Shore up weaknesses before extending.",
            "Adaptable" when t.Intensity > 0.2 => "You've shown ability to adapt. Read the situation and adjust your approach.",
            "Stubborn" when t.Intensity > 0.2 => "You believe strongly in your approach. Don't back down from your core position.",
            "Overconfident" when t.Intensity < -0.2 => "Your confidence has been shaken. Prove yourself with stronger evidence.",
            "Cautious" when t.Intensity < -0.2 => "You've been winning easily. Take bigger swings.",
            _ => ""
        }).Where(d => !string.IsNullOrEmpty(d));

        var combined = string.Join(" ", descriptions);
        return string.IsNullOrWhiteSpace(combined) ? "" : $"[PERSONALITY] {combined}";
    }

    public static string RenderProfile(PersonalityProfile profile)
    {
        if (profile.Traits.Count == 0)
            return $"  {profile.ContestantId}: (no evolved traits yet)";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  {profile.ContestantId}:");
        foreach (var t in profile.Traits)
        {
            var bar = t.Intensity > 0 ? new string('+', (int)(t.Intensity * 10)) : new string('-', (int)(Math.Abs(t.Intensity) * 10));
            sb.AppendLine($"    {t.Name,-15} [{bar,-10}] {t.Intensity:+0.00;-0.00;0.00}");
        }
        return sb.ToString();
    }
}
