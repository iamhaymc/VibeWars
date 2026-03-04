namespace VibeWars.Config;

public class VibeWarsConfig
{
    // API keys / providers
    public string? OpenRouterApiKey { get; set; }
    public string AwsRegion { get; set; } = "us-east-1";

    // Bot models
    public string? BotAProvider { get; set; }
    public string? BotAModel { get; set; }
    public string? BotBProvider { get; set; }
    public string? BotBModel { get; set; }
    public string? JudgeProvider { get; set; }
    public string? JudgeModel { get; set; }

    // Debate settings
    public int MaxRounds { get; set; } = 3;
    public string DebateFormat { get; set; } = "freeform";

    // Memory settings
    public string MemoryBackend { get; set; } = "sqlite";
    public int MemoryContextTokens { get; set; } = 500;
    public int MemoryTopK { get; set; } = 10;
    public int SummarizeThreshold { get; set; } = 10;
    public string? DbPath { get; set; }
    public string? S3Bucket { get; set; }
    public string? S3Prefix { get; set; }

    // Persona settings
    public string? BotAPersona { get; set; }
    public string? BotBPersona { get; set; }
    public string? BotAPersonaDesc { get; set; }
    public string? BotBPersonaDesc { get; set; }

    // Feature flags
    public bool NoMemory { get; set; } = false;
    public bool NoStream { get; set; } = false;
    public bool NoTui { get; set; } = false;
    public bool PostDebateReport { get; set; } = false;
    public bool FactCheck { get; set; } = false;
    public string? FactCheckModel { get; set; }
    public bool ArgumentGraph { get; set; } = false;
    public bool DryRun { get; set; } = false;

    // Cost guard
    public decimal? MaxCostUsd { get; set; }
    public bool CostHardStop { get; set; } = false;
    public bool CostInteractive { get; set; } = false;

    // Resilience
    public int RetryMax { get; set; } = 4;
    public int RetryBaseDelayMs { get; set; } = 1000;

    // Human-in-the-loop
    public string? HumanRole { get; set; }
    public int ThinkTime { get; set; } = 0;

    // Judge panel
    public string? JudgePanel { get; set; }

    // Stance tracking
    public bool StanceTracking { get; set; } = false;

    // ELO tracking (Feature 1)
    public bool EloTracking { get; set; } = true;

    // Audience simulation (Feature 2)
    public bool AudienceSimulation { get; set; } = false;
    public string AudienceSplit { get; set; } = "50/50";

    // Commentator (Feature 3)
    public bool Commentator { get; set; } = false;
    public string CommentatorModel { get; set; } = "";
    public string CommentatorStyle { get; set; } = "sports";

    // Challenges (Feature 5)
    public bool Challenges { get; set; } = false;

    // Debate complexity (Feature 8)
    public string Complexity { get; set; } = "standard";

    // Webhook integration (Feature 7)
    public string? WebhookUrl { get; set; }
    public string WebhookProvider { get; set; } = "generic";
    public bool WebhookOnComplete { get; set; } = false;
    public bool WebhookOnRound { get; set; } = false;

    // Web dashboard (Feature 9)
    public int? WebPort { get; set; }
    public bool NoBrowser { get; set; } = false;

    // Wave 3 feature flags
    public bool Strategy { get; set; } = false;
    public bool RedTeam { get; set; } = false;
    public string? Proposal { get; set; }
    public bool Reflect { get; set; } = false;
    public bool Arbiter { get; set; } = false;
    public bool Brief { get; set; } = false;
    public bool Analytics { get; set; } = false;
    public string? HiddenObjectiveA { get; set; }
    public string? HiddenObjectiveB { get; set; }
    public bool RevealObjectives { get; set; } = false;

    // Follow-up topic chains (Feature 10)
    public bool Chain { get; set; } = false;
    public int ChainDepth { get; set; } = 3;

    // Embedding / semantic search
    public string VibewarsEmbedBackend { get; set; } = "none";
    public string VibewarsEmbedModel { get; set; } = "openai/text-embedding-3-small";

    // Wave 4: Dramatic Intelligence
    public bool Momentum { get; set; } = false;
    public bool PreDebateHype { get; set; } = false;
    public bool Highlights { get; set; } = false;
    public string StakesMode { get; set; } = "flat"; // flat | escalating | winner-take-all

    // Wave 5: Smarter Bots
    public bool Plan { get; set; } = false;
    public bool Lookahead { get; set; } = false;
    public bool OpponentModel { get; set; } = false;
    public bool Balance { get; set; } = false;
    public string? KnowledgeSource { get; set; } // "wikipedia" or a local path
    public bool FallacyCheck { get; set; } = false;

    // Wave 6: Social & Engagement
    public bool PersonalityEvolution { get; set; } = false;
    public bool DebateCard { get; set; } = false;
    public int BotCount { get; set; } = 2;

    // Profile support (for YAML profiles section)
    public Dictionary<string, VibeWarsConfigOverride> Profiles { get; set; } = new();
}
