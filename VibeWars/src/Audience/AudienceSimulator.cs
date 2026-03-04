using System.Text.Json;

namespace VibeWars.Audience;

/// <summary>Result of an audience poll for a single round.</summary>
public record AudienceShiftResult(int ShiftA, int ShiftB, string Mood);

/// <summary>
/// Simulates a virtual audience whose support shifts based on debate performance.
/// SupportA + SupportB always equals 100.
/// </summary>
public class AudienceSimulator
{
    public int SupportA { get; private set; }
    public int SupportB { get; private set; }

    public AudienceSimulator(int startSupportA = 50, int startSupportB = 50)
    {
        int a = Math.Clamp(startSupportA, 0, 100);
        int b = Math.Clamp(startSupportB, 0, 100);
        int total = a + b;
        if (total == 0)
        {
            SupportA = 50;
            SupportB = 50;
        }
        else
        {
            SupportA = (int)Math.Round(100.0 * a / total);
            SupportB = 100 - SupportA;
        }
    }

    /// <summary>
    /// Applies a shift result, clamping values so neither side goes below 0 or above 100
    /// and the two values always sum to 100.
    /// </summary>
    public void ApplyShift(AudienceShiftResult shift)
    {
        int newA = Math.Clamp(SupportA + shift.ShiftA, 0, 100);
        int newB = Math.Clamp(SupportB + shift.ShiftB, 0, 100);

        // Normalize so they sum to 100
        int total = newA + newB;
        if (total == 0)
        {
            SupportA = 50;
            SupportB = 50;
        }
        else
        {
            SupportA = (int)Math.Round(100.0 * newA / total);
            SupportB = 100 - SupportA;
        }
    }

    /// <summary>Parses an audience shift JSON payload; returns null on failure.</summary>
    public static AudienceShiftResult? ParseShiftResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root    = doc.RootElement;
            int shiftA  = root.GetProperty("shift_a").GetInt32();
            int shiftB  = root.GetProperty("shift_b").GetInt32();
            string mood = root.GetProperty("mood").GetString() ?? "";
            return new AudienceShiftResult(shiftA, shiftB, mood);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renders a two-row ASCII bar chart showing audience support percentages.
    /// Uses ▓ for the supported portion and ░ for the remainder.
    /// Total bar width is 35 characters.
    /// </summary>
    public string RenderPollBar(string botAName, string botBName)
    {
        const int barWidth = 35;

        int filledA = (int)Math.Round(barWidth * SupportA / 100.0);
        int emptyA  = barWidth - filledA;

        int filledB = (int)Math.Round(barWidth * SupportB / 100.0);
        int emptyB  = barWidth - filledB;

        string barA = new string('▓', filledA) + new string('░', emptyA);
        string barB = new string('░', emptyB)  + new string('▓', filledB);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📊 Audience Poll  ──────────────────────────────────────────");
        sb.AppendLine($"{botAName,-8}  {barA}  {SupportA}%");
        sb.AppendLine($"{botBName,-8}  {barB}  {SupportB}%");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Maps a mood string to an emoji.</summary>
    public static string MoodEmoji(string mood) => mood.ToLowerInvariant() switch
    {
        "excited"   => "😊",
        "skeptical" => "😤",
        "engaged"   => "🤔",
        "bored"     => "😴",
        _           => "🎭",
    };
}
