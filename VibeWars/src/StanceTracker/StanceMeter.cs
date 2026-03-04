using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.StanceTracker;

public record StanceEntry(int Round, int Stance, IReadOnlyList<string> Concessions);

public sealed class StanceTimeline
{
    private readonly List<StanceEntry> _entries = [];
    public string BotName { get; }
    public IReadOnlyList<StanceEntry> Entries => _entries;
    public StanceTimeline(string botName) => BotName = botName;
    public void Add(StanceEntry entry) => _entries.Add(entry);
    public int? InitialStance => _entries.Count > 0 ? _entries[0].Stance : null;
    public int? FinalStance   => _entries.Count > 0 ? _entries[^1].Stance : null;
    public int StanceDelta    => Math.Abs((FinalStance ?? 0) - (InitialStance ?? 0));
    public int ConcessionCount => _entries.Sum(e => e.Concessions.Count);
}

/// <summary>
/// Runs stance metering prompts to track how bots' positions evolve across rounds.
/// </summary>
public sealed class StanceMeterService
{
    private readonly IChatClient _client;

    private const string StanceMeterSystemPrompt = """
You are an analytical observer. Given the following argument, determine the speaker's position on the debate motion. Return JSON only: {"stance": <-5 to 5>, "concessions": ["..."]}
Where stance: -5 = strongly oppose, 0 = neutral, +5 = strongly support.
""";

    public StanceMeterService(IChatClient client) => _client = client;

    public async Task<StanceEntry> MeasureAsync(string argument, int round, CancellationToken ct = default)
    {
        try
        {
            var (reply, _) = await _client.ChatAsync(
                StanceMeterSystemPrompt,
                [new ChatMessage("user", argument)],
                ct);
            return ParseEntry(reply, round);
        }
        catch
        {
            return new StanceEntry(round, 0, []);
        }
    }

    public static StanceEntry ParseEntry(string json, int round)
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
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var stance = root.TryGetProperty("stance", out var s) ? s.GetInt32() : 0;
            stance = Math.Clamp(stance, -5, 5);
            var concessions = new List<string>();
            if (root.TryGetProperty("concessions", out var conc))
                foreach (var item in conc.EnumerateArray())
                    if (item.GetString() is string c && !string.IsNullOrWhiteSpace(c))
                        concessions.Add(c);
            return new StanceEntry(round, stance, concessions);
        }
        catch
        {
            return new StanceEntry(round, 0, []);
        }
    }

    public static void PrintStanceEvolution(StanceTimeline botA, StanceTimeline botB)
    {
        Console.WriteLine("\nStance evolution (−5 oppose ··· +5 support):");
        PrintTimeline(botA);
        PrintTimeline(botB);
    }

    private static void PrintTimeline(StanceTimeline timeline)
    {
        if (timeline.Entries.Count == 0) return;
        var trend    = string.Join(" → ", timeline.Entries.Select(e => $"R{e.Round}: {(e.Stance >= 0 ? "+" : "")}{e.Stance}"));
        var bar      = RenderBar(timeline.FinalStance ?? 0);
        var movement = (timeline.FinalStance ?? 0) - (timeline.InitialStance ?? 0);
        Console.WriteLine($"  {timeline.BotName}: {bar}  {trend}  (moved {(movement >= 0 ? "+" : "")}{movement} → Δ{timeline.StanceDelta})");
    }

    private static string RenderBar(int stance)
    {
        // Bar from -5 to +5, 11 positions (indices 0..10)
        var pos = stance + 5; // 0..10
        var chars = new char[11];
        for (var i = 0; i < 11; i++)
            chars[i] = i == pos ? '█' : '░';
        return new string(chars);
    }

    public static double CalculateIntellectualProgressScore(
        StanceTimeline botA, StanceTimeline botB, int maxRounds, double reflectionQualityBonus = 0.0)
    {
        if (maxRounds == 0) return 0;
        var totalConcessions = botA.ConcessionCount + botB.ConcessionCount;
        return (botA.StanceDelta + botB.StanceDelta + totalConcessions * 0.5) / maxRounds + reflectionQualityBonus;
    }
}
