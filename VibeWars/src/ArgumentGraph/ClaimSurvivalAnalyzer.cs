using System.Text;
using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.ArgumentGraph;

public enum ClaimLifecycle { Active, Challenged, Defended, Conceded, Refuted, Abandoned }

public record ClaimLifecycleEvent(
    Guid ClaimId,
    int Round,
    ClaimLifecycle NewStatus,
    string Trigger
);

public record ClaimSurvivalStats(
    string BotName,
    int TotalClaims,
    int SurvivedClaims,
    int RefutedClaims,
    int ConcededClaims,
    double SurvivalRate,
    double KillRate
);

public sealed class ClaimSurvivalAnalyzer
{
    private const string AnalysisSystem = """
You are a debate analyst. For the given challenge relationship between two claims, determine the outcome.
Return JSON: {"claim_id": "...", "outcome": "Refuted|Defended|Conceded"}
""";

    public async Task<IReadOnlyList<ClaimLifecycleEvent>> AnalyzeAsync(
        IReadOnlyList<ArgumentNode> nodes,
        IReadOnlyList<ArgumentEdge> edges,
        IChatClient judge,
        CancellationToken ct = default)
    {
        var events = new List<ClaimLifecycleEvent>();
        var challengeEdges = edges.Where(e => e.Relation == RelationType.Challenges).ToList();
        var nodeMap = nodes.ToDictionary(n => n.Id);

        foreach (var edge in challengeEdges)
        {
            if (!nodeMap.TryGetValue(edge.FromId, out var challenger)) continue;
            if (!nodeMap.TryGetValue(edge.ToId,   out var challenged)) continue;

            try
            {
                var prompt = $"Challenged claim: \"{challenged.ClaimText}\"\nChallenging claim: \"{challenger.ClaimText}\"";
                var (reply, _) = await judge.ChatAsync(AnalysisSystem,
                    [new ChatMessage("user", prompt)], ct);
                var evt = ParseOutcome(reply, challenged.Id, challenged.Round, challenger.ClaimText);
                if (evt != null) events.Add(evt);
            }
            catch
            {
                events.Add(new ClaimLifecycleEvent(challenged.Id, challenged.Round, ClaimLifecycle.Challenged, challenger.ClaimText));
            }
        }
        return events;
    }

    public static ClaimLifecycleEvent? ParseOutcome(string json, Guid claimId, int round, string trigger)
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
            var outcome = doc.RootElement.TryGetProperty("outcome", out var o) ? o.GetString() : null;
            var lifecycle = outcome?.ToLowerInvariant() switch
            {
                "refuted"  => ClaimLifecycle.Refuted,
                "conceded" => ClaimLifecycle.Conceded,
                "defended" => ClaimLifecycle.Defended,
                _          => ClaimLifecycle.Challenged,
            };
            return new ClaimLifecycleEvent(claimId, round, lifecycle, trigger);
        }
        catch { return null; }
    }

    public static IReadOnlyList<ClaimSurvivalStats> ComputeSurvivalStats(
        IReadOnlyList<ArgumentNode> nodes,
        IReadOnlyList<ClaimLifecycleEvent> events)
    {
        var eventMap = events.GroupBy(e => e.ClaimId)
                             .ToDictionary(g => g.Key, g => g.Last().NewStatus);

        var results = new List<ClaimSurvivalStats>();
        foreach (var group in nodes.GroupBy(n => n.BotName))
        {
            var botName     = group.Key;
            var total       = group.Count();

            var refuted    = group.Count(n => eventMap.TryGetValue(n.Id, out var s) && s == ClaimLifecycle.Refuted);
            var conceded   = group.Count(n => eventMap.TryGetValue(n.Id, out var s) && s == ClaimLifecycle.Conceded);
            var challenged = group.Count(n => eventMap.ContainsKey(n.Id));
            var survived   = challenged == 0 ? total :
                             group.Count(n => !eventMap.ContainsKey(n.Id) ||
                                             eventMap[n.Id] == ClaimLifecycle.Defended ||
                                             eventMap[n.Id] == ClaimLifecycle.Active);

            // Kill rate: how many opponent claims did this bot refute?
            var opponentNodes = nodes.Where(n => n.BotName != botName).ToList();
            var refutedOpponentClaims = opponentNodes.Count(n => eventMap.TryGetValue(n.Id, out var s) && s == ClaimLifecycle.Refuted);
            var killRate      = opponentNodes.Count == 0 ? 0.0 : (double)refutedOpponentClaims / opponentNodes.Count;
            var survivalRate  = total == 0 ? 0.0 : (double)survived / total;

            results.Add(new ClaimSurvivalStats(botName, total, survived, refuted, conceded, survivalRate, killRate));
        }
        return results;
    }

    public static string RenderAutopsy(
        IReadOnlyList<ArgumentNode> nodes,
        IReadOnlyList<ClaimLifecycleEvent> events)
    {
        var eventMap = events.GroupBy(e => e.ClaimId)
                             .ToDictionary(g => g.Key, g => g.Last());
        var sb = new StringBuilder();

        sb.AppendLine("\n🪦 Argument Graveyard");
        sb.AppendLine("──────────────────────────────────────");
        var dead = nodes.Where(n => eventMap.TryGetValue(n.Id, out var e) &&
                                    (e.NewStatus == ClaimLifecycle.Refuted || e.NewStatus == ClaimLifecycle.Conceded))
                        .ToList();
        if (dead.Count == 0)
            sb.AppendLine("  (no claims were refuted or conceded)");
        else
            foreach (var n in dead)
            {
                var e     = eventMap[n.Id];
                var label = e.NewStatus == ClaimLifecycle.Refuted ? "Refuted" : "Conceded";
                sb.AppendLine($"  [{label}] R{n.Round} {n.BotName}: \"{(n.ClaimText.Length > 80 ? n.ClaimText[..80] + "…" : n.ClaimText)}\"");
                if (!string.IsNullOrWhiteSpace(e.Trigger))
                    sb.AppendLine($"           ← \"{(e.Trigger.Length > 60 ? e.Trigger[..60] + "…" : e.Trigger)}\"");
            }

        sb.AppendLine("\n🏆 Survivors");
        sb.AppendLine("──────────────────────────────────────");
        var survivors = nodes.Where(n => !eventMap.ContainsKey(n.Id) ||
                                          eventMap[n.Id].NewStatus == ClaimLifecycle.Defended ||
                                          eventMap[n.Id].NewStatus == ClaimLifecycle.Active)
                             .ToList();
        if (survivors.Count == 0)
            sb.AppendLine("  (no claims survived unchallenged)");
        else
            foreach (var n in survivors)
                sb.AppendLine($"  [Survived] R{n.Round} {n.BotName}: \"{(n.ClaimText.Length > 80 ? n.ClaimText[..80] + "…" : n.ClaimText)}\"");

        return sb.ToString();
    }
}
