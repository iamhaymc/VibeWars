using System.Text;
using VibeWars.Models;

namespace VibeWars.Reports;

/// <summary>
/// Generates podcast-style scripts from debate sessions.
/// </summary>
public static class PodcastScriptGenerator
{
    /// <summary>
    /// Estimates runtime in seconds based on word count across assistant/commentary entries.
    /// Assumes 150 words per minute speaking pace.
    /// </summary>
    public static int EstimateRuntimeSeconds(IReadOnlyList<MemoryEntry> entries)
    {
        var wordCount = entries
            .Where(e => e.Role == "assistant" || e.Tags.Contains("commentary"))
            .Sum(e => e.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        return (int)Math.Round(wordCount / 150.0 * 60);
    }

    /// <summary>
    /// Formats a runtime estimate in seconds as "X min Y sec".
    /// </summary>
    public static string FormatRuntimeEstimate(int seconds)
    {
        var minutes = seconds / 60;
        var secs = seconds % 60;
        return $"{minutes} min {secs} sec";
    }

    /// <summary>
    /// Generates a full podcast script from a debate session.
    /// </summary>
    public static string Generate(
        DebateSession session,
        IReadOnlyList<MemoryEntry> entries,
        string? voiceA = null,
        string? voiceB = null)
    {
        var sb = new StringBuilder();

        var participants = entries
            .Where(e => e.Role == "assistant")
            .Select(e => e.BotName)
            .Distinct()
            .ToList();

        var runtimeSecs = EstimateRuntimeSeconds(entries);

        // Production header
        sb.AppendLine("=== PODCAST SCRIPT ===");
        sb.AppendLine($"SHOW TITLE: VibeWars Debate Podcast");
        sb.AppendLine($"EPISODE TOPIC: {session.Topic}");
        sb.AppendLine($"DATE: {session.StartedAt:yyyy-MM-dd}");
        sb.AppendLine($"PARTICIPANTS: {string.Join(", ", participants)}");
        sb.AppendLine($"RUNTIME ESTIMATE: {FormatRuntimeEstimate(runtimeSecs)}");
        if (voiceA != null) sb.AppendLine($"VOICE A: {voiceA}");
        if (voiceB != null) sb.AppendLine($"VOICE B: {voiceB}");
        sb.AppendLine();

        sb.AppendLine("[OPENING MUSIC — 5 seconds]");
        sb.AppendLine();

        var rounds = entries
            .Where(e => e.Round > 0)
            .GroupBy(e => e.Round)
            .OrderBy(g => g.Key)
            .ToList();

        for (int i = 0; i < rounds.Count; i++)
        {
            var roundGroup = rounds[i];
            sb.AppendLine($"--- Round {roundGroup.Key} ---");
            sb.AppendLine();

            foreach (var entry in roundGroup.OrderBy(e => e.Timestamp))
            {
                if (entry.BotName == "Judge" && entry.Tags.Contains("verdict"))
                {
                    sb.AppendLine($"HOST/REFEREE: \"{entry.Content}\"");
                }
                else if (entry.Role == "assistant" || entry.Tags.Contains("commentary"))
                {
                    var speakerName = entry.BotName.ToUpper().Replace(" ", "_");
                    if (voiceA != null && entry.BotName == "Bot A")
                        sb.AppendLine($"[Voice: {voiceA}]");
                    else if (voiceB != null && entry.BotName == "Bot B")
                        sb.AppendLine($"[Voice: {voiceB}]");
                    sb.AppendLine($"{speakerName}: \"{entry.Content}\"");
                }
                sb.AppendLine();
            }

            if (i < rounds.Count - 1)
                sb.AppendLine("[TRANSITION STING]");
            sb.AppendLine();
        }

        sb.AppendLine("[CLOSING]");
        sb.AppendLine();

        // Fact check appendix
        var factCheckEntries = entries.Where(e => e.Tags.Contains("fact-check")).ToList();
        if (factCheckEntries.Count > 0)
        {
            sb.AppendLine("[FACT CHECK NOTES]");
            foreach (var fce in factCheckEntries)
            {
                sb.AppendLine($"Round {fce.Round} — {fce.BotName}:");
                sb.AppendLine(fce.Content);
                sb.AppendLine();
            }
        }

        // Show notes with key topics/tags
        sb.AppendLine("[SHOW NOTES]");
        var tags = entries
            .SelectMany(e => e.Tags)
            .Distinct()
            .Where(t => t != "verdict" && t != "fact-check" && t != "commentary")
            .ToList();
        foreach (var tag in tags)
            sb.AppendLine($"  - {tag}");
        sb.AppendLine();

        return sb.ToString();
    }
}
