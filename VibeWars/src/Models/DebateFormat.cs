namespace VibeWars.Models;

public enum DebateFormat
{
    Freeform,
    Structured,
    Oxford,
    Socratic,
    Collaborative,
    RedTeam
}

public static class DebateFormatHelper
{
    public static string GetTurnInstruction(DebateFormat format, int round, int maxRounds, bool isFinalRound)
    {
        if (format == DebateFormat.RedTeam)
        {
            if (round == 1)
                return " Present your initial position or proposal clearly and comprehensively.";
            return isFinalRound
                ? " Summarize which vulnerabilities were adequately addressed and which remain open."
                : " Identify and probe the most critical weakness, edge case, or failure mode in the proposer's argument.";
        }
        if (format != DebateFormat.Structured) return string.Empty;
        if (round == 1)
            return " Present your **opening claim** (1 sentence), **evidence** (2 specific facts or examples), and **warrant** (1 sentence explaining why the evidence supports your claim).";
        if (isFinalRound)
            return " Synthesize the debate: where do you agree with your opponent? What is the one point on which you still fundamentally disagree, and why?";
        return " State the strongest point from your opponent's last turn (**steelman**), then deliver your **rebuttal** (why it fails or is incomplete), and advance your position with a new **supporting argument**.";
    }

    public static string GetFormatSystemNote(DebateFormat format) => format switch
    {
        DebateFormat.Oxford => " This debate follows Oxford format: Bot A proposes the motion, Bot B opposes it.",
        DebateFormat.Socratic => " This debate follows Socratic format: ask probing questions rather than making direct assertions. Expose hidden assumptions through questioning.",
        DebateFormat.Collaborative => " This debate follows Collaborative format: co-author a shared evolving understanding. Each turn, propose additions or refinements to the shared position.",
        DebateFormat.RedTeam => " This debate follows Red Team format: Bot A proposes a position and defends it. Bot B acts as an adversarial red team, probing for weaknesses, edge cases, and failure modes. The goal is not to win but to stress-test the proposal.",
        _ => string.Empty
    };

    public static DebateFormat Parse(string? value) =>
        Enum.TryParse<DebateFormat>(value, ignoreCase: true, out var f) ? f : DebateFormat.Freeform;
}
