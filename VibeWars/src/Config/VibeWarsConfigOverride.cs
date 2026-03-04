namespace VibeWars.Config;

/// <summary>
/// Nullable mirror of <see cref="VibeWarsConfig"/> used exclusively when
/// deserializing YAML files and profiles.  Every field is nullable so that
/// fields absent from the YAML remain <c>null</c> and are not mistakenly
/// treated as intentional overrides that match the default value.
/// </summary>
public class VibeWarsConfigOverride
{
    public string? OpenRouterApiKey { get; set; }
    public string? AwsRegion { get; set; }

    public string? BotAProvider { get; set; }
    public string? BotAModel { get; set; }
    public string? BotBProvider { get; set; }
    public string? BotBModel { get; set; }
    public string? JudgeProvider { get; set; }
    public string? JudgeModel { get; set; }

    public int?    MaxRounds { get; set; }
    public string? DebateFormat { get; set; }

    public string? MemoryBackend { get; set; }
    public int?    MemoryContextTokens { get; set; }
    public int?    MemoryTopK { get; set; }
    public int?    SummarizeThreshold { get; set; }
    public string? DbPath { get; set; }
    public string? S3Bucket { get; set; }
    public string? S3Prefix { get; set; }

    public string? BotAPersona { get; set; }
    public string? BotBPersona { get; set; }
    public string? BotAPersonaDesc { get; set; }
    public string? BotBPersonaDesc { get; set; }

    public bool?   NoMemory { get; set; }
    public bool?   NoStream { get; set; }
    public bool?   NoTui { get; set; }
    public bool?   PostDebateReport { get; set; }
    public bool?   FactCheck { get; set; }
    public string? FactCheckModel { get; set; }
    public bool?   ArgumentGraph { get; set; }
    public bool?   DryRun { get; set; }

    public decimal? MaxCostUsd { get; set; }
    public bool?    CostHardStop { get; set; }
    public bool?    CostInteractive { get; set; }

    public int?    RetryMax { get; set; }
    public int?    RetryBaseDelayMs { get; set; }

    public string? HumanRole { get; set; }
    public int?    ThinkTime { get; set; }

    public string? JudgePanel { get; set; }

    public bool?   StanceTracking { get; set; }
    public bool?   EloTracking { get; set; }

    public bool?   AudienceSimulation { get; set; }
    public string? AudienceSplit { get; set; }

    public bool?   Commentator { get; set; }
    public string? CommentatorModel { get; set; }
    public string? CommentatorStyle { get; set; }

    public bool?   Challenges { get; set; }
    public string? Complexity { get; set; }

    public string? WebhookUrl { get; set; }
    public string? WebhookProvider { get; set; }
    public bool?   WebhookOnComplete { get; set; }
    public bool?   WebhookOnRound { get; set; }

    public int?  WebPort { get; set; }
    public bool? NoBrowser { get; set; }

    public bool?   Strategy { get; set; }
    public bool?   RedTeam { get; set; }
    public string? Proposal { get; set; }
    public bool?   Reflect { get; set; }
    public bool?   Arbiter { get; set; }
    public bool?   Brief { get; set; }
    public bool?   Analytics { get; set; }
    public string? HiddenObjectiveA { get; set; }
    public string? HiddenObjectiveB { get; set; }
    public bool?   RevealObjectives { get; set; }

    public bool?   Chain { get; set; }
    public int?    ChainDepth { get; set; }

    public string? VibewarsEmbedBackend { get; set; }
    public string? VibewarsEmbedModel { get; set; }

    // Wave 4: Dramatic Intelligence
    public bool?   Momentum { get; set; }
    public bool?   PreDebateHype { get; set; }
    public bool?   Highlights { get; set; }
    public string? StakesMode { get; set; }

    // Wave 5: Smarter Bots
    public bool?   Plan { get; set; }
    public bool?   Lookahead { get; set; }
    public bool?   OpponentModel { get; set; }
    public bool?   Balance { get; set; }
    public string? KnowledgeSource { get; set; }
    public bool?   FallacyCheck { get; set; }

    // Wave 6: Social & Engagement
    public bool?   PersonalityEvolution { get; set; }
    public bool?   DebateCard { get; set; }
    public int?    BotCount { get; set; }

    public Dictionary<string, VibeWarsConfigOverride> Profiles { get; set; } = new();
}
