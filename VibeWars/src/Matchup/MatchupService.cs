using VibeWars.Elo;

namespace VibeWars.Matchup;

public record MatchupCard(
    string ContestantA, double EloA, int WinsA, int LossesA,
    string ContestantB, double EloB, int WinsB, int LossesB,
    int HeadToHeadA, int HeadToHeadB,
    double PredictionA, double PredictionB
);

public static class MatchupService
{
    /// <summary>
    /// Computes predicted win probability using the ELO expected-score formula.
    /// E_A = 1 / (1 + 10^((R_B - R_A) / 400))
    /// </summary>
    public static double PredictWinProbability(double eloA, double eloB)
        => 1.0 / (1.0 + Math.Pow(10.0, (eloB - eloA) / 400.0));

    public static MatchupCard BuildCard(EloRecord? recordA, EloRecord? recordB, int h2hA = 0, int h2hB = 0)
    {
        var eloA = recordA?.Rating ?? 1200;
        var eloB = recordB?.Rating ?? 1200;
        var predA = PredictWinProbability(eloA, eloB);
        return new MatchupCard(
            recordA?.ContestantId ?? "Bot A", eloA, recordA?.Wins ?? 0, recordA?.Losses ?? 0,
            recordB?.ContestantId ?? "Bot B", eloB, recordB?.Wins ?? 0, recordB?.Losses ?? 0,
            h2hA, h2hB, predA, 1.0 - predA);
    }

    public static string RenderCard(MatchupCard card)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("╔═══════════════════════════════════════════════════════╗");
        sb.AppendLine("║              PRE-DEBATE ANALYSIS                     ║");
        sb.AppendLine("╠═══════════════════════════╦═══════════════════════════╣");

        var nameA = card.ContestantA.Length > 23 ? card.ContestantA[..23] : card.ContestantA;
        var nameB = card.ContestantB.Length > 23 ? card.ContestantB[..23] : card.ContestantB;
        var statsA = $"W:{card.WinsA} L:{card.LossesA}";
        var statsB = $"W:{card.WinsB} L:{card.LossesB}";
        sb.AppendLine($"║ {nameA,-25} ║ {nameB,-25} ║");
        sb.AppendLine($"║ ELO: {card.EloA,-20:F0} ║ ELO: {card.EloB,-20:F0} ║");
        sb.AppendLine($"║ {statsA,-25} ║ {statsB,-25} ║");
        if (card.HeadToHeadA + card.HeadToHeadB > 0)
            sb.AppendLine($"║ H2H: {card.HeadToHeadA,-20} ║ H2H: {card.HeadToHeadB,-20} ║");
        sb.AppendLine($"║ Prediction: {card.PredictionA,-13:P0} ║ Prediction: {card.PredictionB,-13:P0} ║");
        sb.AppendLine("╚═══════════════════════════╩═══════════════════════════╝");
        return sb.ToString();
    }
}
