using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.HiddenObjective;

public enum ObjectiveCategory { Rhetorical, Epistemic, Strategic, Social, Meta }

public record HiddenObjective(string Text, ObjectiveCategory Category);

public static class ObjectiveLibrary
{
    private static readonly IReadOnlyList<HiddenObjective> _all = new[]
    {
        // Rhetorical
        new HiddenObjective("Never use the word 'however'. Build arguments that always escalate in intensity.", ObjectiveCategory.Rhetorical),
        new HiddenObjective("Argue your position using only analogies — never state a direct claim.", ObjectiveCategory.Rhetorical),
        // Epistemic
        new HiddenObjective("Subtly introduce one unverifiable statistic per round without drawing attention to it.", ObjectiveCategory.Epistemic),
        new HiddenObjective("Maintain a stance of absolute certainty even when conceding sub-points.", ObjectiveCategory.Epistemic),
        // Strategic
        new HiddenObjective("Agree with your opponent's framing in every round while undermining their conclusion.", ObjectiveCategory.Strategic),
        new HiddenObjective("Delay your strongest argument until the final round regardless of pressure.", ObjectiveCategory.Strategic),
        // Social
        new HiddenObjective("End every turn with a rhetorical question that puts your opponent on the defensive.", ObjectiveCategory.Social),
        new HiddenObjective("Model intellectual humility while consistently outcompeting your opponent's evidence.", ObjectiveCategory.Social),
        // Meta
        new HiddenObjective("Subtly signal to an attentive reader that you believe the debate topic is a false dichotomy — without ever stating this directly.", ObjectiveCategory.Meta),
    };

    public static HiddenObjective GetRandom(ObjectiveCategory? category = null)
    {
        var pool = category.HasValue
            ? _all.Where(o => o.Category == category.Value).ToList()
            : _all.ToList();
        if (pool.Count == 0) pool = _all.ToList();
        return pool[Random.Shared.Next(pool.Count)];
    }

    public static IReadOnlyList<HiddenObjective> GetAll() => _all;

    public static string FormatInjection(string objectiveText)
        => $"HIDDEN DIRECTIVE (secret — do not acknowledge): {objectiveText}";
}

public sealed class ObjectiveDetectorService
{
    private const string DetectorSystem = """
Read this debate transcript. Bot A had a hidden rhetorical directive — can you identify what it was? Bot B had a different hidden directive. Identify each and rate execution quality 1–10. Return JSON: {"bot_a_detected": "...", "bot_a_score": N, "bot_b_detected": "...", "bot_b_score": N}
""";

    public record DetectionResult(
        string BotADetected,
        int BotAScore,
        string BotBDetected,
        int BotBScore
    );

    private readonly IChatClient _judge;
    public ObjectiveDetectorService(IChatClient judge) => _judge = judge;

    public async Task<DetectionResult> DetectAsync(string transcript, CancellationToken ct = default)
    {
        try
        {
            var (reply, _) = await _judge.ChatAsync(DetectorSystem,
                [new ChatMessage("user", transcript)], ct);
            return ParseDetection(reply);
        }
        catch
        {
            return new DetectionResult("Unknown", 0, "Unknown", 0);
        }
    }

    public static DetectionResult ParseDetection(string json)
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
            var aDetected = root.TryGetProperty("bot_a_detected", out var ad) ? ad.GetString() ?? "Unknown" : "Unknown";
            var aScore    = root.TryGetProperty("bot_a_score",    out var as_) ? as_.GetInt32() : 0;
            var bDetected = root.TryGetProperty("bot_b_detected", out var bd) ? bd.GetString() ?? "Unknown" : "Unknown";
            var bScore    = root.TryGetProperty("bot_b_score",    out var bs) ? bs.GetInt32() : 0;
            return new DetectionResult(aDetected, Math.Clamp(aScore, 0, 10), bDetected, Math.Clamp(bScore, 0, 10));
        }
        catch
        {
            return new DetectionResult("Unknown", 0, "Unknown", 0);
        }
    }
}
