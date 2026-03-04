using Xunit;
using VibeWars.Reflection;
using VibeWars.Arbiter;
using VibeWars.Memory;
using VibeWars.Tournament;
using VibeWars.Analytics;
using VibeWars.StanceTracker;
using VibeWars.Models;
using VibeWars.Elo;

namespace VibeWars.Tests;

// ─── Feature 6: SelfReflectionService ────────────────────────────────────────

public class SelfReflectionServiceTests
{
    [Fact]
    public void ParseReflection_ValidJson_ParsesAllFields()
    {
        var json = """{"strongest_point": "A is clear", "unaddressed_weakness": "missed B", "next_round_improvement": "address B"}""";
        var entry = SelfReflectionService.ParseReflection(json, "BotX", 2);
        Assert.Equal("BotX", entry.BotName);
        Assert.Equal(2, entry.Round);
        Assert.Equal("A is clear", entry.StrongestPoint);
        Assert.Equal("missed B", entry.WeakestResponse);
        Assert.Equal("address B", entry.PlannedImprovement);
    }

    [Fact]
    public void ParseReflection_MarkdownWrappedJson_ParsesCorrectly()
    {
        var json = "```json\n{\"strongest_point\": \"X\", \"unaddressed_weakness\": \"Y\", \"next_round_improvement\": \"Z\"}\n```";
        var entry = SelfReflectionService.ParseReflection(json, "Bot", 1);
        Assert.Equal("X", entry.StrongestPoint);
        Assert.Equal("Y", entry.WeakestResponse);
        Assert.Equal("Z", entry.PlannedImprovement);
    }

    [Fact]
    public void ParseReflection_MalformedJson_FallsBackToEmpty()
    {
        var entry = SelfReflectionService.ParseReflection("not json", "Bot", 1);
        Assert.Equal("", entry.StrongestPoint);
        Assert.Equal("", entry.WeakestResponse);
        Assert.Equal("", entry.PlannedImprovement);
    }

    [Fact]
    public void FormatReflectionInjection_NonEmptyImprovement_ReturnsInjectionString()
    {
        var entry = new SelfReflectionEntry("Bot", 1, "strong", "weak", "improve next");
        var result = SelfReflectionService.FormatReflectionInjection(entry);
        Assert.Contains("[REFLECTION]", result);
        Assert.Contains("improve next", result);
    }

    [Fact]
    public void FormatReflectionInjection_EmptyImprovement_ReturnsEmpty()
    {
        var entry = new SelfReflectionEntry("Bot", 1, "strong", "weak", "");
        var result = SelfReflectionService.FormatReflectionInjection(entry);
        Assert.Equal("", result);
    }

    [Fact]
    public void CalculateReflectionQualityBonus_HighQuality_ReturnsPositive()
    {
        var entry = new SelfReflectionEntry("Bot", 1, "s", "w", "i");
        Assert.Equal(0.2, SelfReflectionService.CalculateReflectionQualityBonus(entry, true));
    }

    [Fact]
    public void CalculateReflectionQualityBonus_LowQuality_ReturnsNegative()
    {
        var entry = new SelfReflectionEntry("Bot", 1, "s", "w", "i");
        Assert.Equal(-0.1, SelfReflectionService.CalculateReflectionQualityBonus(entry, false));
    }

    [Fact]
    public void CalculateIntellectualProgressScore_WithReflectionBonus_AddsBonus()
    {
        var botA = new StanceTimeline("A");
        botA.Add(new StanceEntry(1, 2, []));
        botA.Add(new StanceEntry(2, 4, ["concession"]));

        var botB = new StanceTimeline("B");
        botB.Add(new StanceEntry(1, -3, []));
        botB.Add(new StanceEntry(2, -1, []));

        var baseScore   = StanceMeterService.CalculateIntellectualProgressScore(botA, botB, 2);
        var bonusScore  = StanceMeterService.CalculateIntellectualProgressScore(botA, botB, 2, 0.2);
        Assert.Equal(baseScore + 0.2, bonusScore, 10);
    }
}

// ─── Feature 7: DialecticalArbiter ───────────────────────────────────────────

public class DialecticalArbiterTests
{
    [Fact]
    public void ParseSynthesis_ValidJson_ParsesAllFields()
    {
        var json = """{"thesis": "T", "antithesis": "AT", "synthesis": "SY", "open_questions": ["Q1", "Q2"]}""";
        var result = DialecticalArbiter.ParseSynthesis(json, "fallback");
        Assert.Equal("T",  result.CoreThesis);
        Assert.Equal("AT", result.CoreAntithesis);
        Assert.Equal("SY", result.Synthesis);
        Assert.Equal(2,    result.OpenQuestions.Length);
        Assert.Contains("Q1", result.OpenQuestions);
        Assert.Contains("Q2", result.OpenQuestions);
    }

    [Fact]
    public void ParseSynthesis_MarkdownWrapped_ParsesCorrectly()
    {
        var json = "```json\n{\"thesis\": \"T\", \"antithesis\": \"AT\", \"synthesis\": \"SY\", \"open_questions\": []}\n```";
        var result = DialecticalArbiter.ParseSynthesis(json, "fallback");
        Assert.Equal("T",  result.CoreThesis);
        Assert.Equal("SY", result.Synthesis);
    }

    [Fact]
    public void ParseSynthesis_MalformedJson_FallsBackToFallback()
    {
        var result = DialecticalArbiter.ParseSynthesis("broken", "fallback");
        Assert.Equal("fallback", result.Synthesis);
        Assert.Equal("", result.CoreThesis);
    }

    [Fact]
    public void ParseSynthesis_EmptyOpenQuestions_ReturnsEmptyArray()
    {
        var json = """{"thesis": "T", "antithesis": "AT", "synthesis": "SY", "open_questions": []}""";
        var result = DialecticalArbiter.ParseSynthesis(json);
        Assert.Empty(result.OpenQuestions);
    }

    [Fact]
    public void ParseSynthesis_MultipleOpenQuestions_AllParsed()
    {
        var json = """{"thesis": "T", "antithesis": "AT", "synthesis": "SY", "open_questions": ["Q1", "Q2", "Q3"]}""";
        var result = DialecticalArbiter.ParseSynthesis(json);
        Assert.Equal(3, result.OpenQuestions.Length);
    }

    [Fact]
    public void RenderSynthesis_ContainsDialecticalSynthesisHeader()
    {
        var synthesis = new ArbiterSynthesis("T", "AT", "SY", ["Q1"]);
        var rendered  = DialecticalArbiter.RenderSynthesis(synthesis);
        Assert.Contains("Dialectical Synthesis", rendered);
    }
}

// ─── Feature 8: AdversarialBriefingService ───────────────────────────────────

public class AdversarialBriefingServiceTests
{
    private static MemoryEntry MakeEntry(string botName, string role, string content, string topic)
        => new(Guid.NewGuid(), botName, topic, 1, role, content, DateTimeOffset.UtcNow, []);

    [Fact]
    public void ShouldBrief_LessThan3Entries_ReturnsFalse()
    {
        var entries = new List<MemoryEntry>
        {
            MakeEntry("Bot A", "assistant", "arg1", "AI"),
            MakeEntry("Bot B", "assistant", "arg2", "AI"),
        };
        Assert.False(AdversarialBriefingService.ShouldBrief(entries, "AI"));
    }

    [Fact]
    public void ShouldBrief_ThreeOrMoreMatchingEntries_ReturnsTrue()
    {
        var entries = new List<MemoryEntry>
        {
            MakeEntry("Bot A", "assistant", "arg1", "AI"),
            MakeEntry("Bot B", "assistant", "arg2", "AI"),
            MakeEntry("Bot A", "assistant", "arg3", "AI"),
        };
        Assert.True(AdversarialBriefingService.ShouldBrief(entries, "AI"));
    }

    [Fact]
    public void FormatBriefingNotice_ContainsBotNameAndCount()
    {
        var notice = AdversarialBriefingService.FormatBriefingNotice("GPT-4", 7);
        Assert.Contains("GPT-4", notice);
        Assert.Contains("7", notice);
    }
}

// ─── Feature 9: SwissTournament ──────────────────────────────────────────────

public class SwissTournamentTests
{
    private static TournamentContestant C(string name) => new(name, "", "", "");

    [Fact]
    public void TotalRounds_4Contestants_Returns3()
    {
        Assert.Equal(3, SwissTournament.TotalRounds(4));
    }

    [Fact]
    public void TotalRounds_8Contestants_Returns4()
    {
        Assert.Equal(4, SwissTournament.TotalRounds(8));
    }

    [Fact]
    public void TotalRounds_9Contestants_Returns5()
    {
        Assert.Equal(5, SwissTournament.TotalRounds(9));
    }

    [Fact]
    public void GenerateSwissPairings_4Contestants_Produces2Matches()
    {
        var contestants = new[] { C("A"), C("B"), C("C"), C("D") };
        var pairings = SwissTournament.GenerateSwissPairings(contestants, [], 1);
        Assert.Equal(2, pairings.Count);
    }

    [Fact]
    public void GenerateSwissPairings_8Contestants_Produces4Matches()
    {
        var contestants = Enumerable.Range(1, 8).Select(i => C($"Bot{i}")).ToArray();
        var pairings = SwissTournament.GenerateSwissPairings(contestants, [], 1);
        Assert.Equal(4, pairings.Count);
    }

    [Fact]
    public void GenerateSwissPairings_AvoidsRematches()
    {
        var a = C("A"); var b = C("B"); var c = C("C"); var d = C("D");
        var contestants = new[] { a, b, c, d };
        // Round 1: A vs B, C vs D
        var match1 = new TournamentMatch(1, a, b);
        var match2 = new TournamentMatch(2, c, d);
        var pastResults = new List<TournamentResult>
        {
            new(match1, a, b, 10, 5),
            new(match2, c, d, 10, 5),
        };
        var round2Pairings = SwissTournament.GenerateSwissPairings(contestants, pastResults, 2);
        // Should not rematch A vs B or C vs D
        foreach (var m in round2Pairings)
        {
            var pair = (m.ContestantA.Name, m.ContestantB.Name);
            Assert.False(pair == ("A", "B") || pair == ("B", "A"));
            Assert.False(pair == ("C", "D") || pair == ("D", "C"));
        }
    }

    [Fact]
    public void ComputeStandings_WithResults_CorrectOrder()
    {
        var a = C("A"); var b = C("B"); var c = C("C"); var d = C("D");
        var contestants = new[] { a, b, c, d };
        var results = new List<TournamentResult>
        {
            new(new TournamentMatch(1, a, b), a, b, 10, 5),
            new(new TournamentMatch(2, c, d), c, d, 10, 5),
        };
        var standings = SwissTournament.ComputeStandings(contestants, results);
        // Winners (A and C) should have more points than losers
        var winnerPoints = standings.Where(s => s.Contestant.Name is "A" or "C").Select(s => s.Points);
        var loserPoints  = standings.Where(s => s.Contestant.Name is "B" or "D").Select(s => s.Points);
        Assert.All(winnerPoints, p => Assert.Equal(3, p));
        Assert.All(loserPoints,  p => Assert.Equal(0, p));
    }

    [Fact]
    public void RenderSwissStandings_OutputIsNonEmpty()
    {
        var a = C("Alpha"); var b = C("Beta");
        var standings = SwissTournament.ComputeStandings([a, b],
            [new(new TournamentMatch(1, a, b), a, b, 10, 5)]);
        var rendered = SwissTournament.RenderSwissStandings(standings);
        Assert.NotEmpty(rendered);
        Assert.Contains("Alpha", rendered);
    }
}

// ─── Feature 9: RoundRobinTournament ─────────────────────────────────────────

public class RoundRobinTournamentTests
{
    private static TournamentContestant C(string name) => new(name, "", "", "");

    [Fact]
    public void GenerateSchedule_4Contestants_EachPlaysEveryOtherExactlyOnce()
    {
        var contestants = new[] { C("A"), C("B"), C("C"), C("D") };
        var schedule    = RoundRobinTournament.GenerateSchedule(contestants);
        var allMatches  = schedule.SelectMany(r => r).ToList();

        // 4 contestants → C(4,2) = 6 unique matchups
        Assert.Equal(6, allMatches.Count);

        // Check every pair plays exactly once
        for (var i = 0; i < contestants.Length; i++)
            for (var j = i + 1; j < contestants.Length; j++)
            {
                var name1 = contestants[i].Name;
                var name2 = contestants[j].Name;
                var count = allMatches.Count(m =>
                    (m.ContestantA.Name == name1 && m.ContestantB.Name == name2) ||
                    (m.ContestantA.Name == name2 && m.ContestantB.Name == name1));
                Assert.Equal(1, count);
            }
    }

    [Fact]
    public void GenerateSchedule_3Contestants_OddByePadding_ScheduleNonEmpty()
    {
        var contestants = new[] { C("A"), C("B"), C("C") };
        var schedule    = RoundRobinTournament.GenerateSchedule(contestants);
        Assert.NotEmpty(schedule);
        Assert.All(schedule, round => Assert.NotEmpty(round));
    }

    [Fact]
    public void RenderResultsMatrix_ProducesNxNTable()
    {
        var contestants = new[] { C("A"), C("B"), C("C") };
        var match = new TournamentMatch(1, contestants[0], contestants[1]);
        var results = new List<TournamentResult>
        {
            new(match, contestants[0], contestants[1], 10, 5)
        };
        var matrix = RoundRobinTournament.RenderResultsMatrix(contestants, results);
        Assert.Contains("A", matrix);
        Assert.Contains("B", matrix);
        Assert.Contains("W", matrix);
        Assert.Contains("L", matrix);
    }
}

// ─── Feature 10: ArgumentStrengthScorer ──────────────────────────────────────

public class ArgumentStrengthScorerTests
{
    [Fact]
    public void ParseScore_ValidJson_ComputesCorrectComposite()
    {
        var json = """{"logical_rigor": 8.0, "novelty": 6.0, "persuasive_impact": 7.0}""";
        var score = ArgumentStrengthScorer.ParseScore(json, 1, "BotA");
        Assert.Equal(8.0, score.LogicalRigor);
        Assert.Equal(6.0, score.Novelty);
        Assert.Equal(7.0, score.PersuasiveImpact);
        // composite = 0.4*8 + 0.3*6 + 0.3*7 = 3.2 + 1.8 + 2.1 = 7.1
        Assert.Equal(7.1, score.Composite, 5);
    }

    [Fact]
    public void ParseScore_MarkdownWrapped_ParsesCorrectly()
    {
        var json = "```json\n{\"logical_rigor\": 5, \"novelty\": 5, \"persuasive_impact\": 5}\n```";
        var score = ArgumentStrengthScorer.ParseScore(json, 1, "Bot");
        Assert.Equal(5.0, score.LogicalRigor);
        Assert.Equal(5.0, score.Composite, 5);
    }

    [Fact]
    public void ParseScore_MalformedJson_FallsBackToAll5()
    {
        var score = ArgumentStrengthScorer.ParseScore("not json", 1, "Bot");
        Assert.Equal(5.0, score.LogicalRigor);
        Assert.Equal(5.0, score.Novelty);
        Assert.Equal(5.0, score.PersuasiveImpact);
        Assert.Equal(5.0, score.Composite);
    }

    [Fact]
    public void ParseScore_ClampsValuesToRange()
    {
        var json = """{"logical_rigor": 15.0, "novelty": -3.0, "persuasive_impact": 5.0}""";
        var score = ArgumentStrengthScorer.ParseScore(json, 1, "Bot");
        Assert.Equal(10.0, score.LogicalRigor);
        Assert.Equal(0.0,  score.Novelty);
    }

    [Fact]
    public void ComputeComposite_CorrectWeights()
    {
        var composite = ArgumentStrengthScore.ComputeComposite(10.0, 10.0, 10.0);
        Assert.Equal(10.0, composite, 10);

        var composite2 = ArgumentStrengthScore.ComputeComposite(0.0, 0.0, 0.0);
        Assert.Equal(0.0, composite2, 10);

        // 0.4*4 + 0.3*6 + 0.3*8 = 1.6 + 1.8 + 2.4 = 5.8
        var composite3 = ArgumentStrengthScore.ComputeComposite(4.0, 6.0, 8.0);
        Assert.Equal(5.8, composite3, 5);
    }
}

// ─── Feature 10: HeatmapRenderer ─────────────────────────────────────────────

public class HeatmapRendererTests
{
    [Theory]
    [InlineData(0.0,  '░')]
    [InlineData(2.4,  '░')]
    [InlineData(2.5,  '▒')]
    [InlineData(4.9,  '▒')]
    [InlineData(5.0,  '▓')]
    [InlineData(7.4,  '▓')]
    [InlineData(7.5,  '█')]
    [InlineData(10.0, '█')]
    public void BlockChar_ReturnsCorrectCharacterForQuartile(double composite, char expected)
    {
        Assert.Equal(expected, HeatmapRenderer.BlockChar(composite));
    }

    [Fact]
    public void GetTrendLabel_AscendingSeries_ReturnsAscending()
    {
        var scores = new[]
        {
            new ArgumentStrengthScore(1, "Bot", 4, 4, 4, 4.0),
            new ArgumentStrengthScore(2, "Bot", 6, 6, 6, 6.0),
            new ArgumentStrengthScore(3, "Bot", 8, 8, 8, 8.0),
        };
        Assert.Equal("▲ ascending", HeatmapRenderer.GetTrendLabel(scores));
    }

    [Fact]
    public void GetTrendLabel_DecliningSeries_ReturnsDeclining()
    {
        var scores = new[]
        {
            new ArgumentStrengthScore(1, "Bot", 9, 9, 9, 9.0),
            new ArgumentStrengthScore(2, "Bot", 6, 6, 6, 6.0),
            new ArgumentStrengthScore(3, "Bot", 3, 3, 3, 3.0),
        };
        Assert.Equal("▼ declining", HeatmapRenderer.GetTrendLabel(scores));
    }

    [Fact]
    public void GetTrendLabel_FlatSeries_ReturnsStable()
    {
        var scores = new[]
        {
            new ArgumentStrengthScore(1, "Bot", 5, 5, 5, 5.0),
            new ArgumentStrengthScore(2, "Bot", 5, 5, 5, 5.0),
            new ArgumentStrengthScore(3, "Bot", 5, 5, 5, 5.0),
        };
        Assert.Equal("─ stable", HeatmapRenderer.GetTrendLabel(scores));
    }

    [Fact]
    public void GetTrendLabel_SingleItem_ReturnsStable()
    {
        var scores = new[] { new ArgumentStrengthScore(1, "Bot", 5, 5, 5, 5.0) };
        Assert.Equal("─ stable", HeatmapRenderer.GetTrendLabel(scores));
    }

    [Fact]
    public void RenderHeatmap_OneRound_ContainsExpectedBlocks()
    {
        var scores = new[] { new ArgumentStrengthScore(1, "BotA", 8, 8, 8, 8.0) };
        var rendered = HeatmapRenderer.RenderHeatmap(scores);
        Assert.Contains("BotA", rendered);
        Assert.Contains("R1:", rendered);
        Assert.Contains('█', rendered);
    }

    [Fact]
    public void RenderHeatmap_FiveRounds_ContainsAllRounds()
    {
        var scores = Enumerable.Range(1, 5)
            .Select(i => new ArgumentStrengthScore(i, "BotA", 5, 5, 5, 5.0))
            .ToArray();
        var rendered = HeatmapRenderer.RenderHeatmap(scores);
        for (var i = 1; i <= 5; i++)
            Assert.Contains($"R{i}:", rendered);
    }

    [Fact]
    public void ComputeSlope_KnownValues_ReturnsCorrectSlope()
    {
        // y = x: slope should be 1.0
        var values = new List<double> { 1.0, 2.0, 3.0, 4.0, 5.0 };
        var slope  = HeatmapRenderer.ComputeSlope(values);
        Assert.Equal(1.0, slope, 5);
    }

    [Fact]
    public void EloKFactorMultiplier_HighQualityWin_AppliesMultiplier()
    {
        // Standard K=32, multiplier=1.1 → K=35
        var deltaStandard   = EloService.ComputeEloDelta(1200, 1200, 1.0, 32);
        var deltaMultiplied = EloService.ComputeEloDelta(1200, 1200, 1.0, (int)Math.Round(32 * 1.1));
        Assert.True(deltaMultiplied > deltaStandard);
    }
}

// ─── Feature 9: TournamentFormat enum ────────────────────────────────────────

public class TournamentFormatTests
{
    [Fact]
    public void TournamentFormat_AllValuesExist()
    {
        Assert.Equal(0, (int)TournamentFormat.SingleElimination);
        Assert.Equal(1, (int)TournamentFormat.Swiss);
        Assert.Equal(2, (int)TournamentFormat.RoundRobin);
    }

    [Fact]
    public void TournamentFormat_ParseFromString()
    {
        var parsed = Enum.Parse<TournamentFormat>("Swiss");
        Assert.Equal(TournamentFormat.Swiss, parsed);
    }
}
