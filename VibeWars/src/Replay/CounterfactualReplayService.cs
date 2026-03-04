using System.Text;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Replay;

public record ReplayConfig(
    Guid OriginalSessionId,
    string? ReplaceBotAModel,
    string? ReplaceBotAPersona,
    string? ReplaceBotBModel,
    string? ReplaceBotBPersona,
    bool UseOriginalJudge
)
{
    /// <summary>
    /// A replay is valid when at least one replacement parameter is specified,
    /// ensuring it differs from the original session in a meaningful way.
    /// </summary>
    public bool IsValid =>
        ReplaceBotAModel != null || ReplaceBotAPersona != null ||
        ReplaceBotBModel != null || ReplaceBotBPersona != null;
}

public record CounterfactualRoundResult(
    int Round,
    string? OriginalWinner,
    string? ReplayWinner
);

public record CounterfactualComparisonReport(
    Guid OriginalSessionId,
    Guid ReplaySessionId,
    IReadOnlyList<CounterfactualRoundResult> RoundComparisons,
    bool DifferentOverallWinner,
    string? OriginalOverallWinner,
    string? ReplayOverallWinner
);

public sealed class CounterfactualReplayService
{
    public static CounterfactualComparisonReport BuildComparisonReport(
        Guid originalSessionId,
        Guid replaySessionId,
        IReadOnlyList<CounterfactualRoundResult> rounds,
        string? originalWinner,
        string? replayWinner)
    {
        var different = originalWinner != replayWinner;
        return new CounterfactualComparisonReport(
            originalSessionId, replaySessionId, rounds,
            different, originalWinner, replayWinner);
    }

    public static string RenderComparisonReport(CounterfactualComparisonReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\n╔══════════════════════════════════════════════════╗");
        sb.AppendLine("║        Counterfactual Comparison Report          ║");
        sb.AppendLine("╠══════════════════════════════════════════════════╣");
        sb.AppendLine($"║ Original Session: {report.OriginalSessionId.ToString()[..8],-33}║");
        sb.AppendLine($"║ Replay Session:   {report.ReplaySessionId.ToString()[..8],-33}║");
        sb.AppendLine("╠═════════╦══════════════════╦════════════════════╣");
        sb.AppendLine("║ Round   ║ Original Winner  ║ Replay Winner      ║");
        sb.AppendLine("╠═════════╬══════════════════╬════════════════════╣");
        foreach (var r in report.RoundComparisons)
        {
            var delta = r.OriginalWinner == r.ReplayWinner ? "  " : "Δ ";
            sb.AppendLine($"║ {delta}R{r.Round,-5}  ║ {r.OriginalWinner ?? "N/A",-16} ║ {r.ReplayWinner ?? "N/A",-18} ║");
        }
        sb.AppendLine("╠═════════╩══════════════════╩════════════════════╣");
        var verdict = report.DifferentOverallWinner
            ? $"⚡ Different outcome! {report.OriginalOverallWinner} → {report.ReplayOverallWinner}"
            : $"✓ Same overall winner: {report.OriginalOverallWinner}";
        sb.AppendLine($"║ {verdict,-50}║");
        sb.AppendLine("╚══════════════════════════════════════════════════╝");
        return sb.ToString();
    }

    public static IReadOnlyList<ChatMessage> ReconstructDebateHistory(
        IReadOnlyList<MemoryEntry> entries,
        string botAName,
        string botBName)
    {
        return entries
            .Where(e => e.Role is "assistant" or "user")
            .OrderBy(e => e.Round)
            .Select(e => new ChatMessage(
                e.BotName == botAName ? "assistant" : "user",
                e.Content))
            .ToList();
    }
}
