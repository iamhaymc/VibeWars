using VibeWars.Analytics;
using VibeWars.Balancing;
using VibeWars.Config;
using VibeWars.Fallacy;
using VibeWars.Highlights;
using VibeWars.JudgePanel;
using VibeWars.Matchup;
using VibeWars.Models;
using VibeWars.Momentum;
using VibeWars.Planning;
using VibeWars.Reports;

namespace VibeWars.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Wave 4: Dramatic Intelligence Tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MomentumTrackerTests
{
    [Fact]
    public void RecordRound_ThreeConsecutiveWins_DetectsStreak()
    {
        var tracker = new MomentumTracker();
        tracker.RecordRound(1, "Bot A");
        tracker.RecordRound(2, "Bot A");
        tracker.RecordRound(3, "Bot A");
        Assert.Contains(tracker.Events, e => e.Type == MomentumEventType.Streak);
    }

    [Fact]
    public void RecordRound_Comeback_DetectedAfterTrailing()
    {
        var tracker = new MomentumTracker();
        tracker.RecordRound(1, "Bot B");
        tracker.RecordRound(2, "Bot B");
        tracker.RecordRound(3, "Bot A"); // Bot A was down 0-2, wins round 3
        Assert.Contains(tracker.Events, e => e.Type == MomentumEventType.Comeback && e.BotName == "Bot A");
    }

    [Fact]
    public void RecordRound_AudienceFlip_DetectsMomentumShift()
    {
        var tracker = new MomentumTracker();
        tracker.RecordRound(1, "Bot A", 60, 40);
        tracker.RecordRound(2, "Bot B", 40, 60); // audience flipped
        Assert.Contains(tracker.Events, e => e.Type == MomentumEventType.MomentumShift);
    }

    [Fact]
    public void CheckClutchRound_FinalRoundTied_Detected()
    {
        var tracker = new MomentumTracker();
        tracker.CheckClutchRound(3, 3, 1, 1);
        Assert.Contains(tracker.Events, e => e.Type == MomentumEventType.ClutchRound);
    }

    [Fact]
    public void CheckBlowout_CleanSweep_Detected()
    {
        var tracker = new MomentumTracker();
        tracker.RecordRound(1, "Bot A");
        tracker.RecordRound(2, "Bot A");
        tracker.RecordRound(3, "Bot A");
        tracker.CheckBlowout(3, 3, 0);
        Assert.Contains(tracker.Events, e => e.Type == MomentumEventType.Blowout);
    }

    [Fact]
    public void CheckUpset_LowerRatedLeading_Detected()
    {
        var tracker = new MomentumTracker();
        tracker.CheckUpset(2, 1050, 1300, 2, 0);
        Assert.Contains(tracker.Events, e => e.Type == MomentumEventType.Upset && e.BotName == "Bot A");
    }

    [Fact]
    public void RenderMomentumBar_ProducesNonEmptyString()
    {
        var tracker = new MomentumTracker();
        var bar = tracker.RenderMomentumBar(2, 1, 3);
        Assert.Contains("[Bot A]", bar);
        Assert.Contains("[Bot B]", bar);
    }
}

public sealed class MatchupServiceTests
{
    [Fact]
    public void PredictWinProbability_EqualRatings_Returns50Percent()
    {
        var prob = MatchupService.PredictWinProbability(1200, 1200);
        Assert.Equal(0.5, prob, 2);
    }

    [Fact]
    public void PredictWinProbability_HigherRated_HigherProbability()
    {
        var prob = MatchupService.PredictWinProbability(1400, 1200);
        Assert.True(prob > 0.7);
    }

    [Fact]
    public void BuildCard_ReturnsValidCard()
    {
        var card = MatchupService.BuildCard(null, null);
        Assert.Equal(1200, card.EloA);
        Assert.Equal(0.5, card.PredictionA, 2);
    }

    [Fact]
    public void RenderCard_ContainsPreDebateHeader()
    {
        var card = MatchupService.BuildCard(null, null);
        var rendered = MatchupService.RenderCard(card);
        Assert.Contains("PRE-DEBATE ANALYSIS", rendered);
    }
}

public sealed class HighlightServiceTests
{
    [Fact]
    public void ExtractHighlights_ReturnsTopKByScore()
    {
        var scores = new List<ArgumentStrengthScore>
        {
            new(1, "Bot A", 9, 8, 9, 8.7),
            new(1, "Bot B", 5, 4, 5, 4.7),
            new(2, "Bot A", 6, 7, 6, 6.3),
        };
        var args = new List<(int Round, string BotName, string Content)>
        {
            (1, "Bot A", "Strong argument"),
            (1, "Bot B", "Weak argument"),
            (2, "Bot A", "Medium argument"),
        };
        var highlights = HighlightService.ExtractHighlights(scores, [], args, topK: 2);
        Assert.Equal(2, highlights.Count);
        Assert.Equal("Bot A", highlights[0].BotName);
    }

    [Fact]
    public void RenderHighlights_Empty_ReturnsEmpty()
    {
        var result = HighlightService.RenderHighlights([]);
        Assert.Equal("", result);
    }
}

public sealed class JudgeControversyTests
{
    [Fact]
    public void ComputeControversy_Unanimous_ReturnsZero()
    {
        var verdicts = new List<JudgeVerdict>
        {
            new("Bot A", "R1", ""),
            new("Bot A", "R2", ""),
            new("Bot A", "R3", ""),
        };
        Assert.Equal(0.0, JudgePanelService.ComputeControversy(verdicts), 2);
    }

    [Fact]
    public void ComputeControversy_SplitDecision_ReturnsPositive()
    {
        var verdicts = new List<JudgeVerdict>
        {
            new("Bot A", "R1", ""),
            new("Bot B", "R2", ""),
            new("Bot A", "R3", ""),
        };
        var score = JudgePanelService.ComputeControversy(verdicts);
        Assert.True(score > 0.3);
    }

    [Fact]
    public void ComputeControversy_SingleJudge_ReturnsZero()
    {
        Assert.Equal(0.0, JudgePanelService.ComputeControversy([new("Bot A", "R", "")]));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Wave 5: Smarter Bots Tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ArgumentPlannerTests
{
    [Fact]
    public void ParsePlan_ValidJson_Parses()
    {
        var json = """{"strongest_point": "Data shows X", "evidence_strategy": "Cite study", "anticipated_counter": "They'll say Y", "preemptive_move": "Address Y first"}""";
        var plan = ArgumentPlanner.ParsePlan(json);
        Assert.Equal("Data shows X", plan.StrongestPoint);
        Assert.Equal("Address Y first", plan.PreemptiveMove);
    }

    [Fact]
    public void ParsePlan_Malformed_ReturnsEmpty()
    {
        var plan = ArgumentPlanner.ParsePlan("bad json");
        Assert.Equal("", plan.StrongestPoint);
    }

    [Fact]
    public void FormatPlanInjection_EmptyPlan_ReturnsEmpty()
    {
        var plan = new ArgumentPlan("", "", "", "");
        Assert.Equal("", ArgumentPlanner.FormatPlanInjection(plan));
    }

    [Fact]
    public void FormatPlanInjection_WithContent_ContainsPLAN()
    {
        var plan = new ArgumentPlan("Strong point", "Evidence", "Counter", "Preempt");
        var injection = ArgumentPlanner.FormatPlanInjection(plan);
        Assert.Contains("[PLAN]", injection);
        Assert.Contains("Strong point", injection);
    }
}

public sealed class LookaheadServiceTests
{
    [Fact]
    public void ParseSketches_ValidJson_ReturnsSketches()
    {
        var json = """{"sketches": ["approach one", "approach two"]}""";
        var result = LookaheadService.ParseSketches(json);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseSketches_Malformed_ReturnsEmpty()
    {
        Assert.Empty(LookaheadService.ParseSketches("bad"));
    }
}

public sealed class FallacyDetectorTests
{
    [Fact]
    public void ParseResult_ValidJson_Parses()
    {
        var json = """{"has_fallacy": true, "fallacy_name": "Straw Man", "explanation": "Misrepresented the argument"}""";
        var result = FallacyDetectorService.ParseResult(json);
        Assert.True(result.HasFallacy);
        Assert.Equal("Straw Man", result.FallacyName);
    }

    [Fact]
    public void ParseResult_NoFallacy_ReturnsFalse()
    {
        var json = """{"has_fallacy": false, "fallacy_name": "", "explanation": ""}""";
        var result = FallacyDetectorService.ParseResult(json);
        Assert.False(result.HasFallacy);
    }

    [Fact]
    public void ParseResult_Malformed_ReturnsFalse()
    {
        var result = FallacyDetectorService.ParseResult("garbage");
        Assert.False(result.HasFallacy);
    }

    [Fact]
    public void FormatCallout_WithFallacy_ContainsName()
    {
        var result = new FallacyResult(true, "Ad Hominem", "Attacked the person");
        var callout = FallacyDetectorService.FormatCallout(result);
        Assert.Contains("Ad Hominem", callout);
    }

    [Fact]
    public void FormatCallout_NoFallacy_ReturnsEmpty()
    {
        var result = new FallacyResult(false, "", "");
        Assert.Equal("", FallacyDetectorService.FormatCallout(result));
    }
}

public sealed class DifficultyBalancerTests
{
    [Fact]
    public void Evaluate_BalancedMatch_ReturnsNull()
    {
        var result = DifficultyBalancer.Evaluate(1, 1, 5.0, 5.0);
        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_BotADominating_ReturnsBotBAdjustment()
    {
        var result = DifficultyBalancer.Evaluate(3, 0, 8.0, 4.0);
        Assert.NotNull(result);
        Assert.Equal("Bot B", result!.TargetBot);
    }

    [Fact]
    public void Evaluate_BotBDominating_ReturnsBotAAdjustment()
    {
        var result = DifficultyBalancer.Evaluate(0, 3, 4.0, 8.0);
        Assert.NotNull(result);
        Assert.Equal("Bot A", result!.TargetBot);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Wave 6: Social & Engagement Tests
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DebateCardGeneratorTests
{
    [Fact]
    public void GenerateSvg_ContainsTopicAndWinner()
    {
        var session = new DebateSession(Guid.NewGuid(), "AI Ethics",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            "Bot A", "Good debate", "Freeform", 0, null, "Standard");
        var svg = DebateCardGenerator.GenerateSvg(session, 1200, 1150, 2, 1, "Key quote");
        Assert.Contains("AI Ethics", svg);
        Assert.Contains("Bot A", svg);
        Assert.Contains("<svg", svg);
    }

    [Fact]
    public void GenerateText_ContainsSessionInfo()
    {
        var session = new DebateSession(Guid.NewGuid(), "Test Topic",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            "Bot B", "", "Structured", 0, null, "Academic");
        var text = DebateCardGenerator.GenerateText(session, 1, 2);
        Assert.Contains("Test Topic", text);
        Assert.Contains("Bot B", text);
    }
}

public sealed class PersonalityEvolutionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vw_pers_{Guid.NewGuid():N}.db");
    private readonly VibeWars.Clients.SqliteMemoryStore _store;
    private readonly VibeWars.Personality.PersonalityEvolutionService _svc;

    public PersonalityEvolutionTests()
    {
        _store = new VibeWars.Clients.SqliteMemoryStore(_dbPath);
        _svc = new VibeWars.Personality.PersonalityEvolutionService(_store.GetConnection());
    }

    [Fact]
    public void UpdateAfterDebate_Win_IncreasesOverconfidence()
    {
        _svc.UpdateAfterDebate("bot1", won: true, wasUpset: false, consecutiveWins: 3, consecutiveLosses: 0, "Reductio");
        var profile = _svc.GetProfile("bot1");
        var overconfident = profile.Traits.FirstOrDefault(t => t.Name == "Overconfident");
        Assert.NotNull(overconfident);
        Assert.True(overconfident!.Intensity > 0);
    }

    [Fact]
    public void UpdateAfterDebate_Loss_IncreasesCaution()
    {
        _svc.UpdateAfterDebate("bot2", won: false, wasUpset: false, consecutiveWins: 0, consecutiveLosses: 2, "");
        var profile = _svc.GetProfile("bot2");
        var cautious = profile.Traits.FirstOrDefault(t => t.Name == "Cautious");
        Assert.NotNull(cautious);
        Assert.True(cautious!.Intensity > 0);
    }

    [Fact]
    public void FormatTraitInjection_NoTraits_ReturnsEmpty()
    {
        var profile = new VibeWars.Personality.PersonalityProfile("x", []);
        Assert.Equal("", VibeWars.Personality.PersonalityEvolutionService.FormatTraitInjection(profile));
    }

    [Fact]
    public void FormatTraitInjection_StrongTrait_ContainsPersonality()
    {
        var profile = new VibeWars.Personality.PersonalityProfile("x",
            [new VibeWars.Personality.PersonalityTrait("Overconfident", 0.5)]);
        var result = VibeWars.Personality.PersonalityEvolutionService.FormatTraitInjection(profile);
        Assert.Contains("[PERSONALITY]", result);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

public sealed class ConfigLoaderWave4to6Tests
{
    [Fact]
    public void MomentumFlag_Parsed()
    {
        var config = ConfigLoader.Load(["--momentum", "topic"]);
        Assert.True(config.Momentum);
    }

    [Fact]
    public void PlanFlag_Parsed()
    {
        var config = ConfigLoader.Load(["--plan", "topic"]);
        Assert.True(config.Plan);
    }

    [Fact]
    public void StakesFlag_Parsed()
    {
        var config = ConfigLoader.Load(["--stakes", "escalating", "topic"]);
        Assert.Equal("escalating", config.StakesMode);
    }

    [Fact]
    public void KnowledgeFlag_Parsed()
    {
        var config = ConfigLoader.Load(["--knowledge", "wikipedia", "topic"]);
        Assert.Equal("wikipedia", config.KnowledgeSource);
    }

    [Fact]
    public void FallacyCheckFlag_Parsed()
    {
        var config = ConfigLoader.Load(["--fallacy-check", "topic"]);
        Assert.True(config.FallacyCheck);
    }

    [Fact]
    public void PersonalityFlag_Parsed()
    {
        var config = ConfigLoader.Load(["--personality", "topic"]);
        Assert.True(config.PersonalityEvolution);
    }

    [Fact]
    public void BotsFlag_ClampedTo2Through8()
    {
        var config = ConfigLoader.Load(["--bots", "15", "topic"]);
        Assert.Equal(8, config.BotCount);

        var config2 = ConfigLoader.Load(["--bots", "1", "topic"]);
        Assert.Equal(2, config2.BotCount);
    }
}

public sealed class KnowledgeFormatterTests
{
    [Fact]
    public void FormatForPrompt_EmptyPassages_ReturnsEmpty()
    {
        Assert.Equal("", VibeWars.Knowledge.KnowledgeFormatter.FormatForPrompt([]));
    }

    [Fact]
    public void FormatForPrompt_WithPassages_ContainsEvidence()
    {
        var passages = new List<VibeWars.Knowledge.KnowledgePassage>
        {
            new("Title", "Content about the topic", "https://example.com")
        };
        var result = VibeWars.Knowledge.KnowledgeFormatter.FormatForPrompt(passages);
        Assert.Contains("AVAILABLE EVIDENCE", result);
        Assert.Contains("Title", result);
    }
}
