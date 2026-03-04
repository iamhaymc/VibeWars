using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Memory;

public sealed class AdversarialBriefingService
{
    public static bool ShouldBrief(IReadOnlyList<MemoryEntry> pastEntries, string topic)
    {
        var relevantEntries = pastEntries
            .Where(e => (e.BotName is "Bot A" or "Bot B") && e.Role == "assistant")
            .ToList();
        return relevantEntries.Count >= 3;
    }

    public static async Task<string> BuildBriefingAsync(
        IMemoryStore store,
        string topic,
        string botName,
        string startingPosition,
        int maxItems = 5,
        CancellationToken ct = default)
    {
        var entries = await store.SearchAsync(topic, topK: 50, ct);
        var opposing = entries
            .Where(e => e.BotName is "Bot A" or "Bot B")
            .Where(e => e.Role == "assistant")
            .Where(e => !string.IsNullOrWhiteSpace(e.Content))
            .Take(maxItems)
            .ToList();

        if (opposing.Count == 0)
            return "";

        var lines = opposing.Select((e, i) => $"{i + 1}. [{e.BotName}] {e.Content}");
        var body  = string.Join("\n", lines);
        return $"INTELLIGENCE BRIEFING — Past effective arguments against your position on '{topic}':\n{body}";
    }

    public static string FormatBriefingNotice(string botName, int count)
        => $"📋 Adversarial briefing loaded: {botName} has {count} past counter-arguments to prepare for.";
}
