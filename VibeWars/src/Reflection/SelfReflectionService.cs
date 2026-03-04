using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Reflection;

public record SelfReflectionEntry(
    string BotName,
    int Round,
    string StrongestPoint,
    string WeakestResponse,
    string PlannedImprovement
);

public sealed class SelfReflectionService
{
    private readonly IChatClient _client;

    private const string ReflectionSystem = """
You are a self-critical reasoning analyst. Review the debate exchange and the judge's verdict.
""";

    public SelfReflectionService(IChatClient client) => _client = client;

    public async Task<SelfReflectionEntry> ReflectAsync(
        string botName,
        string ownTurn,
        string opponentTurn,
        string judgeVerdict,
        int round,
        CancellationToken ct = default)
    {
        try
        {
            var prompt = $"Your argument: {ownTurn}\nOpponent: {opponentTurn}\nJudge verdict: {judgeVerdict}\n\nReflect honestly: What was your single strongest point this round? What did your opponent say that you failed to adequately counter? What specific improvement will you make in your next round? Return JSON: {{\"strongest_point\": \"...\", \"unaddressed_weakness\": \"...\", \"next_round_improvement\": \"...\"}}";
            var (reply, _) = await _client.ChatAsync(ReflectionSystem,
                [new ChatMessage("user", prompt)], ct);
            return ParseReflection(reply, botName, round);
        }
        catch
        {
            return new SelfReflectionEntry(botName, round, "", "", "");
        }
    }

    public static SelfReflectionEntry ParseReflection(string json, string botName, int round)
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
            var strongest = root.TryGetProperty("strongest_point",       out var sp) ? sp.GetString() ?? "" : "";
            var weakness  = root.TryGetProperty("unaddressed_weakness",  out var uw) ? uw.GetString() ?? "" : "";
            var improve   = root.TryGetProperty("next_round_improvement",out var ni) ? ni.GetString() ?? "" : "";
            return new SelfReflectionEntry(botName, round, strongest, weakness, improve);
        }
        catch
        {
            return new SelfReflectionEntry(botName, round, "", "", "");
        }
    }

    public static string FormatReflectionInjection(SelfReflectionEntry entry)
        => string.IsNullOrWhiteSpace(entry.PlannedImprovement)
            ? ""
            : $"[REFLECTION] From last round: {entry.PlannedImprovement}";

    public static string RenderReflection(SelfReflectionEntry entry)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🔍 [{entry.BotName} Self-Reflection — Round {entry.Round}]");
        if (!string.IsNullOrWhiteSpace(entry.StrongestPoint))
            sb.AppendLine($"   Strongest: {entry.StrongestPoint}");
        if (!string.IsNullOrWhiteSpace(entry.WeakestResponse))
            sb.AppendLine($"   Missed:    {entry.WeakestResponse}");
        if (!string.IsNullOrWhiteSpace(entry.PlannedImprovement))
            sb.AppendLine($"   Next:      {entry.PlannedImprovement}");
        return sb.ToString();
    }

    public static double CalculateReflectionQualityBonus(SelfReflectionEntry entry, bool isHighQuality)
        => isHighQuality ? 0.2 : -0.1;
}
