using System.Text;

namespace VibeWars.Analytics;

public static class HeatmapRenderer
{
    /// <summary>
    /// Renders an ASCII heatmap using block characters.
    /// ░ = 0–2.5, ▒ = 2.5–5, ▓ = 5–7.5, █ = 7.5–10
    /// </summary>
    public static string RenderHeatmap(IReadOnlyList<ArgumentStrengthScore> scores)
    {
        if (scores.Count == 0) return "(no data)";

        var sb   = new StringBuilder();
        sb.AppendLine("Argument Strength Heatmap  (░=0–2.5  ▒=2.5–5  ▓=5–7.5  █=7.5–10)");
        sb.AppendLine(new string('─', 70));

        var bots = scores.Select(s => s.BotName).Distinct().OrderBy(b => b).ToList();
        foreach (var bot in bots)
        {
            var botScores = scores.Where(s => s.BotName == bot).OrderBy(s => s.Round).ToList();
            var cells     = string.Join("  ", botScores.Select(s => $"R{s.Round}:{BlockChar(s.Composite)}({s.Composite:F1})"));
            var trend     = GetTrendLabel(botScores);
            sb.AppendLine($"{bot,-10}  {cells}  {trend}");
        }

        return sb.ToString();
    }

    public static char BlockChar(double composite) => composite switch
    {
        < 2.5  => '░',
        < 5.0  => '▒',
        < 7.5  => '▓',
        _      => '█',
    };

    private const double Epsilon = 1e-10;

    /// <summary>
    /// Returns trend label based on linear regression slope of composite scores.
    /// ▲ ascending if slope > 0.3, ▼ declining if slope < -0.3, ─ stable otherwise.
    /// </summary>
    public static string GetTrendLabel(IReadOnlyList<ArgumentStrengthScore> scores)
    {
        if (scores.Count < 2) return "─ stable";
        var slope = ComputeSlope(scores.Select(s => s.Composite).ToList());
        return slope > 0.3 ? "▲ ascending" : slope < -0.3 ? "▼ declining" : "─ stable";
    }

    public static double ComputeSlope(IReadOnlyList<double> values)
    {
        var n     = values.Count;
        if (n < 2) return 0;
        // x values are 0-based indices; xMean is the midpoint index
        var xMean = (n - 1) / 2.0;
        var yMean = values.Average();
        var num   = values.Select((y, x) => (x - xMean) * (y - yMean)).Sum();
        var den   = values.Select((_, x) => (x - xMean) * (x - xMean)).Sum();
        return den < Epsilon ? 0 : num / den;
    }
}
