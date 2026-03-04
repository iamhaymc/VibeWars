using Amazon.S3;
using Amazon.S3.Model;
using Moq;
using VibeWars.Arbiter;
using VibeWars.Clients;
using VibeWars.Commentator;
using VibeWars.Complexity;
using VibeWars.Config;
using VibeWars.JudgePanel;
using VibeWars.Memory;
using VibeWars.Models;
using VibeWars.RedTeam;
using VibeWars.Replay;
using VibeWars.Reports;
using VibeWars.StanceTracker;
using VibeWars.Strategy;

namespace VibeWars.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Regression tests for bugs fixed across rounds 1-14.
// Each test documents the original bug number and what it validates.
// ═══════════════════════════════════════════════════════════════════════════════

// ── DialecticalArbiter: filter on BotName, not Role (Round 1, Bug #13) ───────

public sealed class ArbiterBotNameFilterTests
{
    [Fact]
    public void ParseSynthesis_ValidJson_Parses()
    {
        var json = """{"thesis": "T", "antithesis": "A", "synthesis": "S", "open_questions": ["Q1"]}""";
        var result = DialecticalArbiter.ParseSynthesis(json);
        Assert.Equal("T", result.CoreThesis);
        Assert.Equal("A", result.CoreAntithesis);
        Assert.Equal("S", result.Synthesis);
        Assert.Single(result.OpenQuestions);
    }

    [Fact]
    public void ParseSynthesis_MarkdownWrapped_Parses()
    {
        var json = "```json\n{\"thesis\": \"T\", \"antithesis\": \"A\", \"synthesis\": \"S\", \"open_questions\": []}\n```";
        var result = DialecticalArbiter.ParseSynthesis(json);
        Assert.Equal("T", result.CoreThesis);
    }

    [Fact]
    public void ParseSynthesis_Malformed_ReturnsFallback()
    {
        var result = DialecticalArbiter.ParseSynthesis("garbage", "fallback");
        Assert.Equal("fallback", result.Synthesis);
    }
}

// ── JudgePanelService: winner detection uses StartsWith, not Contains ────────

public sealed class JudgePanelWinnerDetectionTests
{
    [Fact]
    public void Aggregate_ExactBotAWinner_Detected()
    {
        var verdicts = new List<JudgeVerdict>
        {
            new("Bot A", "Reason 1", ""),
            new("Bot A", "Reason 2", ""),
            new("Bot B", "Reason 3", ""),
        };
        var result = JudgePanelService.Aggregate(verdicts);
        Assert.Equal("Bot A", result.Winner);
    }

    [Fact]
    public void Aggregate_ShortformA_Detected()
    {
        var verdicts = new List<JudgeVerdict>
        {
            new("A", "Reason", ""),
            new("A", "Reason", ""),
            new("B", "Reason", ""),
        };
        var result = JudgePanelService.Aggregate(verdicts);
        Assert.Equal("Bot A", result.Winner);
    }

    [Fact]
    public void Aggregate_AmbiguousString_NotDoubleCounted()
    {
        // "Bot A and Bot B tied" should NOT count as both A and B wins
        // With StartsWith, this counts as Bot A only (starts with "Bot A")
        var verdicts = new List<JudgeVerdict>
        {
            new("Bot A and Bot B tied", "Reason", ""),
            new("Bot B", "Reason", ""),
            new("Bot B", "Reason", ""),
        };
        var result = JudgePanelService.Aggregate(verdicts);
        Assert.Equal("Bot B", result.Winner);
    }
}

// ── VulnerabilityTracker: Guid-based identity for record lookup ──────────────

public sealed class VulnerabilityTrackerGuidTests
{
    [Fact]
    public void ParseVulnerabilities_EachRecordHasUniqueId()
    {
        var json = """[{"category": "LogicGap", "description": "Issue A"}, {"category": "LogicGap", "description": "Issue A"}]""";
        var results = VulnerabilityTracker.ParseVulnerabilities(json, 1);
        Assert.Equal(2, results.Count);
        // Even with identical content, each record should have a unique Id
        Assert.NotEqual(results[0].Id, results[1].Id);
    }

    [Fact]
    public void ApplyStatusUpdates_IdenticalRecords_CorrectOneUpdated()
    {
        // Create two records with identical content but different Ids
        var json = """[{"category": "LogicGap", "description": "Same issue"}]""";
        var r1 = VulnerabilityTracker.ParseVulnerabilities(json, 1);
        var r2 = VulnerabilityTracker.ParseVulnerabilities(json, 1);

        var tracker = new VulnerabilityTracker();
        var field = typeof(VulnerabilityTracker).GetField("_records",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var list = (List<VulnerabilityRecord>)field.GetValue(tracker)!;
        list.AddRange(r1);
        list.AddRange(r2);

        // Update only the first record (index 0 of the open list = r1[0])
        var updateJson = """[{"index": 0, "status": "Patched"}]""";
        tracker.ApplyStatusUpdates(updateJson, r1);

        Assert.Equal(VulnerabilityStatus.Patched, list[0].Status);
        Assert.Equal(VulnerabilityStatus.Open, list[1].Status);
    }
}

// ── StanceMeter: RenderBar produces consistent 11-character bars ─────────────

public sealed class StanceMeterRenderBarTests
{
    [Theory]
    [InlineData(-5)]
    [InlineData(-3)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void RenderBar_AllStances_Produce11Chars(int stance)
    {
        // Use reflection to access the private static RenderBar method
        var method = typeof(StanceMeterService).GetMethod("RenderBar",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var bar = (string)method.Invoke(null, [stance])!;
        Assert.Equal(11, bar.Length);
    }

    [Fact]
    public void RenderBar_ZeroStance_MarkerInCenter()
    {
        var method = typeof(StanceMeterService).GetMethod("RenderBar",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var bar = (string)method.Invoke(null, [0])!;
        // Position 5 (0-indexed) should be the marker
        Assert.Equal('\u2588', bar[5]); // █
        Assert.Equal('\u2591', bar[0]); // ░
        Assert.Equal('\u2591', bar[10]); // ░
    }
}

// ── CounterfactualReplayService: filters on actual Role values ───────────────

public sealed class ReplayRoleFilterTests
{
    [Fact]
    public void ReconstructDebateHistory_FiltersOnAssistantRole()
    {
        var entries = new List<MemoryEntry>
        {
            new(Guid.NewGuid(), "Bot A", "topic", 1, "assistant", "A argues", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Bot B", "topic", 1, "assistant", "B argues", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Judge", "topic", 1, "assistant", "Judge says", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Bot A", "topic", 1, "stance", "Stance data", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Bot A", "topic", 1, "fact-check", "FC data", DateTimeOffset.UtcNow, []),
        };
        var messages = CounterfactualReplayService.ReconstructDebateHistory(entries, "Bot A", "Bot B");
        // Only "assistant" role entries should be included
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public void ReconstructDebateHistory_BotAMappedToAssistant_BotBMappedToUser()
    {
        var entries = new List<MemoryEntry>
        {
            new(Guid.NewGuid(), "Bot A", "topic", 1, "assistant", "A's point", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Bot B", "topic", 1, "assistant", "B's counter", DateTimeOffset.UtcNow, []),
        };
        var messages = CounterfactualReplayService.ReconstructDebateHistory(entries, "Bot A", "Bot B");
        Assert.Equal("assistant", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
    }
}

// ── SQLite schema v3: TotalTokens, EstimatedCostUsd, Complexity persisted ───

public sealed class SqliteSchemaV3Tests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vw_v3_{Guid.NewGuid():N}.db");
    private readonly SqliteMemoryStore _store;

    public SqliteSchemaV3Tests() => _store = new SqliteMemoryStore(_dbPath);

    [Fact]
    public async Task SaveSession_NewFields_PersistAndReload()
    {
        var session = new DebateSession(
            Guid.NewGuid(), "test topic",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            "Bot A", "Good synthesis",
            "Structured", 12345, 0.0567m, "Academic");

        await _store.SaveSessionAsync(session, []);

        var sessions = await _store.ListSessionsAsync(10);
        var loaded = sessions.FirstOrDefault(s => s.SessionId == session.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal(12345, loaded!.TotalTokens);
        Assert.NotNull(loaded.EstimatedCostUsd);
        Assert.Equal(0.0567m, loaded.EstimatedCostUsd!.Value, 4);
        Assert.Equal("Academic", loaded.Complexity);
    }

    [Fact]
    public async Task SaveSession_NullCost_ReloadsAsNull()
    {
        var session = new DebateSession(
            Guid.NewGuid(), "null cost",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            "Tie", "", "Freeform", 0, null, "Standard");

        await _store.SaveSessionAsync(session, []);

        var sessions = await _store.ListSessionsAsync(10);
        var loaded = sessions.First(s => s.SessionId == session.SessionId);
        Assert.Null(loaded.EstimatedCostUsd);
        Assert.Equal(0, loaded.TotalTokens);
        Assert.Equal("Standard", loaded.Complexity);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

// ── HybridMemoryStore: SqliteStore property exposed for AsSqlite helper ─────

public sealed class HybridMemoryStoreSqlitePropertyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vw_hybrid_prop_{Guid.NewGuid():N}.db");
    private readonly SqliteMemoryStore _sqlite;

    public HybridMemoryStoreSqlitePropertyTests()
    {
        _sqlite = new SqliteMemoryStore(_dbPath);
    }

    [Fact]
    public void SqliteStore_ReturnsSameInstance()
    {
        var mockS3 = new Mock<IAmazonS3>();
        mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new PutObjectResponse());
        var s3 = new S3MemoryStore(mockS3.Object, bucket: "b", prefix: "p/");
        using var hybrid = new HybridMemoryStore(_sqlite, s3);

        Assert.Same(_sqlite, hybrid.SqliteStore);
    }

    public void Dispose()
    {
        _sqlite.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

// ── ConfigLoader: --red-team / --format redteam linkage ─────────────────────

public sealed class ConfigLoaderRedTeamLinkageTests
{
    [Fact]
    public void RedTeamFlag_AutoSetsFormatRedTeam()
    {
        var config = ConfigLoader.Load(["--red-team", "topic"]);
        Assert.True(config.RedTeam);
        Assert.Equal("redteam", config.DebateFormat, ignoreCase: true);
    }

    [Fact]
    public void FormatRedTeam_AutoEnablesRedTeam()
    {
        var config = ConfigLoader.Load(["--format", "redteam", "topic"]);
        Assert.True(config.RedTeam);
    }

    [Fact]
    public void RedTeamFlag_WithExplicitFormat_DoesNotOverrideFormat()
    {
        var config = ConfigLoader.Load(["--red-team", "--format", "structured", "topic"]);
        Assert.True(config.RedTeam);
        Assert.Equal("structured", config.DebateFormat);
    }
}

// ── ConfigLoader: --max-cost-usd parsing ─────────────────────────────────────

public sealed class ConfigLoaderMaxCostTests
{
    [Fact]
    public void MaxCostUsd_ParsedFromCli()
    {
        var config = ConfigLoader.Load(["--max-cost-usd", "0.50", "topic"]);
        Assert.NotNull(config.MaxCostUsd);
        Assert.Equal(0.50m, config.MaxCostUsd!.Value);
    }

    [Fact]
    public void MaxCostUsd_InvalidValue_NotSet()
    {
        var config = ConfigLoader.Load(["--max-cost-usd", "abc", "topic"]);
        Assert.Null(config.MaxCostUsd);
    }
}

// ── ConfigLoader: --commentator-model and --commentator-style ────────────────

public sealed class ConfigLoaderCommentatorFlagTests
{
    [Fact]
    public void CommentatorModel_ParsedFromCli()
    {
        var config = ConfigLoader.Load(["--commentator", "--commentator-model", "openai/gpt-4o", "topic"]);
        Assert.Equal("openai/gpt-4o", config.CommentatorModel);
    }

    [Fact]
    public void CommentatorStyle_ParsedFromCli()
    {
        var config = ConfigLoader.Load(["--commentator", "--commentator-style", "snarky", "topic"]);
        Assert.Equal("snarky", config.CommentatorStyle);
    }
}

// ── CommentatorService: style-neutral base prompt ────────────────────────────

public sealed class CommentatorBasePromptTests
{
    [Theory]
    [InlineData(CommentatorStyle.Academic)]
    [InlineData(CommentatorStyle.Snarky)]
    [InlineData(CommentatorStyle.Dramatic)]
    [InlineData(CommentatorStyle.Dry)]
    public void GetSystemPrompt_NonSports_DoesNotMentionSportsAnnouncer(CommentatorStyle style)
    {
        // Regression: base prompt previously hardcoded "in the style of a sports announcer"
        // which contradicted non-sports styles
        var svc = new CommentatorService(null!, style);
        var prompt = svc.GetSystemPrompt(style);
        Assert.DoesNotContain("sports announcer", prompt);
    }
}

// ── DebateComplexityService: prompt suffixes ─────────────────────────────────

public sealed class DebateComplexityPromptTests
{
    [Fact]
    public void Academic_BotSuffix_MentionsCitations()
    {
        var suffix = DebateComplexityService.GetBotPromptSuffix(DebateComplexity.Academic);
        Assert.Contains("citation", suffix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PolicyBrief_JudgeSuffix_MentionsStakeholders()
    {
        var suffix = DebateComplexityService.GetJudgePromptSuffix(DebateComplexity.PolicyBrief);
        Assert.Contains("stakeholder", suffix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Standard_BotSuffix_Empty()
    {
        var suffix = DebateComplexityService.GetBotPromptSuffix(DebateComplexity.Standard);
        Assert.Equal(string.Empty, suffix);
    }
}

// ── DebateReportGenerator: EscapeYaml handles backslashes ────────────────────

public sealed class EscapeYamlTests
{
    [Fact]
    public void EscapeYaml_Backslash_Escaped()
    {
        // Access private static method via the public GenerateMarkdown output
        var session = new DebateSession(
            Guid.NewGuid(), @"topic with \backslash",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            "Tie", "");
        var md = DebateReportGenerator.GenerateMarkdown(session, []);
        // The YAML front matter should escape the backslash
        Assert.Contains(@"topic with \\backslash", md);
    }

    [Fact]
    public void EscapeYaml_Quotes_Escaped()
    {
        var session = new DebateSession(
            Guid.NewGuid(), "topic with \"quotes\"",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            "Tie", "");
        var md = DebateReportGenerator.GenerateMarkdown(session, []);
        Assert.Contains("topic with \\\"quotes\\\"", md);
    }
}

// ── DebateReportGenerator: Complexity shown in reports ───────────────────────

public sealed class ReportComplexityTests
{
    [Fact]
    public void GenerateMarkdown_NonStandardComplexity_Included()
    {
        var session = new DebateSession(
            Guid.NewGuid(), "test",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            "Tie", "", "Freeform", 0, null, "Academic");
        var md = DebateReportGenerator.GenerateMarkdown(session, []);
        Assert.Contains("complexity:", md);       // YAML front matter
        Assert.Contains("**Complexity:**", md);   // body
        Assert.Contains("Academic", md);
    }

    [Fact]
    public void GenerateMarkdown_StandardComplexity_Omitted()
    {
        var session = new DebateSession(
            Guid.NewGuid(), "test",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            "Tie", "", "Freeform", 0, null, "Standard");
        var md = DebateReportGenerator.GenerateMarkdown(session, []);
        Assert.DoesNotContain("complexity:", md);
    }
}

// ── AdversarialBriefingService: ShouldBrief filters on BotName+Role ─────────

public sealed class ShouldBriefFilterTests
{
    [Fact]
    public void ShouldBrief_ThreeAssistantEntries_ReturnsTrue()
    {
        var entries = new List<MemoryEntry>
        {
            new(Guid.NewGuid(), "Bot A", "AI", 1, "assistant", "arg1", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Bot B", "AI", 1, "assistant", "arg2", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Bot A", "AI", 2, "assistant", "arg3", DateTimeOffset.UtcNow, []),
        };
        Assert.True(AdversarialBriefingService.ShouldBrief(entries, "AI"));
    }

    [Fact]
    public void ShouldBrief_NonAssistantRoles_NotCounted()
    {
        // Entries with roles like "stance", "fact-check" should not count
        var entries = new List<MemoryEntry>
        {
            new(Guid.NewGuid(), "Bot A", "AI", 1, "assistant", "arg1", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Bot A", "AI", 1, "stance", "Stance: 3", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Bot B", "AI", 1, "fact-check", "HIGH: claim", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "Judge", "AI", 1, "assistant", "verdict", DateTimeOffset.UtcNow, []),
        };
        // Only 1 entry matches (Bot A, assistant) - Judge entries don't count
        Assert.False(AdversarialBriefingService.ShouldBrief(entries, "AI"));
    }
}

// ── StrategyEngine: ParseStrategy handles various formats ────────────────────

public sealed class StrategyParseTests
{
    [Fact]
    public void ParseStrategy_ValidJson_Parses()
    {
        var json = """{"tactic": "Reductio", "target_weakness": "weak premise", "execution_hint": "push to extreme", "confidence": 0.85}""";
        var result = StrategyEngine.ParseStrategy(json);
        Assert.Equal("Reductio", result.TacticName);
        Assert.Equal(0.85, result.ConfidenceScore, 2);
    }

    [Fact]
    public void ParseStrategy_ConfidenceClampedTo01()
    {
        var json = """{"tactic": "T", "confidence": 5.0}""";
        var result = StrategyEngine.ParseStrategy(json);
        Assert.Equal(1.0, result.ConfidenceScore);
    }

    [Fact]
    public void ParseStrategy_Malformed_ReturnsAdaptive()
    {
        var result = StrategyEngine.ParseStrategy("garbage");
        Assert.Equal("Adaptive", result.TacticName);
        Assert.Equal(0.5, result.ConfidenceScore);
    }

    [Fact]
    public void GetHistoricalTacticSuccessRates_ComputesCorrectly()
    {
        var records = new List<StrategyRecord>
        {
            new("Bot A", "Reductio", 1, "s1", 1),
            new("Bot A", "Reductio", 2, "s1", 0),
            new("Bot A", "Appeal", 1, "s2", 1),
        };
        var rates = StrategyEngine.GetHistoricalTacticSuccessRates(records);
        Assert.Equal(0.5, rates["Reductio"], 2);  // 1 win out of 2
        Assert.Equal(1.0, rates["Appeal"], 2);     // 1 win out of 1
    }
}

// ── DebateFormat: RedTeam format included ────────────────────────────────────

public sealed class DebateFormatParserTests
{
    [Fact]
    public void Parse_RedTeam_ReturnsRedTeamFormat()
    {
        var format = DebateFormatHelper.Parse("redteam");
        Assert.Equal(DebateFormat.RedTeam, format);
    }

    [Fact]
    public void Parse_CaseInsensitive()
    {
        Assert.Equal(DebateFormat.Structured, DebateFormatHelper.Parse("STRUCTURED"));
        Assert.Equal(DebateFormat.Oxford, DebateFormatHelper.Parse("Oxford"));
    }

    [Fact]
    public void GetTurnInstruction_RedTeam_Round1_MentionsProposal()
    {
        var instruction = DebateFormatHelper.GetTurnInstruction(DebateFormat.RedTeam, 1, 3, false);
        Assert.Contains("proposal", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetTurnInstruction_RedTeam_FinalRound_MentionsVulnerabilities()
    {
        var instruction = DebateFormatHelper.GetTurnInstruction(DebateFormat.RedTeam, 3, 3, true);
        Assert.Contains("vulnerabilit", instruction, StringComparison.OrdinalIgnoreCase);
    }
}

// ── EloService: rating math ─────────────────────────────────────────────────

public sealed class EloRatingMathTests
{
    [Fact]
    public void ComputeEloDelta_Win_PositiveDelta()
    {
        var delta = VibeWars.Elo.EloService.ComputeEloDelta(1200, 1200, 1.0, 32);
        Assert.True(delta > 0);
        Assert.Equal(16.0, delta, 1); // equal ratings, K=32, win = 16 points
    }

    [Fact]
    public void ComputeEloDelta_Loss_NegativeDelta()
    {
        var delta = VibeWars.Elo.EloService.ComputeEloDelta(1200, 1200, 0.0, 32);
        Assert.True(delta < 0);
        Assert.Equal(-16.0, delta, 1);
    }

    [Fact]
    public void ComputeEloDelta_Draw_ZeroDelta_WhenEqualRatings()
    {
        var delta = VibeWars.Elo.EloService.ComputeEloDelta(1200, 1200, 0.5, 32);
        Assert.Equal(0.0, delta, 1);
    }

    [Fact]
    public void ComputeEloDelta_Upset_LargerSwing()
    {
        // Low-rated player beats high-rated
        var deltaUpset = VibeWars.Elo.EloService.ComputeEloDelta(1000, 1400, 1.0, 32);
        var deltaNormal = VibeWars.Elo.EloService.ComputeEloDelta(1400, 1000, 1.0, 32);
        Assert.True(deltaUpset > deltaNormal);
    }

    [Fact]
    public void RatingToSparkline_SingleValue_ReturnsChar()
    {
        var spark = VibeWars.Elo.EloService.RatingToSparkline([1200.0]);
        Assert.Equal(1, spark.Length);
    }

    [Fact]
    public void RatingToSparkline_Empty_ReturnsEmpty()
    {
        var spark = VibeWars.Elo.EloService.RatingToSparkline([]);
        Assert.Equal("", spark);
    }
}

// ── CounterfactualReplayService: comparison report ──────────────────────────

public sealed class CounterfactualComparisonTests
{
    [Fact]
    public void BuildComparisonReport_DifferentWinners_FlagsDifference()
    {
        var report = CounterfactualReplayService.BuildComparisonReport(
            Guid.NewGuid(), Guid.NewGuid(),
            [new CounterfactualRoundResult(1, "Bot A", "Bot B")],
            "Bot A", "Bot B");
        Assert.True(report.DifferentOverallWinner);
    }

    [Fact]
    public void BuildComparisonReport_SameWinner_NoDifference()
    {
        var report = CounterfactualReplayService.BuildComparisonReport(
            Guid.NewGuid(), Guid.NewGuid(),
            [new CounterfactualRoundResult(1, "Bot A", "Bot A")],
            "Bot A", "Bot A");
        Assert.False(report.DifferentOverallWinner);
    }

    [Fact]
    public void RenderComparisonReport_ContainsSessionIds()
    {
        var origId = Guid.NewGuid();
        var replayId = Guid.NewGuid();
        var report = CounterfactualReplayService.BuildComparisonReport(
            origId, replayId, [], "Bot A", "Bot A");
        var rendered = CounterfactualReplayService.RenderComparisonReport(report);
        Assert.Contains(origId.ToString()[..8], rendered);
        Assert.Contains(replayId.ToString()[..8], rendered);
    }
}

// ── FollowUpService: parsing and sorting ─────────────────────────────────────

public sealed class FollowUpServiceRegressionTests
{
    [Fact]
    public void ParseFollowUps_ValidJson_ReturnsTopics()
    {
        var json = """{"topics": [{"topic": "AI safety", "rationale": "Important", "difficulty": "hard"}]}""";
        var result = VibeWars.FollowUp.FollowUpService.ParseFollowUps(json);
        Assert.Single(result);
        Assert.Equal("AI safety", result[0].Topic);
        Assert.Equal("hard", result[0].Difficulty);
    }

    [Fact]
    public void ParseFollowUps_MarkdownWrapped_Parses()
    {
        var json = "```json\n{\"topics\": [{\"topic\": \"Ethics\", \"rationale\": \"R\", \"difficulty\": \"medium\"}]}\n```";
        var result = VibeWars.FollowUp.FollowUpService.ParseFollowUps(json);
        Assert.Single(result);
    }

    [Fact]
    public void ParseFollowUps_Malformed_ReturnsEmpty()
    {
        var result = VibeWars.FollowUp.FollowUpService.ParseFollowUps("not json");
        Assert.Empty(result);
    }

    [Fact]
    public void SortByRecurrence_MostFrequent_First()
    {
        var topics = new List<VibeWars.FollowUp.FollowUpTopic>
        {
            new("Rare", "R", "easy"),
            new("Common", "R", "medium"),
            new("Common", "R", "medium"),
        };
        var sorted = VibeWars.FollowUp.FollowUpService.SortByRecurrence(topics, topics);
        Assert.Equal("Common", sorted[0].Topic);
    }
}
