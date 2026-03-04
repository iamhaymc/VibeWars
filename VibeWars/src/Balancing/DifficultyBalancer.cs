namespace VibeWars.Balancing;

public record BalancingAdjustment(string TargetBot, string PromptSupplement, string Reason);

/// <summary>
/// Monitors round win differential and argument quality gap. When one bot
/// dominates, injects subtle prompt adjustments to create a more competitive match.
/// </summary>
public static class DifficultyBalancer
{
    private const int RoundLeadThreshold = 2;
    private const double ScoreGapThreshold = 2.0;

    /// <summary>
    /// Returns a balancing adjustment if the match is too lopsided, or null if balanced.
    /// </summary>
    public static BalancingAdjustment? Evaluate(
        int winsA, int winsB,
        double avgCompositeA, double avgCompositeB)
    {
        var roundLead = Math.Abs(winsA - winsB);
        var scoreGap = Math.Abs(avgCompositeA - avgCompositeB);

        if (roundLead < RoundLeadThreshold && scoreGap < ScoreGapThreshold)
            return null;

        if (winsA > winsB + 1 || (winsA == winsB && avgCompositeA > avgCompositeB + ScoreGapThreshold))
        {
            return new BalancingAdjustment("Bot B",
                "Your opponent has been strong. Identify their single weakest assumption and build your entire response around dismantling it. Be bold and take risks.",
                $"Bot B trailing (A:{winsA} B:{winsB}, score gap: {scoreGap:F1})");
        }
        if (winsB > winsA + 1 || (winsA == winsB && avgCompositeB > avgCompositeA + ScoreGapThreshold))
        {
            return new BalancingAdjustment("Bot A",
                "Your opponent has been strong. Identify their single weakest assumption and build your entire response around dismantling it. Be bold and take risks.",
                $"Bot A trailing (A:{winsA} B:{winsB}, score gap: {scoreGap:F1})");
        }

        return null;
    }

    /// <summary>Returns a restraint prompt for the leading bot to encourage novelty.</summary>
    public static string GetLeaderRestraint()
        => "The debate is going well for you. Rather than repeating previous points, take a genuinely new angle that even challenges your own prior position.";
}
