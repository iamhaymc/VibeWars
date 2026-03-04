using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Planning;

public record ArgumentPlan(string StrongestPoint, string EvidenceStrategy, string AnticipatedCounter, string PreemptiveMove);

/// <summary>
/// Implements chain-of-thought planning: the bot generates a private reasoning
/// trace before each argument to improve coherence and strategic depth.
/// </summary>
public sealed class ArgumentPlanner
{
    private readonly IChatClient _plannerClient;

    private const string PlannerSystem = """
You are an internal debate strategist. Plan the next argument without writing the argument itself.
Return JSON only: {"strongest_point": "...", "evidence_strategy": "...", "anticipated_counter": "...", "preemptive_move": "..."}
""";

    public ArgumentPlanner(IChatClient client) => _plannerClient = client;

    public async Task<ArgumentPlan> PlanAsync(
        string topic, string botName, string opponentLastArg, string ownLastArg,
        int round, CancellationToken ct = default)
    {
        try
        {
            var prompt = $"Topic: \"{topic}\"\nYour last argument: {ownLastArg ?? "(opening)"}\nOpponent's last argument: {opponentLastArg ?? "(none yet)"}\nRound: {round}\n\nPlan your next argument.";
            var (reply, _) = await _plannerClient.ChatAsync(PlannerSystem, [new ChatMessage("user", prompt)], ct);
            return ParsePlan(reply);
        }
        catch
        {
            return new ArgumentPlan("", "", "", "");
        }
    }

    public static ArgumentPlan ParsePlan(string json)
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
            var root = doc.RootElement;
            return new ArgumentPlan(
                root.TryGetProperty("strongest_point", out var sp) ? sp.GetString() ?? "" : "",
                root.TryGetProperty("evidence_strategy", out var es) ? es.GetString() ?? "" : "",
                root.TryGetProperty("anticipated_counter", out var ac) ? ac.GetString() ?? "" : "",
                root.TryGetProperty("preemptive_move", out var pm) ? pm.GetString() ?? "" : "");
        }
        catch { return new ArgumentPlan("", "", "", ""); }
    }

    public static string FormatPlanInjection(ArgumentPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.StrongestPoint)) return "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.StrongestPoint)) parts.Add($"Lead with: {plan.StrongestPoint}");
        if (!string.IsNullOrWhiteSpace(plan.EvidenceStrategy)) parts.Add($"Evidence: {plan.EvidenceStrategy}");
        if (!string.IsNullOrWhiteSpace(plan.PreemptiveMove)) parts.Add($"Preempt counter: {plan.PreemptiveMove}");
        return $"[PLAN] {string.Join(". ", parts)}";
    }
}
