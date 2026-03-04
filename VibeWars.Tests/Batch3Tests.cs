using System;
using System.Collections.Generic;
using VibeWars.FactChecker;
using VibeWars.Models;
using VibeWars.Reports;
using VibeWars.TUI;

namespace VibeWars.Tests;

// ─── Feature 8: Debate Report Generator Tests ─────────────────────────────────

public sealed class DebateReportGeneratorTests
{
    [Fact]
    public void GenerateMarkdown_ProducesValidMarkdown()
    {
        var session = new DebateSession(Guid.NewGuid(), "AI ethics", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(10), "Bot A", "Great debate.", "Structured");
        var entries = new[]
        {
            new MemoryEntry(Guid.NewGuid(), "Bot A", "AI ethics", 1, "assistant", "AI poses risks.", DateTimeOffset.UtcNow, ["AI ethics"]),
            new MemoryEntry(Guid.NewGuid(), "Bot B", "AI ethics", 1, "assistant", "AI has benefits.", DateTimeOffset.UtcNow, ["AI ethics"]),
            new MemoryEntry(Guid.NewGuid(), "Judge", "AI ethics", 1, "assistant", "{\"winner\":\"Bot A\",\"reasoning\":\"Better argument\"}", DateTimeOffset.UtcNow, ["AI ethics", "verdict"]),
        };
        var md = DebateReportGenerator.GenerateMarkdown(session, entries);
        Assert.Contains("## VibeWars Debate Report", md);
        Assert.Contains("AI ethics", md);
        Assert.Contains("Bot A", md);
    }

    [Fact]
    public void GenerateHtml_ProducesValidHtml()
    {
        var session = new DebateSession(Guid.NewGuid(), "Climate change", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), "Tie", "Both made points.", "Freeform");
        var entries = Array.Empty<MemoryEntry>();
        var html = DebateReportGenerator.GenerateHtml(session, entries);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("Climate change", html);
        Assert.Contains("Tie", html);
    }

    [Fact]
    public void GenerateMarkdown_IncludesFactCheckSection_WhenFactCheckEntriesPresent()
    {
        var session = new DebateSession(Guid.NewGuid(), "Vaccines", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), "Bot B", "Good points.", "Freeform");
        var entries = new[]
        {
            new MemoryEntry(Guid.NewGuid(), "Bot A", "Vaccines", 1, "fact-check", "HIGH: Vaccines are safe", DateTimeOffset.UtcNow, ["Vaccines", "fact-check"]),
        };
        var md = DebateReportGenerator.GenerateMarkdown(session, entries);
        Assert.Contains("Fact-Check Summary", md);
    }

    [Fact]
    public void GenerateMarkdown_IncludesFrontMatter()
    {
        var session = new DebateSession(Guid.NewGuid(), "Test topic", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), "Tie", "", "Freeform");
        var md = DebateReportGenerator.GenerateMarkdown(session, Array.Empty<MemoryEntry>());
        Assert.Contains("---", md);
        Assert.Contains("topic:", md);
        Assert.Contains("winner:", md);
    }

    [Fact]
    public void GenerateHtml_IncludesFactCheckSection_WhenPresent()
    {
        var session = new DebateSession(Guid.NewGuid(), "Space", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), "Bot A", "", "Freeform");
        var entries = new[]
        {
            new MemoryEntry(Guid.NewGuid(), "Bot B", "Space", 1, "fact-check", "LOW: Mars is nearby", DateTimeOffset.UtcNow, ["Space", "fact-check"]),
        };
        var html = DebateReportGenerator.GenerateHtml(session, entries);
        Assert.Contains("Fact-Check Summary", html);
        Assert.Contains("Mars is nearby", html);
    }

    [Fact]
    public void GenerateMarkdown_IncludesFinalSynthesis_WhenPresent()
    {
        var session = new DebateSession(Guid.NewGuid(), "Tech", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), "Bot A", "This is the final synthesis.", "Freeform");
        var md = DebateReportGenerator.GenerateMarkdown(session, Array.Empty<MemoryEntry>());
        Assert.Contains("Final Synthesis", md);
        Assert.Contains("This is the final synthesis.", md);
    }
}

// ─── Feature 7: Fact Checker Service Tests ────────────────────────────────────

public sealed class FactCheckerServiceTests
{
    [Fact]
    public void ParseResult_ValidJson_ReturnsCorrectClaims()
    {
        var json = "{\"claims\":[{\"claim\":\"The Earth is round\",\"confidence\":\"HIGH\",\"note\":\"Well established\"},{\"claim\":\"100% consensus\",\"confidence\":\"LOW\",\"note\":\"Overstated\"}]}";
        var result = FactCheckerService.ParseResult(json);
        Assert.Equal(2, result.Claims.Count);
        Assert.Equal("HIGH", result.Claims[0].Confidence);
        Assert.Equal("LOW", result.Claims[1].Confidence);
    }

    [Fact]
    public void ParseResult_MalformedJson_ReturnsEmpty()
    {
        var result = FactCheckerService.ParseResult("not json at all");
        Assert.Empty(result.Claims);
    }

    [Fact]
    public void ParseResult_EmptyClaims_ReturnsEmpty()
    {
        var result = FactCheckerService.ParseResult("{\"claims\":[]}");
        Assert.Empty(result.Claims);
    }

    [Fact]
    public void FormatLowConfidenceFlags_ReturnsOnlyLowItems()
    {
        var result = new FactCheckResult([
            new FactClaim("Safe claim", "HIGH", ""),
            new FactClaim("Suspicious claim", "LOW", "No evidence"),
        ]);
        var flags = FactCheckerService.FormatLowConfidenceFlags(result);
        Assert.Contains("Suspicious claim", flags);
        Assert.DoesNotContain("Safe claim", flags);
    }

    [Fact]
    public void FormatLowConfidenceFlags_NoLowItems_ReturnsEmpty()
    {
        var result = new FactCheckResult([new FactClaim("Safe", "HIGH", "")]);
        var flags = FactCheckerService.FormatLowConfidenceFlags(result);
        Assert.Equal(string.Empty, flags);
    }

    [Fact]
    public void ParseResult_JsonWrappedInMarkdown_ParsesCorrectly()
    {
        var json = "```json\n{\"claims\":[{\"claim\":\"Test\",\"confidence\":\"MEDIUM\",\"note\":\"ok\"}]}\n```";
        var result = FactCheckerService.ParseResult(json);
        Assert.Single(result.Claims);
    }

    [Fact]
    public void ParseResult_MissingClaimsProperty_ReturnsEmpty()
    {
        var result = FactCheckerService.ParseResult("{\"other\":\"value\"}");
        Assert.Empty(result.Claims);
    }

    [Fact]
    public void ParseResult_ConfidenceNormalisedToUpperCase()
    {
        var json = "{\"claims\":[{\"claim\":\"Test\",\"confidence\":\"medium\",\"note\":\"\"}]}";
        var result = FactCheckerService.ParseResult(json);
        Assert.Equal("MEDIUM", result.Claims[0].Confidence);
    }
}

// ─── Feature 6: SpectreRenderer Smoke Tests ───────────────────────────────────

public sealed class SpectreRendererTests
{
    [Fact]
    public void SpectreRenderer_CanBeInstantiated()
    {
        var renderer = new SpectreRenderer();
        Assert.NotNull(renderer);
    }
}
