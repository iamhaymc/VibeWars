using VibeWars.Config;
using VibeWars.Models;
using VibeWars.Personas;

namespace VibeWars.Tests;

// ──────────────────────────────────────────────────────────────────────────────
// PersonaLibraryTests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class PersonaLibraryTests
{
    [Theory]
    [InlineData("Pragmatist",   PersonaArchetype.Pragmatist)]
    [InlineData("Idealist",     PersonaArchetype.Idealist)]
    [InlineData("Devil's Advocate", PersonaArchetype.DevilsAdvocate)]
    [InlineData("Domain Expert",    PersonaArchetype.DomainExpert)]
    [InlineData("Empiricist",   PersonaArchetype.Empiricist)]
    [InlineData("Ethicist",     PersonaArchetype.Ethicist)]
    [InlineData("Contrarian",   PersonaArchetype.Custom)]
    [InlineData("Synthesizer",  PersonaArchetype.Custom)]
    public void Resolve_KnownPersona_ReturnsCorrectArchetype(string name, PersonaArchetype expected)
    {
        var persona = PersonaLibrary.Resolve(name);
        Assert.Equal(expected, persona.Archetype);
        Assert.Equal(name, persona.Name);
    }

    [Theory]
    [InlineData("PRAGMATIST")]
    [InlineData("pragmatist")]
    [InlineData("PrAgMaTiSt")]
    public void Resolve_CaseInsensitive_ReturnsMatch(string name)
    {
        var persona = PersonaLibrary.Resolve(name);
        Assert.Equal(PersonaArchetype.Pragmatist, persona.Archetype);
    }

    [Fact]
    public void Resolve_UnknownName_ReturnsCustomWithOriginalName()
    {
        var persona = PersonaLibrary.Resolve("Philosopher");
        Assert.Equal(PersonaArchetype.Custom, persona.Archetype);
        Assert.Equal("Philosopher", persona.Name);
    }

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsCustom()
    {
        var p1 = PersonaLibrary.Resolve(string.Empty);
        var p2 = PersonaLibrary.Resolve("   ");
        Assert.Equal(PersonaArchetype.Custom, p1.Archetype);
        Assert.Equal(PersonaArchetype.Custom, p2.Archetype);
    }

    [Fact]
    public void ListAll_ReturnsAtLeastEightPersonas()
    {
        var all = PersonaLibrary.ListAll();
        Assert.True(all.Count >= 8);
    }

    [Fact]
    public void ListAll_ContainsAllArchetypesExceptCustom()
    {
        var all = PersonaLibrary.ListAll();
        var archetypes = all.Select(p => p.Archetype).ToHashSet();
        Assert.Contains(PersonaArchetype.Pragmatist, archetypes);
        Assert.Contains(PersonaArchetype.Idealist, archetypes);
        Assert.Contains(PersonaArchetype.DevilsAdvocate, archetypes);
        Assert.Contains(PersonaArchetype.DomainExpert, archetypes);
        Assert.Contains(PersonaArchetype.Empiricist, archetypes);
        Assert.Contains(PersonaArchetype.Ethicist, archetypes);
    }

    [Fact]
    public void AllPersonas_HaveNonEmptyFields()
    {
        foreach (var p in PersonaLibrary.ListAll())
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name), $"Persona name is empty");
            Assert.False(string.IsNullOrWhiteSpace(p.StyleDescription), $"StyleDescription empty for {p.Name}");
            Assert.False(string.IsNullOrWhiteSpace(p.StrengthBias), $"StrengthBias empty for {p.Name}");
            Assert.False(string.IsNullOrWhiteSpace(p.WeaknessBias), $"WeaknessBias empty for {p.Name}");
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// DebateFormatTests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class DebateFormatTests
{
    [Fact]
    public void GetTurnInstruction_Structured_Round1_ContainsOpeningClaim()
    {
        var instruction = DebateFormatHelper.GetTurnInstruction(DebateFormat.Structured, 1, 3, false);
        Assert.Contains("opening claim", instruction);
        Assert.Contains("evidence", instruction);
        Assert.Contains("warrant", instruction);
    }

    [Fact]
    public void GetTurnInstruction_Structured_FinalRound_ContainsSynthesize()
    {
        var instruction = DebateFormatHelper.GetTurnInstruction(DebateFormat.Structured, 3, 3, true);
        Assert.Contains("Synthesize", instruction);
        Assert.Contains("agree", instruction);
    }

    [Fact]
    public void GetTurnInstruction_Structured_MiddleRound_ContainsSteelman()
    {
        var instruction = DebateFormatHelper.GetTurnInstruction(DebateFormat.Structured, 2, 3, false);
        Assert.Contains("steelman", instruction.ToLowerInvariant());
        Assert.Contains("rebuttal", instruction.ToLowerInvariant());
    }

    [Fact]
    public void GetTurnInstruction_Freeform_ReturnsEmpty()
    {
        var instruction = DebateFormatHelper.GetTurnInstruction(DebateFormat.Freeform, 1, 3, false);
        Assert.Equal(string.Empty, instruction);
    }

    [Fact]
    public void GetFormatSystemNote_Oxford_ContainsOxford()
    {
        var note = DebateFormatHelper.GetFormatSystemNote(DebateFormat.Oxford);
        Assert.Contains("Oxford", note);
    }

    [Fact]
    public void GetFormatSystemNote_Freeform_ReturnsEmpty()
    {
        var note = DebateFormatHelper.GetFormatSystemNote(DebateFormat.Freeform);
        Assert.Equal(string.Empty, note);
    }

    [Theory]
    [InlineData("freeform",     DebateFormat.Freeform)]
    [InlineData("structured",   DebateFormat.Structured)]
    [InlineData("oxford",       DebateFormat.Oxford)]
    [InlineData("SOCRATIC",     DebateFormat.Socratic)]
    [InlineData("Collaborative",DebateFormat.Collaborative)]
    [InlineData("unknown",      DebateFormat.Freeform)]
    [InlineData(null,           DebateFormat.Freeform)]
    public void Parse_VariousInputs_ReturnsExpected(string? input, DebateFormat expected)
    {
        Assert.Equal(expected, DebateFormatHelper.Parse(input));
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// CostAccumulatorTests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CostAccumulatorTests
{
    [Fact]
    public void Add_AccumulatesTokens()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(100, 50, 150, null));
        acc.Add(new TokenUsage(200, 80, 280, null));

        Assert.Equal(300, acc.TotalPromptTokens);
        Assert.Equal(130, acc.TotalCompletionTokens);
        Assert.Equal(430, acc.TotalTokens);
        Assert.Null(acc.TotalEstimatedCostUsd);
    }

    [Fact]
    public void Add_AccumulatesCost()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(100, 50, 150, 0.01m));
        acc.Add(new TokenUsage(200, 80, 280, 0.02m));

        Assert.Equal(0.03m, acc.TotalEstimatedCostUsd);
    }

    [Fact]
    public void Add_MixedCostAndNull_AccumulatesOnlyPresent()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(100, 50, 150, 0.01m));
        acc.Add(new TokenUsage(200, 80, 280, null));  // no cost

        Assert.Equal(0.01m, acc.TotalEstimatedCostUsd);
    }

    [Fact]
    public void ExceedsBudget_UnderBudget_ReturnsFalse()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(0, 0, 0, 0.10m));

        Assert.False(acc.ExceedsBudget(0.50m));
    }

    [Fact]
    public void ExceedsBudget_AtBudget_ReturnsTrue()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(0, 0, 0, 0.50m));

        Assert.True(acc.ExceedsBudget(0.50m));
    }

    [Fact]
    public void ExceedsBudget_OverBudget_ReturnsTrue()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(0, 0, 0, 0.60m));

        Assert.True(acc.ExceedsBudget(0.50m));
    }

    [Fact]
    public void ExceedsBudget_NoBudgetSet_ReturnsFalse()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(0, 0, 0, 99.99m));

        Assert.False(acc.ExceedsBudget(null));
    }

    [Fact]
    public void ExceedsBudget_NoCostTracked_ReturnsFalse()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(100, 50, 150, null)); // no cost

        Assert.False(acc.ExceedsBudget(0.50m));
    }

    [Fact]
    public void FormatSummary_WithCost_IncludesCostAndTokens()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(1000, 500, 1500, 0.0150m));

        var summary = acc.FormatSummary();
        Assert.Contains("$", summary);
        Assert.Contains("1,000", summary);  // prompt tokens formatted with comma
        Assert.Contains("500", summary);
    }

    [Fact]
    public void FormatSummary_NoCost_ShowsTokensOnly()
    {
        var acc = new CostAccumulator();
        acc.Add(new TokenUsage(500, 250, 750, null));

        var summary = acc.FormatSummary();
        Assert.DoesNotContain("$", summary);
        Assert.Contains("tokens", summary);
    }

    [Fact]
    public void TokenUsage_Empty_HasZeroValues()
    {
        Assert.Equal(0, TokenUsage.Empty.PromptTokens);
        Assert.Equal(0, TokenUsage.Empty.CompletionTokens);
        Assert.Equal(0, TokenUsage.Empty.TotalTokens);
        Assert.Null(TokenUsage.Empty.EstimatedCostUsd);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// ConfigLoaderTests
// ──────────────────────────────────────────────────────────────────────────────

public sealed class ConfigLoaderTests
{
    [Fact]
    public void Load_NoArgsNoFile_ReturnsDefaults()
    {
        // Use a config path that doesn't exist
        var args = new[] { "--config", "/tmp/nonexistent_vibewars_config_xyz.yml" };
        var config = ConfigLoader.Load(args);

        Assert.Equal(3, config.MaxRounds);
        Assert.Equal("freeform", config.DebateFormat);
        Assert.Equal("sqlite", config.MemoryBackend);
        Assert.Null(config.BotBProvider);
        Assert.Equal("us-east-1", config.AwsRegion);
        Assert.False(config.NoMemory);
    }

    [Fact]
    public void Load_NoMemoryFlag_SetsNoMemoryTrue()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--no-memory" };
        var config = ConfigLoader.Load(args);
        Assert.True(config.NoMemory);
    }

    [Fact]
    public void Load_PersonaAFlag_SetsBotAPersona()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--persona-a", "Pragmatist" };
        var config = ConfigLoader.Load(args);
        Assert.Equal("Pragmatist", config.BotAPersona);
    }

    [Fact]
    public void Load_PersonaBFlag_SetsBotBPersona()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--persona-b", "Idealist" };
        var config = ConfigLoader.Load(args);
        Assert.Equal("Idealist", config.BotBPersona);
    }

    [Fact]
    public void Load_FormatFlag_SetsDebateFormat()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--format", "oxford" };
        var config = ConfigLoader.Load(args);
        Assert.Equal("oxford", config.DebateFormat);
    }

    [Fact]
    public void Load_NoStreamFlag_SetsNoStreamTrue()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--no-stream" };
        var config = ConfigLoader.Load(args);
        Assert.True(config.NoStream);
    }

    [Fact]
    public void Load_FactCheckFlag_SetsFactCheckTrue()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--fact-check" };
        var config = ConfigLoader.Load(args);
        Assert.True(config.FactCheck);
    }

    [Fact]
    public void Load_CostHardStopFlag_SetsCostHardStopTrue()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--cost-hard-stop" };
        var config = ConfigLoader.Load(args);
        Assert.True(config.CostHardStop);
    }

    [Fact]
    public void GetConfigPath_WithConfigFlag_ReturnsProvidedPath()
    {
        var args = new[] { "--config", "/some/path/config.yml" };
        Assert.Equal("/some/path/config.yml", ConfigLoader.GetConfigPath(args));
    }

    [Fact]
    public void GetConfigPath_NoFlag_ReturnsDefaultPath()
    {
        var args = Array.Empty<string>();
        var path = ConfigLoader.GetConfigPath(args);
        Assert.Contains(".vibewars", path);
        Assert.Contains("config.yml", path);
    }

    [Fact]
    public void GenerateStarterConfig_ContainsExpectedSections()
    {
        var yaml = ConfigLoader.GenerateStarterConfig();
        Assert.Contains("botAModel", yaml);
        Assert.Contains("maxRounds", yaml);
        Assert.Contains("memoryBackend", yaml);
        Assert.Contains("botAPersona", yaml);
        Assert.Contains("maxCostUsd", yaml);
    }

    [Fact]
    public void Load_YamlConfigFile_ParsesValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vibewars_cfg_{Guid.NewGuid():N}.yml");
        try
        {
            File.WriteAllText(path, """
maxRounds: 5
debateFormat: oxford
memoryBackend: sqlite
""");
            var args = new[] { "--config", path };
            var config = ConfigLoader.Load(args);

            Assert.Equal(5, config.MaxRounds);
            Assert.Equal("oxford", config.DebateFormat);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_BotAProviderFlag_SetsBotAProvider()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--bot-a-provider", "bedrock" };
        var config = ConfigLoader.Load(args);
        Assert.Equal("bedrock", config.BotAProvider);
    }

    [Fact]
    public void Load_BotBProviderFlag_SetsBotBProvider()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--bot-b-provider", "openrouter" };
        var config = ConfigLoader.Load(args);
        Assert.Equal("openrouter", config.BotBProvider);
    }

    [Fact]
    public void Load_JudgeProviderFlag_SetsJudgeProvider()
    {
        var args = new[] { "--config", "/tmp/nonexistent_xyz.yml", "--judge-provider", "bedrock" };
        var config = ConfigLoader.Load(args);
        Assert.Equal("bedrock", config.JudgeProvider);
    }

    [Fact]
    public void Load_NoArgsNoFile_BotAAndJudgeProvidersAreNull()
    {
        var args = new[] { "--config", "/tmp/nonexistent_vibewars_config_xyz.yml" };
        var config = ConfigLoader.Load(args);
        Assert.Null(config.BotAProvider);
        Assert.Null(config.JudgeProvider);
    }

    [Fact]
    public void GenerateStarterConfig_ContainsBotAAndJudgeProviderComments()
    {
        var yaml = ConfigLoader.GenerateStarterConfig();
        Assert.Contains("botAProvider", yaml);
        Assert.Contains("judgeProvider", yaml);
    }
}
