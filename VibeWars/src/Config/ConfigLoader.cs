using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VibeWars.Config;

public static class ConfigLoader
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Loads config with resolution order: defaults → config file → env vars → CLI flags.
    /// </summary>
    public static VibeWarsConfig Load(string[] args)
    {
        var config = new VibeWarsConfig();

        // 1. Load from config file — deserialise into the nullable override type so that
        //    fields absent from the YAML stay null and are not confused with intentional
        //    overrides that happen to equal the C# default value.
        var configPath = GetConfigPath(args);
        if (File.Exists(configPath))
        {
            try
            {
                var yaml = File.ReadAllText(configPath);
                var fileConfig = YamlDeserializer.Deserialize<VibeWarsConfigOverride>(yaml);
                if (fileConfig != null)
                    MergeInto(config, fileConfig);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Config] Warning: could not parse config file '{configPath}': {ex.Message}");
            }
        }

        // Apply profile if specified
        var profile = GetArgValue(args, "--profile");
        if (profile != null && config.Profiles.TryGetValue(profile, out var profileConfig))
            MergeInto(config, profileConfig);

        // 2. Override with environment variables
        ApplyEnvVars(config);

        // 3. Override with CLI flags
        ApplyCliFlags(config, args);

        return config;
    }

    public static string GetConfigPath(string[] args)
    {
        var fromArg = GetArgValue(args, "--config");
        if (fromArg != null) return fromArg;
        var fromEnv = Environment.GetEnvironmentVariable("VIBEWARS_CONFIG");
        if (fromEnv != null) return fromEnv;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vibewars", "config.yml");
    }

    public static string? GetArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static void ApplyEnvVars(VibeWarsConfig c)
    {
        c.OpenRouterApiKey     = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? c.OpenRouterApiKey;
        c.AwsRegion            = Environment.GetEnvironmentVariable("AWS_REGION") ?? c.AwsRegion;
        c.BotAProvider         = Environment.GetEnvironmentVariable("BOT_A_PROVIDER") ?? c.BotAProvider;
        c.BotAModel            = Environment.GetEnvironmentVariable("BOT_A_MODEL") ?? c.BotAModel;
        c.BotBProvider         = Environment.GetEnvironmentVariable("BOT_B_PROVIDER") ?? c.BotBProvider;
        c.BotBModel            = Environment.GetEnvironmentVariable("BOT_B_MODEL") ?? c.BotBModel;
        c.JudgeProvider        = Environment.GetEnvironmentVariable("JUDGE_PROVIDER") ?? c.JudgeProvider;
        c.JudgeModel           = Environment.GetEnvironmentVariable("JUDGE_MODEL") ?? c.JudgeModel;
        c.MemoryBackend        = Environment.GetEnvironmentVariable("VIBEWARS_MEMORY_BACKEND") ?? c.MemoryBackend;
        c.DbPath               = Environment.GetEnvironmentVariable("VIBEWARS_DB_PATH") ?? c.DbPath;
        c.S3Bucket             = Environment.GetEnvironmentVariable("VIBEWARS_S3_BUCKET") ?? c.S3Bucket;
        c.S3Prefix             = Environment.GetEnvironmentVariable("VIBEWARS_S3_PREFIX") ?? c.S3Prefix;
        c.BotAPersona          = Environment.GetEnvironmentVariable("BOT_A_PERSONA") ?? c.BotAPersona;
        c.BotBPersona          = Environment.GetEnvironmentVariable("BOT_B_PERSONA") ?? c.BotBPersona;
        c.BotAPersonaDesc      = Environment.GetEnvironmentVariable("BOT_A_PERSONA_DESC") ?? c.BotAPersonaDesc;
        c.BotBPersonaDesc      = Environment.GetEnvironmentVariable("BOT_B_PERSONA_DESC") ?? c.BotBPersonaDesc;
        c.DebateFormat         = Environment.GetEnvironmentVariable("VIBEWARS_FORMAT") ?? c.DebateFormat;
        c.VibewarsEmbedBackend = Environment.GetEnvironmentVariable("VIBEWARS_EMBED_BACKEND") ?? c.VibewarsEmbedBackend;
        c.VibewarsEmbedModel   = Environment.GetEnvironmentVariable("VIBEWARS_EMBED_MODEL") ?? c.VibewarsEmbedModel;
        c.FactCheckModel       = Environment.GetEnvironmentVariable("VIBEWARS_FACT_CHECKER_MODEL") ?? c.FactCheckModel;
        c.JudgePanel           = Environment.GetEnvironmentVariable("VIBEWARS_JUDGE_PANEL") ?? c.JudgePanel;

        if (int.TryParse(Environment.GetEnvironmentVariable("MAX_ROUNDS"), out var r)) c.MaxRounds = r;
        if (int.TryParse(Environment.GetEnvironmentVariable("VIBEWARS_MEMORY_CONTEXT_TOKENS"), out var mct)) c.MemoryContextTokens = mct;
        if (int.TryParse(Environment.GetEnvironmentVariable("VIBEWARS_MEMORY_TOP_K"), out var mtk)) c.MemoryTopK = mtk;
        if (int.TryParse(Environment.GetEnvironmentVariable("VIBEWARS_SUMMARIZE_THRESHOLD"), out var st)) c.SummarizeThreshold = st;
        if (int.TryParse(Environment.GetEnvironmentVariable("VIBEWARS_RETRY_MAX"), out var rm)) c.RetryMax = rm;
        if (int.TryParse(Environment.GetEnvironmentVariable("VIBEWARS_RETRY_BASE_DELAY_MS"), out var bd)) c.RetryBaseDelayMs = bd;
        if (decimal.TryParse(Environment.GetEnvironmentVariable("VIBEWARS_MAX_COST_USD"), out var mc)) c.MaxCostUsd = mc;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_FACT_CHECK"), "true", StringComparison.OrdinalIgnoreCase)) c.FactCheck = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_ARGUMENT_GRAPH"), "true", StringComparison.OrdinalIgnoreCase)) c.ArgumentGraph = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_STANCE_TRACKING"), "true", StringComparison.OrdinalIgnoreCase)) c.StanceTracking = true;

        // New feature env vars
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_AUDIENCE"), "true", StringComparison.OrdinalIgnoreCase)) c.AudienceSimulation = true;
        c.AudienceSplit = Environment.GetEnvironmentVariable("VIBEWARS_AUDIENCE_SPLIT") ?? c.AudienceSplit;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_COMMENTATOR"), "true", StringComparison.OrdinalIgnoreCase)) c.Commentator = true;
        c.CommentatorModel = Environment.GetEnvironmentVariable("VIBEWARS_COMMENTATOR_MODEL") ?? c.CommentatorModel;
        c.CommentatorStyle = Environment.GetEnvironmentVariable("VIBEWARS_COMMENTATOR_STYLE") ?? c.CommentatorStyle;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_CHALLENGES"), "true", StringComparison.OrdinalIgnoreCase)) c.Challenges = true;
        c.Complexity = Environment.GetEnvironmentVariable("VIBEWARS_COMPLEXITY") ?? c.Complexity;
        c.WebhookUrl = Environment.GetEnvironmentVariable("VIBEWARS_WEBHOOK_URL") ?? c.WebhookUrl;
        c.WebhookProvider = Environment.GetEnvironmentVariable("VIBEWARS_WEBHOOK_PROVIDER") ?? c.WebhookProvider;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_WEBHOOK_ON_COMPLETE"), "true", StringComparison.OrdinalIgnoreCase)) c.WebhookOnComplete = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_WEBHOOK_ON_ROUND"), "true", StringComparison.OrdinalIgnoreCase)) c.WebhookOnRound = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_NO_ELO"), "true", StringComparison.OrdinalIgnoreCase)) c.EloTracking = false;
        // Also support VIBEWARS_ELO_TRACKING=false as a more consistent naming pattern
        var eloTrackingEnv = Environment.GetEnvironmentVariable("VIBEWARS_ELO_TRACKING");
        if (eloTrackingEnv != null)
            c.EloTracking = !string.Equals(eloTrackingEnv, "false", StringComparison.OrdinalIgnoreCase);

        // Wave 3 env vars
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_STRATEGY"),  "true", StringComparison.OrdinalIgnoreCase)) c.Strategy  = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_REFLECT"),   "true", StringComparison.OrdinalIgnoreCase)) c.Reflect   = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_ARBITER"),   "true", StringComparison.OrdinalIgnoreCase)) c.Arbiter   = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_BRIEF"),     "true", StringComparison.OrdinalIgnoreCase)) c.Brief     = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_ANALYTICS"), "true", StringComparison.OrdinalIgnoreCase)) c.Analytics = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_REVEAL_OBJECTIVES"), "true", StringComparison.OrdinalIgnoreCase)) c.RevealObjectives = true;
        c.HiddenObjectiveA = Environment.GetEnvironmentVariable("VIBEWARS_HIDDEN_OBJ_A") ?? c.HiddenObjectiveA;
        c.HiddenObjectiveB = Environment.GetEnvironmentVariable("VIBEWARS_HIDDEN_OBJ_B") ?? c.HiddenObjectiveB;

        // Wave 4 env vars
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_MOMENTUM"),  "true", StringComparison.OrdinalIgnoreCase)) c.Momentum  = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_HYPE"),      "true", StringComparison.OrdinalIgnoreCase)) c.PreDebateHype = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_HIGHLIGHTS"),"true", StringComparison.OrdinalIgnoreCase)) c.Highlights = true;
        c.StakesMode = Environment.GetEnvironmentVariable("VIBEWARS_STAKES_MODE") ?? c.StakesMode;

        // Wave 5 env vars
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_PLAN"),      "true", StringComparison.OrdinalIgnoreCase)) c.Plan = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_LOOKAHEAD"), "true", StringComparison.OrdinalIgnoreCase)) c.Lookahead = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_OPPONENT_MODEL"), "true", StringComparison.OrdinalIgnoreCase)) c.OpponentModel = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_BALANCE"),   "true", StringComparison.OrdinalIgnoreCase)) c.Balance = true;
        c.KnowledgeSource = Environment.GetEnvironmentVariable("VIBEWARS_KNOWLEDGE") ?? c.KnowledgeSource;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_FALLACY_CHECK"), "true", StringComparison.OrdinalIgnoreCase)) c.FallacyCheck = true;

        // Wave 6 env vars
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_PERSONALITY"), "true", StringComparison.OrdinalIgnoreCase)) c.PersonalityEvolution = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_DEBATE_CARD"), "true", StringComparison.OrdinalIgnoreCase)) c.DebateCard = true;
    }

    private static void ApplyCliFlags(VibeWarsConfig c, string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-memory":    c.NoMemory = true; break;
                case "--no-stream":    c.NoStream = true; break;
                case "--no-tui":       c.NoTui = true; break;
                case "--post-debate-report": c.PostDebateReport = true; break;
                case "--fact-check":   c.FactCheck = true; break;
                case "--argument-graph": c.ArgumentGraph = true; break;
                case "--dry-run": c.DryRun = true; break;
                case "--cost-hard-stop": c.CostHardStop = true; break;
                case "--stance-tracking": c.StanceTracking = true; break;
                case "--cost-interactive": c.CostInteractive = true; break;
                case "--max-cost-usd" when i + 1 < args.Length:
                    if (decimal.TryParse(args[++i], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var maxCost))
                        c.MaxCostUsd = maxCost;
                    break;
                case "--elo":      c.EloTracking = true; break;
                case "--no-elo":   c.EloTracking = false; break;
                case "--audience": c.AudienceSimulation = true; break;
                case "--commentator": c.Commentator = true; break;
                case "--challenges": c.Challenges = true; break;
                case "--audience-split" when i + 1 < args.Length: c.AudienceSplit = args[++i]; break;
                case "--complexity" when i + 1 < args.Length: c.Complexity = args[++i]; break;
                case "--webhook-url"      when i + 1 < args.Length: c.WebhookUrl = args[++i]; break;
                case "--webhook-provider" when i + 1 < args.Length: c.WebhookProvider = args[++i]; break;
                case "--webhook-on-complete": c.WebhookOnComplete = true; break;
                case "--webhook-on-round":    c.WebhookOnRound = true; break;
                case "--web":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var webPort))
                    {
                        c.WebPort = webPort;
                        i++;
                    }
                    else
                    {
                        c.WebPort = 5050;
                    }
                    break;
                case "--no-browser": c.NoBrowser = true; break;
                case "--chain":      c.Chain = true; break;
                case "--chain-depth" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var cd)) c.ChainDepth = cd;
                    break;
                case "--persona-a" when i + 1 < args.Length:   c.BotAPersona = args[++i]; break;
                case "--persona-b" when i + 1 < args.Length:   c.BotBPersona = args[++i]; break;
                case "--bot-a-provider" when i + 1 < args.Length: c.BotAProvider = args[++i]; break;
                case "--bot-b-provider" when i + 1 < args.Length: c.BotBProvider = args[++i]; break;
                case "--judge-provider" when i + 1 < args.Length: c.JudgeProvider = args[++i]; break;
                case "--format"    when i + 1 < args.Length:   c.DebateFormat = args[++i]; break;
                case "--human"     when i + 1 < args.Length:   c.HumanRole = args[++i]; break;
                case "--think-time" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var tt)) c.ThinkTime = tt;
                    break;
                case "--config":   i++; break; // already consumed
                case "--profile":  i++; break; // already consumed
                // Wave 3 flags
                case "--strategy":           c.Strategy  = true; break;
                case "--reflect":            c.Reflect   = true; break;
                case "--arbiter":            c.Arbiter   = true; break;
                case "--brief":              c.Brief     = true; break;
                case "--analytics":          c.Analytics = true; break;
                case "--reveal-objectives":  c.RevealObjectives = true; break;
                case "--hidden-objective-a" when i + 1 < args.Length: c.HiddenObjectiveA = args[++i]; break;
                case "--hidden-objective-b" when i + 1 < args.Length: c.HiddenObjectiveB = args[++i]; break;
                case "--proposal" when i + 1 < args.Length: c.Proposal = args[++i]; break;
                case "--red-team":            c.RedTeam   = true; break;
                case "--commentator-model" when i + 1 < args.Length: c.CommentatorModel = args[++i]; break;
                case "--commentator-style" when i + 1 < args.Length: c.CommentatorStyle = args[++i]; break;
                // Wave 4
                case "--momentum":           c.Momentum = true; break;
                case "--hype":               c.PreDebateHype = true; break;
                case "--highlights":         c.Highlights = true; break;
                case "--stakes" when i + 1 < args.Length: c.StakesMode = args[++i]; break;
                // Wave 5
                case "--plan":               c.Plan = true; break;
                case "--lookahead":          c.Lookahead = true; break;
                case "--opponent-model":     c.OpponentModel = true; break;
                case "--balance":            c.Balance = true; break;
                case "--knowledge" when i + 1 < args.Length: c.KnowledgeSource = args[++i]; break;
                case "--fallacy-check":      c.FallacyCheck = true; break;
                // Wave 6
                case "--personality":        c.PersonalityEvolution = true; break;
                case "--debate-card":        c.DebateCard = true; break;
                case "--bots" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var bc)) c.BotCount = Math.Clamp(bc, 2, 8);
                    break;
            }
        }

        // Auto-detect non-interactive: if output is redirected, default to --no-tui
        if (Console.IsOutputRedirected)
            c.NoTui = true;

        // --red-team implies RedTeam format; --format redteam implies red-team tracking
        if (c.RedTeam && string.Equals(c.DebateFormat, "freeform", StringComparison.OrdinalIgnoreCase))
            c.DebateFormat = "redteam";
        if (string.Equals(c.DebateFormat, "redteam", StringComparison.OrdinalIgnoreCase))
            c.RedTeam = true;

        // --opponent-model requires --strategy for tactic generation
        if (c.OpponentModel)
            c.Strategy = true;
    }

    /// <summary>
    /// Merges explicitly-set values from <paramref name="source"/> into
    /// <paramref name="target"/>.  Because <see cref="VibeWarsConfigOverride"/>
    /// uses nullable types, every non-null field was explicitly present in the
    /// YAML, so we never confuse a "field not written" with "field set to its
    /// default value".
    /// </summary>
    private static void MergeInto(VibeWarsConfig target, VibeWarsConfigOverride source)
    {
        if (source.OpenRouterApiKey != null)      target.OpenRouterApiKey = source.OpenRouterApiKey;
        if (source.AwsRegion        != null)      target.AwsRegion        = source.AwsRegion;
        if (source.BotAProvider     != null)      target.BotAProvider     = source.BotAProvider;
        if (source.BotAModel        != null)      target.BotAModel        = source.BotAModel;
        if (source.BotBProvider     != null)      target.BotBProvider     = source.BotBProvider;
        if (source.BotBModel        != null)      target.BotBModel        = source.BotBModel;
        if (source.JudgeProvider    != null)      target.JudgeProvider    = source.JudgeProvider;
        if (source.JudgeModel       != null)      target.JudgeModel       = source.JudgeModel;
        if (source.MaxRounds        != null)      target.MaxRounds        = source.MaxRounds.Value;
        if (source.DebateFormat     != null)      target.DebateFormat     = source.DebateFormat;
        if (source.MemoryBackend    != null)      target.MemoryBackend    = source.MemoryBackend;
        if (source.MemoryContextTokens != null)   target.MemoryContextTokens = source.MemoryContextTokens.Value;
        if (source.MemoryTopK       != null)      target.MemoryTopK       = source.MemoryTopK.Value;
        if (source.SummarizeThreshold != null)    target.SummarizeThreshold = source.SummarizeThreshold.Value;
        if (source.DbPath           != null)      target.DbPath           = source.DbPath;
        if (source.S3Bucket         != null)      target.S3Bucket         = source.S3Bucket;
        if (source.S3Prefix         != null)      target.S3Prefix         = source.S3Prefix;
        if (source.BotAPersona      != null)      target.BotAPersona      = source.BotAPersona;
        if (source.BotBPersona      != null)      target.BotBPersona      = source.BotBPersona;
        if (source.BotAPersonaDesc  != null)      target.BotAPersonaDesc  = source.BotAPersonaDesc;
        if (source.BotBPersonaDesc  != null)      target.BotBPersonaDesc  = source.BotBPersonaDesc;
        if (source.MaxCostUsd       != null)      target.MaxCostUsd       = source.MaxCostUsd;
        if (source.RetryMax         != null)      target.RetryMax         = source.RetryMax.Value;
        if (source.RetryBaseDelayMs != null)      target.RetryBaseDelayMs = source.RetryBaseDelayMs.Value;
        if (source.JudgePanel       != null)      target.JudgePanel       = source.JudgePanel;
        if (source.FactCheckModel   != null)      target.FactCheckModel   = source.FactCheckModel;
        if (source.HumanRole        != null)      target.HumanRole        = source.HumanRole;
        if (source.ThinkTime        != null)      target.ThinkTime        = source.ThinkTime.Value;
        if (source.VibewarsEmbedBackend != null)  target.VibewarsEmbedBackend = source.VibewarsEmbedBackend;
        if (source.VibewarsEmbedModel   != null)  target.VibewarsEmbedModel   = source.VibewarsEmbedModel;
        if (source.NoMemory         != null)      target.NoMemory         = source.NoMemory.Value;
        if (source.NoStream         != null)      target.NoStream         = source.NoStream.Value;
        if (source.NoTui            != null)      target.NoTui            = source.NoTui.Value;
        if (source.PostDebateReport != null)      target.PostDebateReport = source.PostDebateReport.Value;
        if (source.FactCheck        != null)      target.FactCheck        = source.FactCheck.Value;
        if (source.ArgumentGraph    != null)      target.ArgumentGraph    = source.ArgumentGraph.Value;
        if (source.DryRun           != null)      target.DryRun           = source.DryRun.Value;
        if (source.CostHardStop     != null)      target.CostHardStop     = source.CostHardStop.Value;
        if (source.CostInteractive  != null)      target.CostInteractive  = source.CostInteractive.Value;
        if (source.AudienceSimulation != null)    target.AudienceSimulation = source.AudienceSimulation.Value;
        if (source.AudienceSplit    != null)      target.AudienceSplit    = source.AudienceSplit;
        if (source.Commentator      != null)      target.Commentator      = source.Commentator.Value;
        if (source.CommentatorModel != null)      target.CommentatorModel = source.CommentatorModel;
        if (source.CommentatorStyle != null)      target.CommentatorStyle = source.CommentatorStyle;
        if (source.Challenges       != null)      target.Challenges       = source.Challenges.Value;
        if (source.Complexity       != null)      target.Complexity       = source.Complexity;
        if (source.WebhookUrl       != null)      target.WebhookUrl       = source.WebhookUrl;
        if (source.WebhookProvider  != null)      target.WebhookProvider  = source.WebhookProvider;
        if (source.WebhookOnComplete != null)     target.WebhookOnComplete = source.WebhookOnComplete.Value;
        if (source.WebhookOnRound   != null)      target.WebhookOnRound   = source.WebhookOnRound.Value;
        if (source.EloTracking      != null)      target.EloTracking      = source.EloTracking.Value;
        if (source.Chain            != null)      target.Chain            = source.Chain.Value;
        if (source.ChainDepth       != null)      target.ChainDepth       = source.ChainDepth.Value;
        if (source.WebPort          != null)      target.WebPort          = source.WebPort;
        if (source.NoBrowser        != null)      target.NoBrowser        = source.NoBrowser.Value;
        if (source.Strategy         != null)      target.Strategy         = source.Strategy.Value;
        if (source.RedTeam          != null)      target.RedTeam          = source.RedTeam.Value;
        if (source.Proposal         != null)      target.Proposal         = source.Proposal;
        if (source.Reflect          != null)      target.Reflect          = source.Reflect.Value;
        if (source.Arbiter          != null)      target.Arbiter          = source.Arbiter.Value;
        if (source.Brief            != null)      target.Brief            = source.Brief.Value;
        if (source.Analytics        != null)      target.Analytics        = source.Analytics.Value;
        if (source.HiddenObjectiveA != null)      target.HiddenObjectiveA = source.HiddenObjectiveA;
        if (source.HiddenObjectiveB != null)      target.HiddenObjectiveB = source.HiddenObjectiveB;
        if (source.RevealObjectives != null)      target.RevealObjectives = source.RevealObjectives.Value;
        if (source.StanceTracking   != null)      target.StanceTracking   = source.StanceTracking.Value;
        // Wave 4-6
        if (source.Momentum         != null)      target.Momentum         = source.Momentum.Value;
        if (source.PreDebateHype    != null)      target.PreDebateHype    = source.PreDebateHype.Value;
        if (source.Highlights       != null)      target.Highlights       = source.Highlights.Value;
        if (source.StakesMode       != null)      target.StakesMode       = source.StakesMode;
        if (source.Plan             != null)      target.Plan             = source.Plan.Value;
        if (source.Lookahead        != null)      target.Lookahead        = source.Lookahead.Value;
        if (source.OpponentModel    != null)      target.OpponentModel    = source.OpponentModel.Value;
        if (source.Balance          != null)      target.Balance          = source.Balance.Value;
        if (source.KnowledgeSource  != null)      target.KnowledgeSource  = source.KnowledgeSource;
        if (source.FallacyCheck     != null)      target.FallacyCheck     = source.FallacyCheck.Value;
        if (source.PersonalityEvolution != null)  target.PersonalityEvolution = source.PersonalityEvolution.Value;
        if (source.DebateCard       != null)      target.DebateCard       = source.DebateCard.Value;
        if (source.BotCount         != null)      target.BotCount         = source.BotCount.Value;
    }
    public static string GenerateStarterConfig() => """
# VibeWars configuration file
# Resolution order: defaults → this file → environment variables → CLI flags

# API Configuration
# openRouterApiKey: ""        # or set OPENROUTER_API_KEY env var
# awsRegion: us-east-1

# Bot Models
# botAProvider: openrouter   # openrouter | bedrock (auto-detected from OPENROUTER_API_KEY if not set)
# botAModel: openai/gpt-4o-mini
# botBProvider: bedrock       # openrouter | bedrock (defaults to bedrock if not set)
# botBModel: amazon.nova-lite-v1:0
# judgeProvider: openrouter   # openrouter | bedrock (auto-detected from OPENROUTER_API_KEY if not set)
# judgeModel: openai/gpt-4o-mini

# Debate Settings
# maxRounds: 3
# debateFormat: freeform       # freeform | structured | oxford | socratic | collaborative

# Bot Personas
# botAPersona: Pragmatist      # Pragmatist | Idealist | Devil's Advocate | Domain Expert | Empiricist | Ethicist | Contrarian | Synthesizer
# botBPersona: Idealist
# botAPersonaDesc: ""          # Custom persona description when botAPersona=custom

# Memory Settings
# memoryBackend: sqlite        # sqlite | s3 | hybrid
# memoryContextTokens: 500
# memoryTopK: 10
# summarizeThreshold: 10

# Feature Flags
# noMemory: false
# noStream: false
# noTui: false
# factCheck: false
# argumentGraph: false

# Cost Guard
# maxCostUsd: ~                # e.g. 0.50 to limit spend to $0.50
# costHardStop: false
# costInteractive: false

# Resilience
# retryMax: 4
# retryBaseDelayMs: 1000

# Profiles (named config overrides)
# profiles:
#   cheap:
#     botAModel: openai/gpt-4o-mini
#     botBModel: amazon.nova-lite-v1:0
#     maxRounds: 2
#   research:
#     botAModel: openai/gpt-4o
#     botBModel: anthropic/claude-3-5-sonnet
#     maxRounds: 5
""";
}
