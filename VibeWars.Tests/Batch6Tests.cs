using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using VibeWars.Elo;
using VibeWars.Audience;
using VibeWars.Commentator;
using VibeWars.Drift;
using VibeWars.Challenges;
using VibeWars.Reports;
using VibeWars.Webhook;
using VibeWars.Complexity;
using VibeWars.FollowUp;
using VibeWars.Models;
using VibeWars.Notifications;

namespace VibeWars.Tests;

// ── ELO Tests ─────────────────────────────────────────────────────────────────

public sealed class EloServiceTests
{
    [Fact]
    public void ComputeEloDelta_Win_ReturnsPositiveDelta()
    {
        double delta = EloService.ComputeEloDelta(1200, 1200, 1.0, 32);
        Assert.True(delta > 0);
    }

    [Fact]
    public void ComputeEloDelta_Loss_ReturnsNegativeDelta()
    {
        double delta = EloService.ComputeEloDelta(1200, 1200, 0.0, 32);
        Assert.True(delta < 0);
    }

    [Fact]
    public void ComputeEloDelta_Draw_EqualRatings_ReturnZero()
    {
        double delta = EloService.ComputeEloDelta(1200, 1200, 0.5, 32);
        Assert.Equal(0.0, delta, precision: 6);
    }

    [Fact]
    public void ComputeEloDelta_HigherRatedWins_SmallPositiveDelta()
    {
        // Expected win for higher-rated player → small delta
        double delta = EloService.ComputeEloDelta(1600, 1200, 1.0, 32);
        Assert.True(delta > 0 && delta < 16); // expected score is high → small gain
    }

    [Fact]
    public void ComputeEloDelta_LowerRatedWins_LargePositiveDelta()
    {
        // Upset win for lower-rated player → large delta
        double delta = EloService.ComputeEloDelta(1200, 1600, 1.0, 32);
        Assert.True(delta > 16); // expected score is low → large gain
    }

    [Fact]
    public void RatingToSparkline_ReturnsExpectedLength()
    {
        var ratings = new List<double> { 1200, 1250, 1230, 1270, 1300 };
        var sparkline = EloService.RatingToSparkline(ratings);
        Assert.Equal(ratings.Count, sparkline.Length);
    }

    [Fact]
    public void RatingToSparkline_AllSameValues_ReturnsSameCharacter()
    {
        var ratings = new List<double> { 1200, 1200, 1200, 1200 };
        var sparkline = EloService.RatingToSparkline(ratings);
        Assert.Equal(ratings.Count, sparkline.Length);
        // All chars should be the same (middle bar character for zero range)
        Assert.True(sparkline.Distinct().Count() == 1);
    }

    [Fact]
    public async Task EloService_GetOrCreate_NewContestant_Returns1200()
    {
        using var db = CreateMemoryDb();
        var svc = new EloService(db);
        var record = await svc.GetOrCreateAsync("bedrock/claude/Pragmatist");
        Assert.Equal(1200, record.Rating);
    }

    [Fact]
    public async Task EloService_UpdateRatings_Win_IncreasesWinnerRating()
    {
        using var db = CreateMemoryDb();
        var svc = new EloService(db);
        await svc.UpdateRatingsAsync("bedrock/claude/Pragmatist", "bedrock/titan/Idealist", false, false);
        var winner = await svc.GetOrCreateAsync("bedrock/claude/Pragmatist");
        Assert.True(winner.Rating > 1200);
    }

    [Fact]
    public void EloService_IsUnrated_FewerThan5Matches_ReturnsTrue()
    {
        using var db = CreateMemoryDb();
        var svc = new EloService(db);
        var record = new EloRecord { Wins = 2, Losses = 1, Draws = 0 };
        Assert.True(svc.IsUnrated(record));
    }

    private static SqliteConnection CreateMemoryDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }
}

// ── Audience Tests ─────────────────────────────────────────────────────────────

public sealed class AudienceSimulatorTests
{
    [Fact]
    public void ParseShiftResult_ValidJson_ReturnsCorrectShift()
    {
        var json = "{\"shift_a\": 5, \"shift_b\": -5, \"mood\": \"excited\"}";
        var result = AudienceSimulator.ParseShiftResult(json);
        Assert.NotNull(result);
        Assert.Equal(5, result.ShiftA);
        Assert.Equal(-5, result.ShiftB);
        Assert.Equal("excited", result.Mood);
    }

    [Fact]
    public void ParseShiftResult_InvalidJson_ReturnsNull()
    {
        var result = AudienceSimulator.ParseShiftResult("not valid json");
        Assert.Null(result);
    }

    [Fact]
    public void ApplyShift_BasicShift_UpdatesSupport()
    {
        var sim = new AudienceSimulator(50, 50);
        sim.ApplyShift(new AudienceShiftResult(10, -10, "excited"));
        Assert.NotEqual(50, sim.SupportA);
        Assert.NotEqual(50, sim.SupportB);
    }

    [Fact]
    public void ApplyShift_ClampsAt0And100()
    {
        var sim = new AudienceSimulator(5, 95);
        // Large negative shift on A should not go below 0
        sim.ApplyShift(new AudienceShiftResult(-200, 200, "bored"));
        Assert.True(sim.SupportA >= 0);
        Assert.True(sim.SupportB <= 100);
    }

    [Fact]
    public void ApplyShift_AlwaysSumsTo100()
    {
        var sim = new AudienceSimulator(50, 50);
        sim.ApplyShift(new AudienceShiftResult(15, -5, "engaged"));
        Assert.Equal(100, sim.SupportA + sim.SupportB);
    }

    [Fact]
    public void MoodEmoji_KnownMoods_ReturnsCorrectEmoji()
    {
        Assert.Equal("😊", AudienceSimulator.MoodEmoji("excited"));
        Assert.Equal("😤", AudienceSimulator.MoodEmoji("skeptical"));
        Assert.Equal("🤔", AudienceSimulator.MoodEmoji("engaged"));
        Assert.Equal("😴", AudienceSimulator.MoodEmoji("bored"));
    }

    [Fact]
    public void MoodEmoji_UnknownMood_ReturnsDefault()
    {
        Assert.Equal("🎭", AudienceSimulator.MoodEmoji("confused"));
    }

    [Fact]
    public void RenderPollBar_ContainsBotNames()
    {
        var sim = new AudienceSimulator(60, 40);
        var bar = sim.RenderPollBar("AlphaBot", "BetaBot");
        Assert.Contains("AlphaBot", bar);
        Assert.Contains("BetaBot", bar);
    }

    [Fact]
    public void RenderPollBar_ContainsPercentages()
    {
        var sim = new AudienceSimulator(60, 40);
        var bar = sim.RenderPollBar("AlphaBot", "BetaBot");
        Assert.Contains("60%", bar);
        Assert.Contains("40%", bar);
    }
}

// ── Commentator Tests ──────────────────────────────────────────────────────────

public sealed class CommentatorServiceTests
{
    [Fact]
    public void ParseStyle_Sports_ReturnsSports()
    {
        Assert.Equal(CommentatorStyle.Sports, CommentatorService.ParseStyle("sports"));
    }

    [Fact]
    public void ParseStyle_Snarky_ReturnsSnarky()
    {
        Assert.Equal(CommentatorStyle.Snarky, CommentatorService.ParseStyle("snarky"));
    }

    [Fact]
    public void ParseStyle_Unknown_ReturnsSports()
    {
        Assert.Equal(CommentatorStyle.Sports, CommentatorService.ParseStyle("unknown_style"));
    }

    [Fact]
    public void ParseStyle_CaseInsensitive_Works()
    {
        Assert.Equal(CommentatorStyle.Academic, CommentatorService.ParseStyle("ACADEMIC"));
    }

    [Fact]
    public void GetStyleInstruction_EachStyle_ReturnsNonEmpty()
    {
        foreach (CommentatorStyle style in Enum.GetValues<CommentatorStyle>())
        {
            var instruction = CommentatorService.GetStyleInstruction(style);
            Assert.False(string.IsNullOrWhiteSpace(instruction), $"Style {style} returned empty instruction");
        }
    }

    [Fact]
    public void GetSystemPrompt_ContainsBasePrompt()
    {
        // Use a fake client since we're only testing the prompt string
        var svc = new CommentatorService(new FakeChatClient(), CommentatorStyle.Sports);
        var prompt = svc.GetSystemPrompt(CommentatorStyle.Sports);
        Assert.Contains("debate commentator", prompt);
    }

    [Fact]
    public void CommentaryEntries_TaggedCorrectly()
    {
        // Verify the "commentary" tag string constant used for role tagging
        const string commentaryRole = "commentary";
        var entry = new MemoryEntry(
            Guid.NewGuid(), "Commentator", "Topic", 1, commentaryRole, "Great move!",
            DateTimeOffset.UtcNow, new[] { commentaryRole });
        Assert.Equal("commentary", entry.Role);
        Assert.Contains("commentary", entry.Tags);
    }

    // Minimal fake client for tests that need an IChatClient but won't actually call it
    private sealed class FakeChatClient : VibeWars.Clients.IChatClient
    {
        public string ProviderName => "Fake";
        public string ModelId => "fake-model";
        public Task<(string Reply, VibeWars.Models.TokenUsage Usage)> ChatAsync(
            string systemPrompt,
            IReadOnlyList<VibeWars.Models.ChatMessage> history,
            CancellationToken ct = default) => Task.FromResult(("", VibeWars.Models.TokenUsage.Empty));
        public async IAsyncEnumerable<string> ChatStreamAsync(
            string systemPrompt,
            IReadOnlyList<VibeWars.Models.ChatMessage> history,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
        public void Dispose() { }
    }
}

// ── Drift Tests ────────────────────────────────────────────────────────────────

public sealed class OpinionDriftServiceTests
{
    [Fact]
    public void ComputeDriftVelocity_EmptyList_ReturnsZero()
    {
        var velocity = OpinionDriftService.ComputeDriftVelocity(new List<OpinionDriftRecord>());
        Assert.Equal(0.0, velocity);
    }

    [Fact]
    public void ComputeDriftVelocity_PositiveDeltas_ReturnsPositive()
    {
        var records = new List<OpinionDriftRecord>
        {
            new() { InitialStance = 40, FinalStance = 60, StanceDelta = 20 },
            new() { InitialStance = 50, FinalStance = 70, StanceDelta = 20 },
        };
        var velocity = OpinionDriftService.ComputeDriftVelocity(records);
        Assert.True(velocity > 0);
    }

    [Fact]
    public void ClassifyTrend_LowVelocity_ReturnsStable()
    {
        Assert.Equal(DriftTrend.Stable, OpinionDriftService.ClassifyTrend(0.0));
        Assert.Equal(DriftTrend.Stable, OpinionDriftService.ClassifyTrend(0.4));
        Assert.Equal(DriftTrend.Stable, OpinionDriftService.ClassifyTrend(-0.4));
    }

    [Fact]
    public void ClassifyTrend_HighPositiveVelocity_ReturnsDiverging()
    {
        Assert.Equal(DriftTrend.Diverging, OpinionDriftService.ClassifyTrend(1.0));
    }

    [Fact]
    public void ClassifyTrend_HighNegativeVelocity_ReturnsConverging()
    {
        Assert.Equal(DriftTrend.Converging, OpinionDriftService.ClassifyTrend(-1.0));
    }

    [Fact]
    public void OpinionDriftRecord_StanceDelta_IsAbsolute()
    {
        var record = new OpinionDriftRecord
        {
            InitialStance = 60,
            FinalStance   = 40,
            StanceDelta   = Math.Abs(40 - 60),
        };
        Assert.Equal(20, record.StanceDelta);
        Assert.True(record.StanceDelta >= 0);
    }

    [Fact]
    public async Task OpinionDriftService_SaveAndRetrieve_Works()
    {
        using var db = CreateMemoryDb();
        var svc = new OpinionDriftService(db);
        var sessionId = Guid.NewGuid();
        var record = new OpinionDriftRecord
        {
            SessionId     = sessionId,
            Topic         = "AI regulation",
            BotName       = "Bot A",
            Model         = "claude",
            Persona       = "Pragmatist",
            InitialStance = 50,
            FinalStance   = 70,
            StanceDelta   = 20,
            SessionDate   = DateTimeOffset.UtcNow,
        };
        await svc.SaveDriftRecordAsync(record);
        var retrieved = await svc.GetDriftRecordsAsync("AI regulation");
        Assert.Single(retrieved);
        Assert.Equal("Bot A", retrieved[0].BotName);
        Assert.Equal(20, retrieved[0].StanceDelta);
    }

    private static SqliteConnection CreateMemoryDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }
}

// ── Challenge Tests ────────────────────────────────────────────────────────────

public sealed class ChallengeServiceTests
{
    [Fact]
    public void ParseChallengeResult_ValidJson_ReturnsShouldChallenge()
    {
        var json = "{\"should_challenge\": true, \"type\": \"CitationNeeded\", \"target\": \"AI will take 80% of jobs\"}";
        var result = ChallengeService.ParseChallengeResult(json);
        Assert.NotNull(result);
        Assert.True(result.ShouldChallenge);
        Assert.Equal("CitationNeeded", result.Type);
        Assert.Equal("AI will take 80% of jobs", result.Target);
    }

    [Fact]
    public void ParseChallengeResult_ShouldChallengeFalse_ReturnsCorrectly()
    {
        var json = "{\"should_challenge\": false, \"type\": \"\", \"target\": \"\"}";
        var result = ChallengeService.ParseChallengeResult(json);
        Assert.NotNull(result);
        Assert.False(result.ShouldChallenge);
    }

    [Fact]
    public void ParseChallengeResult_InvalidJson_ReturnsNull()
    {
        var result = ChallengeService.ParseChallengeResult("this is not json");
        Assert.Null(result);
    }

    [Fact]
    public void ParseChallengeResult_MarkdownWrapped_ParsesCorrectly()
    {
        var json = "```json\n{\"should_challenge\": true, \"type\": \"PointOfOrder\", \"target\": \"some claim\"}\n```";
        var result = ChallengeService.ParseChallengeResult(json);
        Assert.NotNull(result);
        Assert.True(result.ShouldChallenge);
        Assert.Equal("PointOfOrder", result.Type);
    }

    [Fact]
    public void FormatInterruption_ContainsBotNameAndTarget()
    {
        var interruption = new DebateInterruption("Bot A", 2, ChallengeType.CitationNeeded, "claim about jobs", true);
        var formatted = ChallengeService.FormatInterruption(interruption);
        Assert.Contains("Bot A", formatted);
        Assert.Contains("claim about jobs", formatted);
    }

    [Fact]
    public void BuildInterruptionInjection_ContainsChallengeTarget()
    {
        var svc = new ChallengeService(new FakeChatClient());
        var challenge = new ChallengeDetectorResult(true, "CitationNeeded", "80% job loss claim");
        var injection = svc.BuildInterruptionInjection("Bot B", challenge);
        Assert.Contains("POINT OF ORDER", injection);
        Assert.Contains("80% job loss claim", injection);
        Assert.Contains("Bot B", injection);
    }

    private sealed class FakeChatClient : VibeWars.Clients.IChatClient
    {
        public string ProviderName => "Fake";
        public string ModelId => "fake-model";
        public Task<(string Reply, VibeWars.Models.TokenUsage Usage)> ChatAsync(
            string systemPrompt,
            IReadOnlyList<VibeWars.Models.ChatMessage> history,
            CancellationToken ct = default) => Task.FromResult(("", VibeWars.Models.TokenUsage.Empty));
        public async IAsyncEnumerable<string> ChatStreamAsync(
            string systemPrompt,
            IReadOnlyList<VibeWars.Models.ChatMessage> history,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
        public void Dispose() { }
    }
}

// ── Podcast Tests ──────────────────────────────────────────────────────────────

public sealed class PodcastScriptGeneratorTests
{
    private static DebateSession MakeSession(string topic = "AI and jobs") =>
        new(Guid.NewGuid(), topic, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
            "Bot A", "Both sides made good points.", "Freeform");

    private static MemoryEntry MakeEntry(
        string botName, string role, string content, int round = 1, string[]? tags = null) =>
        new(Guid.NewGuid(), botName, "AI and jobs", round, role, content,
            DateTimeOffset.UtcNow, tags ?? Array.Empty<string>());

    [Fact]
    public void EstimateRuntimeSeconds_WordCount_CalculatesCorrectly()
    {
        // 300 words at 150 wpm = 120 seconds
        var content = string.Join(" ", Enumerable.Repeat("word", 300));
        var entries = new[] { MakeEntry("Bot A", "assistant", content) };
        var seconds = PodcastScriptGenerator.EstimateRuntimeSeconds(entries);
        Assert.Equal(120, seconds);
    }

    [Fact]
    public void FormatRuntimeEstimate_ReturnsCorrectFormat()
    {
        var result = PodcastScriptGenerator.FormatRuntimeEstimate(90);
        Assert.Equal("1 min 30 sec", result);
    }

    [Fact]
    public void Generate_ContainsProductionHeader()
    {
        var session = MakeSession();
        var entries = new[] { MakeEntry("Bot A", "assistant", "Hello world") };
        var script = PodcastScriptGenerator.Generate(session, entries);
        Assert.Contains("SHOW TITLE", script);
    }

    [Fact]
    public void Generate_ContainsEpisodeTopic()
    {
        var session = MakeSession("Universal Basic Income");
        var entries = new[] { MakeEntry("Bot A", "assistant", "Hello world") };
        var script = PodcastScriptGenerator.Generate(session, entries);
        Assert.Contains("Universal Basic Income", script);
    }

    [Fact]
    public void Generate_ContainsOpeningMusic()
    {
        var session = MakeSession();
        var entries = new[] { MakeEntry("Bot A", "assistant", "Hello world") };
        var script = PodcastScriptGenerator.Generate(session, entries);
        Assert.Contains("[OPENING MUSIC", script);
    }

    [Fact]
    public void Generate_ContainsClosing()
    {
        var session = MakeSession();
        var entries = new[] { MakeEntry("Bot A", "assistant", "Hello world") };
        var script = PodcastScriptGenerator.Generate(session, entries);
        Assert.Contains("[CLOSING]", script);
    }

    [Fact]
    public void Generate_ContainsTransitionSting_WhenMultipleRounds()
    {
        var session = MakeSession();
        var entries = new MemoryEntry[]
        {
            MakeEntry("Bot A", "assistant", "Round 1 argument", round: 1),
            MakeEntry("Bot B", "assistant", "Round 2 argument", round: 2),
        };
        var script = PodcastScriptGenerator.Generate(session, entries);
        Assert.Contains("[TRANSITION STING]", script);
    }

    [Fact]
    public void Generate_ContainsFactCheckNotes_WhenPresent()
    {
        var session = MakeSession();
        var factEntry = MakeEntry("Bot A", "assistant", "Fact check content", round: 1,
            tags: new[] { "fact-check" });
        var entries = new[] { factEntry };
        var script = PodcastScriptGenerator.Generate(session, entries);
        Assert.Contains("[FACT CHECK NOTES]", script);
    }

    [Fact]
    public void Generate_RuntimeEstimate_InHeader()
    {
        var session = MakeSession();
        var entries = new[] { MakeEntry("Bot A", "assistant", "Hello world") };
        var script = PodcastScriptGenerator.Generate(session, entries);
        Assert.Contains("RUNTIME ESTIMATE", script);
    }
}

// ── Webhook Tests ──────────────────────────────────────────────────────────────

public sealed class WebhookServiceTests
{
    [Fact]
    public void LoadFromEnvironment_NoVars_ReturnsDefaultConfig()
    {
        // Ensure env vars are cleared
        Environment.SetEnvironmentVariable("VIBEWARS_WEBHOOK_URL", null);
        Environment.SetEnvironmentVariable("VIBEWARS_WEBHOOK_PROVIDER", null);

        var config = WebhookService.LoadFromEnvironment();
        Assert.Null(config.WebhookUrl);
        Assert.Equal(WebhookProvider.Generic, config.WebhookProvider);
        Assert.False(config.WebhookOnComplete);
        Assert.False(config.WebhookOnRound);
    }

    [Fact]
    public async Task BuildDiscordPayload_ContainsEmbedsKey()
    {
        var (service, handler) = MakeServiceWithHandler();
        var session = MakeSession();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Discord };
        await service.PostDebateSummaryAsync(session, Array.Empty<MemoryEntry>(), config);
        Assert.Contains("embeds", handler.LastBody ?? "");
    }

    [Fact]
    public async Task BuildSlackPayload_ContainsBlocksKey()
    {
        var (service, handler) = MakeServiceWithHandler();
        var session = MakeSession();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Slack };
        await service.PostDebateSummaryAsync(session, Array.Empty<MemoryEntry>(), config);
        Assert.Contains("blocks", handler.LastBody ?? "");
    }

    [Fact]
    public async Task BuildTeamsPayload_ContainsAdaptiveCard()
    {
        var (service, handler) = MakeServiceWithHandler();
        var session = MakeSession();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Teams };
        await service.PostDebateSummaryAsync(session, Array.Empty<MemoryEntry>(), config);
        Assert.Contains("AdaptiveCard", handler.LastBody ?? "");
    }

    [Fact]
    public async Task BuildTeamsPayload_ContainsDollarSchema()
    {
        var (service, handler) = MakeServiceWithHandler();
        var session = MakeSession();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Teams };
        await service.PostDebateSummaryAsync(session, Array.Empty<MemoryEntry>(), config);
        Assert.Contains("\"$schema\"", handler.LastBody ?? "");
    }

    [Fact]
    public async Task BuildDiscordPayload_ContainsSessionFooter()
    {
        var (service, handler) = MakeServiceWithHandler();
        var session = MakeSession();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Discord };
        await service.PostDebateSummaryAsync(session, Array.Empty<MemoryEntry>(), config);
        var body = handler.LastBody ?? "";
        Assert.Contains("footer", body);
        Assert.Contains(session.SessionId.ToString(), body);
    }

    [Fact]
    public async Task BuildSlackPayload_ContainsSessionContext()
    {
        var (service, handler) = MakeServiceWithHandler();
        var session = MakeSession();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Slack };
        await service.PostDebateSummaryAsync(session, Array.Empty<MemoryEntry>(), config);
        var body = handler.LastBody ?? "";
        Assert.Contains("context", body);
        Assert.Contains(session.SessionId.ToString(), body);
    }

    [Fact]
    public async Task BuildGenericPayload_ContainsSessionId()
    {
        var (service, handler) = MakeServiceWithHandler();
        var session = MakeSession();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Generic };
        await service.PostDebateSummaryAsync(session, Array.Empty<MemoryEntry>(), config);
        Assert.Contains(session.SessionId.ToString(), handler.LastBody ?? "");
    }

    private static DebateSession MakeSession() =>
        new(Guid.NewGuid(), "Test Topic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
            "Bot A", "Synthesis here.", "Freeform");

    private static (WebhookService Service, CapturingHandler Handler) MakeServiceWithHandler()
    {
        var handler = new CapturingHandler();
        var httpClient = new System.Net.Http.HttpClient(handler);
        return (new WebhookService(httpClient), handler);
    }

    private sealed class CapturingHandler : System.Net.Http.HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

// ── Complexity Tests ───────────────────────────────────────────────────────────

public sealed class DebateComplexityServiceTests
{
    [Fact]
    public void Parse_Casual_ReturnsCasual() =>
        Assert.Equal(DebateComplexity.Casual, DebateComplexityService.Parse("casual"));

    [Fact]
    public void Parse_Academic_ReturnsAcademic() =>
        Assert.Equal(DebateComplexity.Academic, DebateComplexityService.Parse("academic"));

    [Fact]
    public void Parse_PolicyBrief_ReturnsPolicyBrief() =>
        Assert.Equal(DebateComplexity.PolicyBrief, DebateComplexityService.Parse("policybrief"));

    [Fact]
    public void Parse_PolicyBriefWithDash_ReturnsPolicyBrief() =>
        Assert.Equal(DebateComplexity.PolicyBrief, DebateComplexityService.Parse("policy-brief"));

    [Fact]
    public void Parse_Unknown_ReturnsStandard() =>
        Assert.Equal(DebateComplexity.Standard, DebateComplexityService.Parse("xyz"));

    [Fact]
    public void Parse_CaseInsensitive() =>
        Assert.Equal(DebateComplexity.Technical, DebateComplexityService.Parse("TECHNICAL"));

    [Fact]
    public void GetBotPromptSuffix_Standard_ReturnsEmpty() =>
        Assert.Equal(string.Empty, DebateComplexityService.GetBotPromptSuffix(DebateComplexity.Standard));

    [Fact]
    public void GetBotPromptSuffix_Casual_ContainsConversational()
    {
        var suffix = DebateComplexityService.GetBotPromptSuffix(DebateComplexity.Casual);
        Assert.Contains("conversational", suffix);
    }

    [Fact]
    public void GetBotPromptSuffix_Academic_ContainsCitation()
    {
        var suffix = DebateComplexityService.GetBotPromptSuffix(DebateComplexity.Academic);
        Assert.Contains("citation", suffix.ToLowerInvariant());
    }

    [Fact]
    public void GetBotPromptSuffix_PolicyBrief_ContainsPolicy()
    {
        var suffix = DebateComplexityService.GetBotPromptSuffix(DebateComplexity.PolicyBrief);
        Assert.Contains("policy", suffix.ToLowerInvariant());
    }

    [Fact]
    public void GetJudgePromptSuffix_Academic_ContainsCitation()
    {
        var suffix = DebateComplexityService.GetJudgePromptSuffix(DebateComplexity.Academic);
        Assert.Contains("citation", suffix.ToLowerInvariant());
    }

    [Fact]
    public void GetJudgePromptSuffix_Standard_ReturnsEmpty() =>
        Assert.Equal(string.Empty, DebateComplexityService.GetJudgePromptSuffix(DebateComplexity.Standard));
}

// ── FollowUp Tests ─────────────────────────────────────────────────────────────

public sealed class FollowUpServiceTests
{
    [Fact]
    public void ParseFollowUps_ValidJson_ReturnsCorrectTopics()
    {
        var json = """
            {
              "topics": [
                {"topic": "UBI feasibility", "rationale": "Related to jobs", "difficulty": "medium"},
                {"topic": "Automation ethics", "rationale": "Ethics angle", "difficulty": "hard"}
              ]
            }
            """;
        var topics = FollowUpService.ParseFollowUps(json);
        Assert.Equal(2, topics.Count);
        Assert.Equal("UBI feasibility", topics[0].Topic);
        Assert.Equal("hard", topics[1].Difficulty);
    }

    [Fact]
    public void ParseFollowUps_InvalidJson_ReturnsEmpty()
    {
        var topics = FollowUpService.ParseFollowUps("not valid json at all");
        Assert.Empty(topics);
    }

    [Fact]
    public void ParseFollowUps_MarkdownWrapped_ParsesCorrectly()
    {
        var json = "```json\n{\"topics\": [{\"topic\": \"Test\", \"rationale\": \"r\", \"difficulty\": \"easy\"}]}\n```";
        var topics = FollowUpService.ParseFollowUps(json);
        Assert.Single(topics);
        Assert.Equal("Test", topics[0].Topic);
    }

    [Fact]
    public void ParseFollowUps_EmptyTopics_ReturnsEmpty()
    {
        var json = "{\"topics\": []}";
        var topics = FollowUpService.ParseFollowUps(json);
        Assert.Empty(topics);
    }

    [Fact]
    public void FormatFollowUpDisplay_ContainsHeader()
    {
        var topics = new List<FollowUpTopic>
        {
            new("Some debate topic", "Because it matters", "medium"),
        };
        var display = FollowUpService.FormatFollowUpDisplay(topics);
        Assert.Contains("💡 Suggested next debates:", display);
    }

    [Fact]
    public void FormatFollowUpDisplay_ContainsTopicText()
    {
        var topics = new List<FollowUpTopic>
        {
            new("Robot rights debate", "Relevant follow-on", "hard"),
        };
        var display = FollowUpService.FormatFollowUpDisplay(topics);
        Assert.Contains("Robot rights debate", display);
    }

    [Fact]
    public void SortByRecurrence_FrequentTopicFloatsToTop()
    {
        var candidate1 = new FollowUpTopic("Recurring topic", "r", "easy");
        var candidate2 = new FollowUpTopic("New topic", "r", "easy");
        var previous = new List<FollowUpTopic>
        {
            new("Recurring topic", "r", "easy"),
            new("Recurring topic", "r", "easy"),
        };
        var sorted = FollowUpService.SortByRecurrence(new[] { candidate1, candidate2 }, previous);
        Assert.Equal("Recurring topic", sorted[0].Topic);
    }

    [Fact]
    public void BuildFollowUpPrompt_ContainsSynthesis()
    {
        var synthesis = "Both sides agreed on core economic principles.";
        var prompt = FollowUpService.BuildFollowUpPrompt(synthesis);
        Assert.Contains(synthesis, prompt);
    }
}

// ── Webhook Round Summary Provider-Specific Tests ──────────────────────────────

public sealed class WebhookRoundSummaryTests
{
    [Fact]
    public async Task PostRoundSummary_Discord_ContainsEmbeds()
    {
        var (service, handler) = MakeServiceWithHandler();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Discord };
        await service.PostRoundSummaryAsync(1, "Bot A", "Bot A made a stronger argument.", config);
        Assert.Contains("embeds", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundSummary_Slack_ContainsBlocks()
    {
        var (service, handler) = MakeServiceWithHandler();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Slack };
        await service.PostRoundSummaryAsync(1, "Bot B", "Bot B made a stronger argument.", config);
        Assert.Contains("blocks", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundSummary_Teams_ContainsAdaptiveCard()
    {
        var (service, handler) = MakeServiceWithHandler();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Teams };
        await service.PostRoundSummaryAsync(2, "Bot A", "Strong evidence presented.", config);
        Assert.Contains("AdaptiveCard", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundSummary_Teams_ContainsDollarSchema()
    {
        var (service, handler) = MakeServiceWithHandler();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Teams };
        await service.PostRoundSummaryAsync(2, "Bot A", "Strong evidence presented.", config);
        Assert.Contains("\"$schema\"", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundSummary_Generic_ContainsRoundAndWinner()
    {
        var (service, handler) = MakeServiceWithHandler();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Generic };
        await service.PostRoundSummaryAsync(3, "Bot B", "Convincing rebuttal.", config);
        Assert.Contains("\"round\"", handler.LastBody ?? "");
        Assert.Contains("\"winner\"", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundSummary_Discord_ContainsRoundNumber()
    {
        var (service, handler) = MakeServiceWithHandler();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Discord };
        await service.PostRoundSummaryAsync(2, "Bot A", "Some reasoning.", config);
        Assert.Contains("Round 2", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundSummary_Slack_ContainsWinnerName()
    {
        var (service, handler) = MakeServiceWithHandler();
        var config = new WebhookConfig { WebhookUrl = "http://localhost/test", WebhookProvider = WebhookProvider.Slack };
        await service.PostRoundSummaryAsync(1, "Bot A", "Great argument.", config);
        Assert.Contains("Bot A", handler.LastBody ?? "");
    }

    private static (WebhookService Service, CapturingHandlerR Handler) MakeServiceWithHandler()
    {
        var handler = new CapturingHandlerR();
        var httpClient = new System.Net.Http.HttpClient(handler);
        return (new WebhookService(httpClient), handler);
    }

    private sealed class CapturingHandlerR : System.Net.Http.HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

// ── SlackNotifier Tests ────────────────────────────────────────────────────────

public sealed class SlackNotifierTests
{
    [Fact]
    public async Task PostDebateSummary_ContainsHeaderBlock()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var session = MakeSession();
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.Contains("header", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostDebateSummary_ContainsWinner()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var session = MakeSession();
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.Contains("Bot A", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostDebateSummary_ContainsTopic()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var session = MakeSession();
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.Contains("Test Topic", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostDebateSummary_ContainsBlocksKey()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var session = MakeSession();
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.Contains("blocks", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundResult_ContainsBlocks()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        await notifier.PostRoundResultAsync("http://localhost/test", 1, "Bot A", "Strong argument.");
        Assert.Contains("blocks", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundResult_ContainsRoundNumber()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        await notifier.PostRoundResultAsync("http://localhost/test", 2, "Bot B", "Stronger rebuttal.");
        Assert.Contains("Round 2", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundResult_ContainsWinner()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        await notifier.PostRoundResultAsync("http://localhost/test", 1, "Bot A", "Compelling evidence.");
        Assert.Contains("Bot A", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostDebateSummary_LongSynthesisTruncated()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var longSynthesis = new string('X', 800);
        var session = new DebateSession(Guid.NewGuid(), "Topic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
            "Bot A", longSynthesis, "Freeform");
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        // The body should not contain the full 800-char string untruncated
        Assert.DoesNotContain(longSynthesis, handler.LastBody ?? "");
    }

    private static DebateSession MakeSession() =>
        new(Guid.NewGuid(), "Test Topic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
            "Bot A", "Synthesis here.", "Freeform");

    private static (SlackNotifier Notifier, CapturingHandlerS Handler) MakeNotifierWithHandler()
    {
        var handler = new CapturingHandlerS();
        var httpClient = new System.Net.Http.HttpClient(handler);
        return (new SlackNotifier(httpClient), handler);
    }

    private sealed class CapturingHandlerS : System.Net.Http.HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

// ── DiscordNotifier Tests ──────────────────────────────────────────────────────

public sealed class DiscordNotifierTests
{
    [Fact]
    public async Task PostDebateSummary_ContainsEmbedsKey()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var session = MakeSession();
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.Contains("embeds", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostDebateSummary_ContainsWinner()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var session = MakeSession();
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.Contains("Bot A", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostDebateSummary_ContainsTopic()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var session = MakeSession();
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.Contains("Test Topic", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostDebateSummary_ContainsTitleField()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var session = MakeSession();
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.Contains("title", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundResult_ContainsEmbeds()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        await notifier.PostRoundResultAsync("http://localhost/test", 1, "Bot B", "Better argument.");
        Assert.Contains("embeds", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundResult_ContainsRoundNumber()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        await notifier.PostRoundResultAsync("http://localhost/test", 3, "Bot A", "Clear evidence.");
        Assert.Contains("Round 3", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostRoundResult_ContainsWinner()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        await notifier.PostRoundResultAsync("http://localhost/test", 1, "Bot B", "Strong rebuttal.");
        Assert.Contains("Bot B", handler.LastBody ?? "");
    }

    [Fact]
    public async Task PostDebateSummary_LongSynthesisTruncated()
    {
        var (notifier, handler) = MakeNotifierWithHandler();
        var longSynthesis = new string('Y', 1200);
        var session = new DebateSession(Guid.NewGuid(), "Topic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
            "Bot A", longSynthesis, "Freeform");
        await notifier.PostDebateSummaryAsync("http://localhost/test", session);
        Assert.DoesNotContain(longSynthesis, handler.LastBody ?? "");
    }

    private static DebateSession MakeSession() =>
        new(Guid.NewGuid(), "Test Topic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
            "Bot A", "Synthesis here.", "Freeform");

    private static (DiscordNotifier Notifier, CapturingHandlerD Handler) MakeNotifierWithHandler()
    {
        var handler = new CapturingHandlerD();
        var httpClient = new System.Net.Http.HttpClient(handler);
        return (new DiscordNotifier(httpClient), handler);
    }

    private sealed class CapturingHandlerD : System.Net.Http.HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
