using VibeWars.Analytics;
using VibeWars.Momentum;

namespace VibeWars.Highlights;

public record Highlight(int Round, string BotName, string Content, double Score, string Narrative);

public static class HighlightService
{
    /// <summary>
    /// Extracts the top highlights from a debate using argument scores,
    /// audience shifts, and momentum events.
    /// </summary>
    public static IReadOnlyList<Highlight> ExtractHighlights(
        IReadOnlyList<ArgumentStrengthScore> scores,
        IReadOnlyList<MomentumEvent> momentumEvents,
        IReadOnlyList<(int Round, string BotName, string Content)> arguments,
        int topK = 5)
    {
        var scoreMap = scores.ToDictionary(s => (s.Round, s.BotName), s => s.Composite);
        var momentumRounds = momentumEvents.Select(e => e.Round).ToHashSet();

        var candidates = arguments.Select(a =>
        {
            var composite = scoreMap.TryGetValue((a.Round, a.BotName), out var c) ? c : 5.0;
            var momentumBonus = momentumRounds.Contains(a.Round) ? 3.0 : 0.0;
            var totalScore = composite + momentumBonus;
            return new Highlight(a.Round, a.BotName,
                a.Content.Length > 200 ? a.Content[..200] + "..." : a.Content,
                totalScore, "");
        })
        .OrderByDescending(h => h.Score)
        .Take(topK)
        .ToList();

        return candidates;
    }

    /// <summary>Generates narrative framing for highlights using momentum context.</summary>
    public static IReadOnlyList<Highlight> AddNarratives(
        IReadOnlyList<Highlight> highlights,
        IReadOnlyList<MomentumEvent> momentumEvents)
    {
        var eventsByRound = momentumEvents.ToLookup(e => e.Round);
        return highlights.Select(h =>
        {
            var roundEvents = eventsByRound[h.Round].ToList();
            var narrative = roundEvents.Count > 0
                ? $"Round {h.Round}: {roundEvents[0].Description}"
                : $"Round {h.Round}: {h.BotName} delivered a standout argument (score: {h.Score:F1})";
            return h with { Narrative = narrative };
        }).ToList();
    }

    public static string RenderHighlights(IReadOnlyList<Highlight> highlights)
    {
        if (highlights.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\n  Highlight Reel");
        sb.AppendLine("  " + new string('─', 50));
        foreach (var h in highlights)
        {
            sb.AppendLine($"  [{h.BotName} R{h.Round}] {h.Narrative}");
            var preview = h.Content.Length > 100 ? h.Content[..100] + "..." : h.Content;
            sb.AppendLine($"    \"{preview}\"");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
