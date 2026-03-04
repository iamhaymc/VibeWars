namespace VibeWars.Complexity;

public enum DebateComplexity
{
    Casual,
    Standard,
    Academic,
    Technical,
    PolicyBrief
}

public static class DebateComplexityService
{
    public static DebateComplexity Parse(string? input) =>
        (input ?? string.Empty).ToLowerInvariant() switch
        {
            "casual"                                     => DebateComplexity.Casual,
            "academic"                                   => DebateComplexity.Academic,
            "technical"                                  => DebateComplexity.Technical,
            "policybrief" or "policy-brief" or "policy_brief" => DebateComplexity.PolicyBrief,
            _                                            => DebateComplexity.Standard
        };

    public static string GetBotPromptSuffix(DebateComplexity complexity) => complexity switch
    {
        DebateComplexity.Casual =>
            "Keep it conversational, avoid jargon, use everyday examples. 2-3 sentences max.",
        DebateComplexity.Standard =>
            string.Empty,
        DebateComplexity.Academic =>
            "Use formal academic register. Include at least one citation-style reference per turn (real or illustrative). Structure arguments as Claim–Evidence–Warrant. 4–6 sentences.",
        DebateComplexity.Technical =>
            "Assume deep domain expertise in your audience. Use precise technical terminology, quantify claims where possible, and address implementation details.",
        DebateComplexity.PolicyBrief =>
            "Frame each argument as a policy recommendation. Address stakeholders, trade-offs, implementation feasibility, and measurable success metrics. 5–8 sentences.",
        _ => string.Empty
    };

    public static string GetJudgePromptSuffix(DebateComplexity complexity) => complexity switch
    {
        DebateComplexity.Academic =>
            "Evaluate citation quality and academic rigor in arguments.",
        DebateComplexity.PolicyBrief =>
            "Evaluate stakeholder consideration and policy feasibility.",
        _ => string.Empty
    };
}
