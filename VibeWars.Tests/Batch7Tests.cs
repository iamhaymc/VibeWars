using System;
using System.Collections.Generic;
using System.Linq;
using VibeWars.Strategy;
using VibeWars.RedTeam;
using VibeWars.ArgumentGraph;
using VibeWars.Replay;
using VibeWars.HiddenObjective;
using VibeWars.Models;
using VibeWars.Tournament;

namespace VibeWars.Tests;

// ── StrategyEngine Tests ──────────────────────────────────────────────────────

public sealed class StrategyEngineParseTests
{
    [Fact]
    public void ParseStrategy_ValidJson_ReturnsCorrectValues()
    {
        var json = """{"tactic": "Strawman", "target_weakness": "vague premise", "execution_hint": "exaggerate opponent", "confidence": 0.8}""";
        var result = StrategyEngine.ParseStrategy(json);
        Assert.Equal("Strawman", result.TacticName);
        Assert.Equal("vague premise", result.TargetWeakness);
        Assert.Equal("exaggerate opponent", result.ExecutionHint);
        Assert.Equal(0.8, result.ConfidenceScore, precision: 5);
    }

    [Fact]
    public void ParseStrategy_MarkdownWrapped_ReturnsCorrectValues()
    {
        var json = "```json\n{\"tactic\": \"Ad Hominem\", \"target_weakness\": \"credibility\", \"execution_hint\": \"question authority\", \"confidence\": 0.6}\n```";
        var result = StrategyEngine.ParseStrategy(json);
        Assert.Equal("Ad Hominem", result.TacticName);
        Assert.Equal(0.6, result.ConfidenceScore, precision: 5);
    }

    [Fact]
    public void ParseStrategy_MalformedJson_ReturnsAdaptiveFallback()
    {
        var result = StrategyEngine.ParseStrategy("not valid json at all");
        Assert.Equal("Adaptive", result.TacticName);
        Assert.Equal(0.5, result.ConfidenceScore, precision: 5);
    }

    [Fact]
    public void ParseStrategy_EmptyJson_ReturnsAdaptiveFallback()
    {
        var result = StrategyEngine.ParseStrategy("{}");
        Assert.Equal("Adaptive", result.TacticName);
        Assert.Equal(0.5, result.ConfidenceScore, precision: 5);
    }

    [Fact]
    public void ParseStrategy_ConfidenceClampedToOne()
    {
        var json = """{"tactic": "X", "target_weakness": "", "execution_hint": "", "confidence": 2.5}""";
        var result = StrategyEngine.ParseStrategy(json);
        Assert.Equal(1.0, result.ConfidenceScore, precision: 5);
    }

    [Fact]
    public void ParseStrategy_ConfidenceClampedToZero()
    {
        var json = """{"tactic": "X", "target_weakness": "", "execution_hint": "", "confidence": -1.0}""";
        var result = StrategyEngine.ParseStrategy(json);
        Assert.Equal(0.0, result.ConfidenceScore, precision: 5);
    }

    [Fact]
    public void GetHistoricalTacticSuccessRates_MixedRecords_CorrectRates()
    {
        var records = new List<StrategyRecord>
        {
            new("bot1", "Strawman", 1, "s1", 1),
            new("bot1", "Strawman", 2, "s1", 0),
            new("bot1", "Analogy",  3, "s1", 1),
        };
        var rates = StrategyEngine.GetHistoricalTacticSuccessRates(records);
        Assert.Equal(0.5, rates["Strawman"], precision: 5);
        Assert.Equal(1.0, rates["Analogy"],  precision: 5);
    }

    [Fact]
    public void GetHistoricalTacticSuccessRates_EmptyRecords_ReturnsEmptyDict()
    {
        var rates = StrategyEngine.GetHistoricalTacticSuccessRates([]);
        Assert.Empty(rates);
    }

    [Fact]
    public void FormatStrategyHint_ContainsStrategyKeywords()
    {
        var strategy = new DebateStrategy("Socratic", "logic", "Ask questions", 0.7);
        var hint = StrategyEngine.FormatStrategyHint(strategy);
        Assert.Contains("[STRATEGY]", hint);
        Assert.Contains("Socratic", hint);
        Assert.Contains("Ask questions", hint);
    }
}

// ── VulnerabilityTracker Tests ────────────────────────────────────────────────

public sealed class VulnerabilityTrackerTests
{
    [Fact]
    public void ParseVulnerabilities_ValidJson_ReturnsParsedRecords()
    {
        var json = """[{"category": "LogicGap", "description": "Circular reasoning"}, {"category": "EdgeCase", "description": "Null input not handled"}]""";
        var results = VulnerabilityTracker.ParseVulnerabilities(json, 1);
        Assert.Equal(2, results.Count);
        Assert.Equal("LogicGap", results[0].Category);
        Assert.Equal("Circular reasoning", results[0].Description);
        Assert.Equal(VulnerabilityStatus.Open, results[0].Status);
    }

    [Fact]
    public void ParseVulnerabilities_MarkdownWrapped_ReturnsParsed()
    {
        var json = "```json\n[{\"category\": \"EthicalBlindSpot\", \"description\": \"Ignores minorities\"}]\n```";
        var results = VulnerabilityTracker.ParseVulnerabilities(json, 2);
        Assert.Single(results);
        Assert.Equal("EthicalBlindSpot", results[0].Category);
    }

    [Fact]
    public void ParseVulnerabilities_MalformedJson_ReturnsEmpty()
    {
        var results = VulnerabilityTracker.ParseVulnerabilities("not json", 1);
        Assert.Empty(results);
    }

    [Fact]
    public void ParseVulnerabilities_EmptyDescription_Skipped()
    {
        var json = """[{"category": "LogicGap", "description": "  "}, {"category": "EdgeCase", "description": "Real issue"}]""";
        var results = VulnerabilityTracker.ParseVulnerabilities(json, 1);
        Assert.Single(results);
        Assert.Equal("Real issue", results[0].Description);
    }

    [Fact]
    public void ApplyStatusUpdates_ValidJson_UpdatesStatus()
    {
        var tracker = new VulnerabilityTracker();
        // Seed by parsing directly
        var parsed = VulnerabilityTracker.ParseVulnerabilities(
            """[{"category": "LogicGap", "description": "Issue 1"}, {"category": "EdgeCase", "description": "Issue 2"}]""", 1);
        // Manually add to tracker via reflection-free approach using AddVulnerabilityAsync is async,
        // so instead test via the ApplyStatusUpdates method on a tracker that has records.
        // We'll access via the public interface: create a tracker, add via field, update via method.
        // Since _records is private, test the static parsing separately then test a round trip.

        var openList = parsed.ToList();
        var updateJson = """[{"index": 0, "status": "Patched"}, {"index": 1, "status": "Disputed"}]""";
        // Use a new tracker and add records via reflection-free path
        // The ApplyStatusUpdates method takes an open list; since _records is private,
        // we test with a seeded tracker using a helper.
        var seededTracker = CreateSeededTracker(openList);
        seededTracker.ApplyStatusUpdates(updateJson, seededTracker.Records);
        Assert.Equal(VulnerabilityStatus.Patched,  seededTracker.Records[0].Status);
        Assert.Equal(VulnerabilityStatus.Disputed, seededTracker.Records[1].Status);
    }

    [Fact]
    public void ApplyStatusUpdates_MalformedJson_NoChange()
    {
        var parsed = VulnerabilityTracker.ParseVulnerabilities(
            """[{"category": "LogicGap", "description": "Issue 1"}]""", 1);
        var tracker = CreateSeededTracker(parsed.ToList());
        tracker.ApplyStatusUpdates("bad json here", tracker.Records);
        Assert.Equal(VulnerabilityStatus.Open, tracker.Records[0].Status);
    }

    [Fact]
    public void RenderScorecard_WithRecords_ContainsExpectedText()
    {
        var parsed = VulnerabilityTracker.ParseVulnerabilities(
            """[{"category": "LogicGap", "description": "Some logical issue here"}]""", 1);
        var tracker = CreateSeededTracker(parsed.ToList());
        var scorecard = tracker.RenderScorecard();
        Assert.Contains("Vulnerability Scorecard", scorecard);
        Assert.Contains("LogicGap", scorecard);
    }

    [Fact]
    public void RenderScorecard_EmptyRecords_StillRendersHeader()
    {
        var tracker = new VulnerabilityTracker();
        var scorecard = tracker.RenderScorecard();
        Assert.Contains("Vulnerability Scorecard", scorecard);
    }

    // Helper: create a tracker with pre-seeded records (via ParseVulnerabilities path)
    private static VulnerabilityTracker CreateSeededTracker(IReadOnlyList<VulnerabilityRecord> records)
    {
        // VulnerabilityTracker._records is private, but ApplyStatusUpdates takes open list
        // We use a trick: embed records by calling AddVulnerabilityAsync is async, so
        // instead we use the fact that VulnerabilityTracker.ParseVulnerabilities returns records
        // and then call a workaround via a derived approach.
        //
        // Since _records is private with no direct seeding method, we can use a subclass trick.
        // However the simplest approach is to test via the public RenderScorecard / ApplyStatusUpdates
        // which work on _records. We'll use reflection as a last resort.
        var tracker = new VulnerabilityTracker();
        var field = typeof(VulnerabilityTracker).GetField("_records",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var list = (List<VulnerabilityRecord>)field.GetValue(tracker)!;
        list.AddRange(records);
        return tracker;
    }
}

// ── ClaimSurvivalAnalyzer Tests ───────────────────────────────────────────────

public sealed class ClaimSurvivalAnalyzerTests
{
    private static ArgumentNode MakeNode(Guid id, string bot, int round, string text) =>
        new(id, Guid.NewGuid(), round, bot, text, ClaimType.Assertion);

    [Fact]
    public void ParseOutcome_Refuted_ReturnsRefutedLifecycle()
    {
        var id = Guid.NewGuid();
        var json = """{"claim_id": "abc", "outcome": "Refuted"}""";
        var evt = ClaimSurvivalAnalyzer.ParseOutcome(json, id, 1, "trigger text");
        Assert.NotNull(evt);
        Assert.Equal(ClaimLifecycle.Refuted, evt!.NewStatus);
        Assert.Equal(id, evt.ClaimId);
    }

    [Fact]
    public void ParseOutcome_Defended_ReturnsDefendedLifecycle()
    {
        var id = Guid.NewGuid();
        var json = """{"claim_id": "abc", "outcome": "Defended"}""";
        var evt = ClaimSurvivalAnalyzer.ParseOutcome(json, id, 2, "trigger");
        Assert.NotNull(evt);
        Assert.Equal(ClaimLifecycle.Defended, evt!.NewStatus);
    }

    [Fact]
    public void ParseOutcome_Conceded_ReturnsConceded()
    {
        var id = Guid.NewGuid();
        var json = """{"claim_id": "abc", "outcome": "Conceded"}""";
        var evt = ClaimSurvivalAnalyzer.ParseOutcome(json, id, 1, "t");
        Assert.NotNull(evt);
        Assert.Equal(ClaimLifecycle.Conceded, evt!.NewStatus);
    }

    [Fact]
    public void ParseOutcome_MarkdownWrapped_ParsesCorrectly()
    {
        var id = Guid.NewGuid();
        var json = "```json\n{\"claim_id\": \"abc\", \"outcome\": \"Refuted\"}\n```";
        var evt = ClaimSurvivalAnalyzer.ParseOutcome(json, id, 1, "t");
        Assert.NotNull(evt);
        Assert.Equal(ClaimLifecycle.Refuted, evt!.NewStatus);
    }

    [Fact]
    public void ParseOutcome_MalformedJson_ReturnsNull()
    {
        var evt = ClaimSurvivalAnalyzer.ParseOutcome("bad json", Guid.NewGuid(), 1, "t");
        Assert.Null(evt);
    }

    [Fact]
    public void ComputeSurvivalStats_BasicFixture_CorrectStats()
    {
        var idA1 = Guid.NewGuid();
        var idA2 = Guid.NewGuid();
        var idB1 = Guid.NewGuid();

        var nodes = new List<ArgumentNode>
        {
            MakeNode(idA1, "BotA", 1, "Claim A1"),
            MakeNode(idA2, "BotA", 2, "Claim A2"),
            MakeNode(idB1, "BotB", 1, "Claim B1"),
        };

        var events = new List<ClaimLifecycleEvent>
        {
            new(idA1, 1, ClaimLifecycle.Refuted, "counter"),
            new(idB1, 1, ClaimLifecycle.Defended, "defended"),
        };

        var stats = ClaimSurvivalAnalyzer.ComputeSurvivalStats(nodes, events);
        var botA = stats.First(s => s.BotName == "BotA");
        var botB = stats.First(s => s.BotName == "BotB");

        Assert.Equal(2, botA.TotalClaims);
        Assert.Equal(1, botA.RefutedClaims);
        Assert.Equal("BotB", botB.BotName);
    }

    [Fact]
    public void ComputeSurvivalStats_ZeroClaims_NoException()
    {
        // Empty nodes should produce empty stats
        var stats = ClaimSurvivalAnalyzer.ComputeSurvivalStats([], []);
        Assert.Empty(stats);
    }

    [Fact]
    public void ComputeSurvivalStats_NoOpponentClaims_ZeroKillRate()
    {
        var idA1 = Guid.NewGuid();
        var nodes = new List<ArgumentNode> { MakeNode(idA1, "BotA", 1, "Claim") };
        var stats = ClaimSurvivalAnalyzer.ComputeSurvivalStats(nodes, []);
        Assert.Equal(0.0, stats[0].KillRate, precision: 5);
    }

    [Fact]
    public void RenderAutopsy_WithDeadAndSurvivorClaims_ContainsExpectedSections()
    {
        var idA1 = Guid.NewGuid();
        var idA2 = Guid.NewGuid();
        var nodes = new List<ArgumentNode>
        {
            MakeNode(idA1, "BotA", 1, "Refuted claim text here"),
            MakeNode(idA2, "BotA", 2, "Surviving claim text"),
        };
        var events = new List<ClaimLifecycleEvent>
        {
            new(idA1, 1, ClaimLifecycle.Refuted, "strong counter"),
        };
        var result = ClaimSurvivalAnalyzer.RenderAutopsy(nodes, events);
        Assert.Contains("Argument Graveyard", result);
        Assert.Contains("Survivors", result);
        Assert.Contains("Refuted", result);
        Assert.Contains("Survived", result);
    }

    [Fact]
    public void RenderAutopsy_NoClaims_ShowsEmptyMessages()
    {
        var result = ClaimSurvivalAnalyzer.RenderAutopsy([], []);
        Assert.Contains("no claims were refuted or conceded", result);
        Assert.Contains("no claims survived unchallenged", result);
    }
}

// ── CounterfactualReplayService Tests ────────────────────────────────────────

public sealed class CounterfactualReplayServiceTests
{
    [Fact]
    public void BuildComparisonReport_SameWinner_DifferentFalse()
    {
        var orig = Guid.NewGuid();
        var replay = Guid.NewGuid();
        var rounds = new List<CounterfactualRoundResult>
        {
            new(1, "BotA", "BotA"),
            new(2, "BotB", "BotB"),
        };
        var report = CounterfactualReplayService.BuildComparisonReport(orig, replay, rounds, "BotA", "BotA");
        Assert.False(report.DifferentOverallWinner);
        Assert.Equal("BotA", report.OriginalOverallWinner);
        Assert.Equal("BotA", report.ReplayOverallWinner);
    }

    [Fact]
    public void BuildComparisonReport_DifferentWinner_DifferentTrue()
    {
        var orig = Guid.NewGuid();
        var replay = Guid.NewGuid();
        var report = CounterfactualReplayService.BuildComparisonReport(orig, replay, [], "BotA", "BotB");
        Assert.True(report.DifferentOverallWinner);
    }

    [Fact]
    public void RenderComparisonReport_ContainsExpectedSections()
    {
        var orig = Guid.NewGuid();
        var replay = Guid.NewGuid();
        var rounds = new List<CounterfactualRoundResult>
        {
            new(1, "BotA", "BotB"),
        };
        var report = CounterfactualReplayService.BuildComparisonReport(orig, replay, rounds, "BotA", "BotB");
        var rendered = CounterfactualReplayService.RenderComparisonReport(report);
        Assert.Contains("Counterfactual Comparison Report", rendered);
        Assert.Contains("Original Session", rendered);
        Assert.Contains("Replay Session", rendered);
        Assert.Contains("Different outcome", rendered);
    }

    [Fact]
    public void RenderComparisonReport_SameWinner_ShowsSameWinnerVerdict()
    {
        var orig = Guid.NewGuid();
        var replay = Guid.NewGuid();
        var report = CounterfactualReplayService.BuildComparisonReport(orig, replay, [], "BotA", "BotA");
        var rendered = CounterfactualReplayService.RenderComparisonReport(report);
        Assert.Contains("Same overall winner", rendered);
    }

    [Fact]
    public void ReconstructDebateHistory_FiltersAndOrdersByRound()
    {
        var entries = new List<MemoryEntry>
        {
            new(Guid.NewGuid(), "BotA", "topic", 2, "assistant", "Round 2 argument", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "BotB", "topic", 1, "assistant", "Round 1 argument", DateTimeOffset.UtcNow, []),
            new(Guid.NewGuid(), "judge", "topic", 1, "system", "Judge comment",   DateTimeOffset.UtcNow, []),
        };
        var messages = CounterfactualReplayService.ReconstructDebateHistory(entries, "BotA", "BotB");
        Assert.Equal(2, messages.Count);
        Assert.Equal("Round 1 argument", messages[0].Content);
        Assert.Equal("Round 2 argument", messages[1].Content);
    }

    [Fact]
    public void ReconstructDebateHistory_BotAIsAssistant()
    {
        var entries = new List<MemoryEntry>
        {
            new(Guid.NewGuid(), "BotA", "topic", 1, "assistant", "BotA content", DateTimeOffset.UtcNow, []),
        };
        var messages = CounterfactualReplayService.ReconstructDebateHistory(entries, "BotA", "BotB");
        Assert.Single(messages);
        Assert.Equal("assistant", messages[0].Role);
    }

    [Fact]
    public void ReconstructDebateHistory_BotBIsUser()
    {
        var entries = new List<MemoryEntry>
        {
            new(Guid.NewGuid(), "BotB", "topic", 1, "assistant", "BotB content", DateTimeOffset.UtcNow, []),
        };
        var messages = CounterfactualReplayService.ReconstructDebateHistory(entries, "BotA", "BotB");
        Assert.Single(messages);
        Assert.Equal("user", messages[0].Role);
    }
}

// ── ObjectiveLibrary / ObjectiveDetectorService Tests ─────────────────────────

public sealed class ObjectiveLibraryTests
{
    [Fact]
    public void GetRandom_NoCategoryFilter_ReturnsObjective()
    {
        var obj = ObjectiveLibrary.GetRandom();
        Assert.NotNull(obj);
        Assert.False(string.IsNullOrWhiteSpace(obj.Text));
    }

    [Fact]
    public void GetRandom_WithCategory_ReturnsMatchingCategory()
    {
        var obj = ObjectiveLibrary.GetRandom(ObjectiveCategory.Rhetorical);
        Assert.Equal(ObjectiveCategory.Rhetorical, obj.Category);
    }

    [Fact]
    public void GetRandom_EachCategory_ReturnsObjective()
    {
        foreach (var cat in Enum.GetValues<ObjectiveCategory>())
        {
            var obj = ObjectiveLibrary.GetRandom(cat);
            Assert.Equal(cat, obj.Category);
        }
    }

    [Fact]
    public void GetAll_ReturnsNonEmptyList()
    {
        var all = ObjectiveLibrary.GetAll();
        Assert.NotEmpty(all);
    }

    [Fact]
    public void FormatInjection_ContainsHiddenDirective()
    {
        var text = "Never use the word 'however'.";
        var result = ObjectiveLibrary.FormatInjection(text);
        Assert.Contains("HIDDEN DIRECTIVE", result);
        Assert.Contains(text, result);
    }

    [Fact]
    public void ParseDetection_ValidJson_ReturnsParsedValues()
    {
        var json = """{"bot_a_detected": "Analogy tactic", "bot_a_score": 8, "bot_b_detected": "Escalation", "bot_b_score": 6}""";
        var result = ObjectiveDetectorService.ParseDetection(json);
        Assert.Equal("Analogy tactic", result.BotADetected);
        Assert.Equal(8, result.BotAScore);
        Assert.Equal("Escalation", result.BotBDetected);
        Assert.Equal(6, result.BotBScore);
    }

    [Fact]
    public void ParseDetection_MarkdownWrapped_Parses()
    {
        var json = "```json\n{\"bot_a_detected\": \"X\", \"bot_a_score\": 5, \"bot_b_detected\": \"Y\", \"bot_b_score\": 3}\n```";
        var result = ObjectiveDetectorService.ParseDetection(json);
        Assert.Equal("X", result.BotADetected);
        Assert.Equal(5, result.BotAScore);
    }

    [Fact]
    public void ParseDetection_MalformedJson_ReturnsFallback()
    {
        var result = ObjectiveDetectorService.ParseDetection("not json");
        Assert.Equal("Unknown", result.BotADetected);
        Assert.Equal(0, result.BotAScore);
        Assert.Equal("Unknown", result.BotBDetected);
        Assert.Equal(0, result.BotBScore);
    }

    [Fact]
    public void ParseDetection_ScoreClampedToTen()
    {
        var json = """{"bot_a_detected": "X", "bot_a_score": 99, "bot_b_detected": "Y", "bot_b_score": -5}""";
        var result = ObjectiveDetectorService.ParseDetection(json);
        Assert.Equal(10, result.BotAScore);
        Assert.Equal(0,  result.BotBScore);
    }
}

// ── Regression: VulnerabilityTracker.RenderScorecard alignment ────────────────

public sealed class VulnerabilityTrackerAlignmentTests
{
    private static VulnerabilityTracker CreateSeededTracker(IReadOnlyList<VulnerabilityRecord> records)
    {
        var tracker = new VulnerabilityTracker();
        var field = typeof(VulnerabilityTracker).GetField("_records",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var list = (List<VulnerabilityRecord>)field.GetValue(tracker)!;
        list.AddRange(records);
        return tracker;
    }

    [Fact]
    public void RenderScorecard_AllLines_HaveConsistentWidth()
    {
        // Seed one record of each status type to exercise all branches.
        var records = new List<VulnerabilityRecord>
        {
            new(Guid.NewGuid(), 1, "LogicGap",          "Short description",                      VulnerabilityStatus.Open),
            new(Guid.NewGuid(), 2, "ImplementationRisk","A very long description that exceeds the column width limit set in the scorecard renderer", VulnerabilityStatus.Patched),
            new(Guid.NewGuid(), 3, "EthicalBlindSpot",  "Medium length description here",         VulnerabilityStatus.Disputed),
            new(Guid.NewGuid(), 4, "AdversarialExploit","Another description",                    VulnerabilityStatus.Accepted),
        };
        var tracker  = CreateSeededTracker(records);
        var scorecard = tracker.RenderScorecard();

        // Every non-empty line must have the same character width.
        var lines = scorecard.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
        var widths = lines.Select(l => l.Length).Distinct().ToArray();
        Assert.Single(widths);
    }
}

// ── Regression: TournamentBracket.RenderBracket winner overflow ────────────────

public sealed class TournamentBracketAlignmentTests
{
    [Fact]
    public void RenderBracket_WithResults_AllLinesHaveConsistentWidth()
    {
        var contestants = new[]
        {
            new TournamentContestant("AlphaBot", "bedrock", "model", "Pragmatist"),
            new TournamentContestant("BetaBot",  "bedrock", "model", "Idealist"),
        };
        var match   = new TournamentMatch(1, contestants[0], contestants[1]);
        var results = new[] { new TournamentResult(match, contestants[0], contestants[1], 10, 5) };

        var rendered = TournamentBracket.RenderBracket(contestants, results);
        var lines = rendered.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
        var widths = lines.Select(l => l.Length).Distinct().ToArray();
        Assert.Single(widths);
    }

    [Fact]
    public void RenderBracket_LongWinnerName_DoesNotOverflowBox()
    {
        var contestants = new[]
        {
            new TournamentContestant("ShortBot", "bedrock", "model", "Pragmatist"),
            new TournamentContestant("OpponentX", "bedrock", "model", "Idealist"),
        };
        var match   = new TournamentMatch(1, contestants[0], contestants[1]);
        // Winner name is long enough that " → WinnerName" would overflow 8 chars.
        var results = new[] { new TournamentResult(match, contestants[0], contestants[1], 10, 5) };

        var rendered = TournamentBracket.RenderBracket(contestants, results);
        var lines = rendered.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
        var widths = lines.Select(l => l.Length).Distinct().ToArray();
        Assert.Single(widths);
    }
}
