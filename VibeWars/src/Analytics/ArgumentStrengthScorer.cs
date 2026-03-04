using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Analytics;

public record ArgumentStrengthScore(
    int Round,
    string BotName,
    double LogicalRigor,
    double Novelty,
    double PersuasiveImpact,
    double Composite
)
{
    public static ArgumentStrengthScore Default(int round, string botName)
        => new(round, botName, 5.0, 5.0, 5.0, 5.0);

    public static double ComputeComposite(double rigor, double novelty, double persuasion)
        => 0.4 * rigor + 0.3 * novelty + 0.3 * persuasion;
}

public sealed class ArgumentStrengthScorer
{
    private readonly IChatClient _judge;

    private const string ScorerSystem = """
Score this debate argument on three dimensions (0–10 each):
- logical_rigor: internal consistency, absence of fallacies
- novelty: new information introduced vs prior arguments
- persuasive_impact: how likely to shift a neutral observer
Return JSON: {"logical_rigor": N, "novelty": N, "persuasive_impact": N}
""";

    public ArgumentStrengthScorer(IChatClient judge) => _judge = judge;

    public async Task<ArgumentStrengthScore> ScoreAsync(
        string argument,
        IReadOnlyList<string> priorArguments,
        int round,
        string botName,
        CancellationToken ct = default)
    {
        try
        {
            var priorSummary = priorArguments.Count > 0
                ? $"\n\nPrior arguments for context:\n{string.Join("\n---\n", priorArguments.TakeLast(3))}"
                : "";
            var prompt = $"Argument to score:\n{argument}{priorSummary}";
            var (reply, _) = await _judge.ChatAsync(ScorerSystem,
                [new ChatMessage("user", prompt)], ct);
            return ParseScore(reply, round, botName);
        }
        catch
        {
            return ArgumentStrengthScore.Default(round, botName);
        }
    }

    public static ArgumentStrengthScore ParseScore(string json, int round, string botName)
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
            var rigor    = root.TryGetProperty("logical_rigor",     out var r) ? Math.Clamp(r.GetDouble(), 0, 10) : 5.0;
            var novelty  = root.TryGetProperty("novelty",           out var n) ? Math.Clamp(n.GetDouble(), 0, 10) : 5.0;
            var persuade = root.TryGetProperty("persuasive_impact", out var p) ? Math.Clamp(p.GetDouble(), 0, 10) : 5.0;
            var composite = ArgumentStrengthScore.ComputeComposite(rigor, novelty, persuade);
            return new ArgumentStrengthScore(round, botName, rigor, novelty, persuade, composite);
        }
        catch
        {
            return ArgumentStrengthScore.Default(round, botName);
        }
    }
}
