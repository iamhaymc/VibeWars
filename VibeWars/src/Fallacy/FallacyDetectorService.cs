using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Fallacy;

public record FallacyResult(bool HasFallacy, string FallacyName, string Explanation);

/// <summary>
/// Detects logical fallacies in arguments. Two modes:
/// - Defensive: checks own argument before submission, suggests revision
/// - Offensive: checks opponent's argument, injects callout into next prompt
/// </summary>
public sealed class FallacyDetectorService
{
    private readonly IChatClient _client;

    private const string DetectorSystem = """
You are a logical fallacy detector for formal debates.
Common fallacies to check: Ad Hominem, Straw Man, Appeal to Authority, Slippery Slope, False Dichotomy, Circular Reasoning, Red Herring, Appeal to Emotion, Hasty Generalization, Tu Quoque, Burden of Proof, Equivocation, Bandwagon, Appeal to Nature, Genetic Fallacy.
Analyze the argument and return JSON: {"has_fallacy": true|false, "fallacy_name": "...", "explanation": "..."}
If no fallacy is found, set has_fallacy to false and leave other fields empty.
""";

    private const string RevisionSystem = """
The following argument contains a logical fallacy. Revise it to eliminate the fallacy while keeping the core point intact. Return only the revised argument, no explanation.
""";

    public FallacyDetectorService(IChatClient client) => _client = client;

    /// <summary>Checks an argument for logical fallacies (offensive mode).</summary>
    public async Task<FallacyResult> DetectAsync(string argument, CancellationToken ct = default)
    {
        try
        {
            var (reply, _) = await _client.ChatAsync(DetectorSystem,
                [new ChatMessage("user", $"Analyze this argument:\n{argument}")], ct);
            return ParseResult(reply);
        }
        catch { return new FallacyResult(false, "", ""); }
    }

    /// <summary>Checks own argument and returns a revised version if a fallacy is found (defensive mode).</summary>
    public async Task<(string RevisedArgument, FallacyResult? DetectedFallacy)> DefensiveCheckAsync(
        string argument, CancellationToken ct = default)
    {
        try
        {
            var result = await DetectAsync(argument, ct);
            if (!result.HasFallacy) return (argument, null);

            var revisionPrompt = $"Original argument with {result.FallacyName}:\n{argument}";
            var (revised, _) = await _client.ChatAsync(RevisionSystem,
                [new ChatMessage("user", revisionPrompt)], ct);
            return (revised, result);
        }
        catch { return (argument, null); }
    }

    public static FallacyResult ParseResult(string json)
    {
        try
        {
            var trimmed = json.Trim();
            if (trimmed.StartsWith("```"))
            {
                var start = trimmed.IndexOf('{');
                var end = trimmed.LastIndexOf('}');
                if (start >= 0 && end > start) trimmed = trimmed[start..(end + 1)];
            }
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var has = root.TryGetProperty("has_fallacy", out var h) && h.GetBoolean();
            var name = root.TryGetProperty("fallacy_name", out var n) ? n.GetString() ?? "" : "";
            var expl = root.TryGetProperty("explanation", out var e) ? e.GetString() ?? "" : "";
            return new FallacyResult(has, name, expl);
        }
        catch { return new FallacyResult(false, "", ""); }
    }

    public static string FormatCallout(FallacyResult result)
        => result.HasFallacy
            ? $"Your opponent committed a {result.FallacyName}: {result.Explanation}. Call this out."
            : "";
}
