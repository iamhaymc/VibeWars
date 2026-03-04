using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Planning;

public record LookaheadResult(string SelectedArgument, string AnticipatedCounter, double NetScore);

/// <summary>
/// 1-ply lookahead: generates candidate arguments, simulates opponent counters,
/// and selects the argument whose counter is weakest (hardest to rebut).
/// </summary>
public sealed class LookaheadService
{
    private readonly IChatClient _client;

    public LookaheadService(IChatClient client) => _client = client;

    public async Task<LookaheadResult> SelectBestArgumentAsync(
        string systemPrompt, IReadOnlyList<ChatMessage> history,
        string opponentSystemHint, CancellationToken ct = default)
    {
        try
        {
            // Generate 2 candidate argument sketches
            var sketchPrompt = "Generate exactly 2 different argument approaches for your next turn. " +
                "Return JSON: {\"sketches\": [\"approach 1...\", \"approach 2...\"]}";
            var sketchHistory = new List<ChatMessage>(history) { new("user", sketchPrompt) };
            var (sketchReply, _) = await _client.ChatAsync(systemPrompt, sketchHistory, ct);
            var sketches = ParseSketches(sketchReply);
            if (sketches.Count == 0)
                return new LookaheadResult("", "", 0);

            // For each sketch, simulate opponent's best counter
            var bestNet = double.MinValue;
            var bestSketch = sketches[0];
            var bestCounter = "";

            foreach (var sketch in sketches)
            {
                var counterPrompt = $"Your opponent just argued: \"{sketch}\"\nWhat is the single strongest counter-argument in 1-2 sentences?";
                var (counter, _) = await _client.ChatAsync(
                    opponentSystemHint, [new ChatMessage("user", counterPrompt)], ct);
                // Score: shorter counter = weaker response (heuristic)
                var counterStrength = Math.Min(counter.Length / 100.0, 10.0);
                var netScore = 10.0 - counterStrength;
                if (netScore > bestNet)
                {
                    bestNet = netScore;
                    bestSketch = sketch;
                    bestCounter = counter;
                }
            }

            return new LookaheadResult(bestSketch, bestCounter, bestNet);
        }
        catch
        {
            return new LookaheadResult("", "", 0);
        }
    }

    public static List<string> ParseSketches(string json)
    {
        try
        {
            var trimmed = json.Trim();
            if (trimmed.StartsWith("```"))
            {
                var start = trimmed.IndexOf('{');
                var end = trimmed.LastIndexOf('}');
                if (start >= 0 && end > start) trimmed = trimmed[start..(end + 1)];
            }
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("sketches", out var arr))
                return arr.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            return [];
        }
        catch { return []; }
    }
}
