using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.FactChecker;

/// <summary>
/// Runs a fact-checking pass against a bot's argument using a dedicated LLM call.
/// </summary>
public sealed class FactCheckerService
{
    private readonly IChatClient _client;

    private const string FactCheckerSystemPrompt = """
You are a rigorous fact-checker. Given the following argument, identify any specific factual claims (statistics, attributions, events). For each claim, rate confidence as HIGH (widely established), MEDIUM (plausible but unverified), or LOW (suspicious/unlikely). Return JSON only, no explanation outside JSON:
{"claims": [{"claim": "...", "confidence": "HIGH|MEDIUM|LOW", "note": "..."}]}
""";

    public FactCheckerService(IChatClient client) => _client = client;

    public async Task<FactCheckResult> CheckAsync(string argument, CancellationToken ct = default)
    {
        try
        {
            var (reply, _) = await _client.ChatAsync(
                FactCheckerSystemPrompt,
                [new ChatMessage("user", argument)],
                ct);

            return ParseResult(reply);
        }
        catch
        {
            return new FactCheckResult([]);
        }
    }

    public static FactCheckResult ParseResult(string json)
    {
        try
        {
            var trimmed = json.Trim();
            if (trimmed.StartsWith("```"))
            {
                var start = trimmed.IndexOf('{');
                var end   = trimmed.LastIndexOf('}');
                if (start >= 0 && end > start) trimmed = trimmed[start..(end + 1)];
            }
            using var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("claims", out var claimsEl))
                return new FactCheckResult([]);

            var claims = new List<FactClaim>();
            foreach (var item in claimsEl.EnumerateArray())
            {
                var claim      = item.TryGetProperty("claim",      out var c)    ? c.GetString()    ?? "" : "";
                var confidence = item.TryGetProperty("confidence", out var conf) ? conf.GetString() ?? "MEDIUM" : "MEDIUM";
                var note       = item.TryGetProperty("note",       out var n)    ? n.GetString()    ?? "" : "";
                claims.Add(new FactClaim(claim, confidence.ToUpperInvariant(), note));
            }
            return new FactCheckResult(claims);
        }
        catch
        {
            return new FactCheckResult([]);
        }
    }

    public static void Print(FactCheckResult result)
    {
        if (result.Claims.Count == 0) return;
        Console.WriteLine();
        foreach (var claim in result.Claims)
        {
            var icon  = claim.Confidence == "HIGH" ? "✓" : claim.Confidence == "LOW" ? "✗" : "⚠";
            var label = $"  {icon} {claim.Confidence,-7} \"{claim.Claim}\"";
            if (!string.IsNullOrWhiteSpace(claim.Note))
                label += $" ({claim.Note})";
            Console.WriteLine(label);
        }
    }

    public static string FormatLowConfidenceFlags(FactCheckResult result)
    {
        var low = result.Claims.Where(c => c.Confidence == "LOW").ToList();
        if (low.Count == 0) return string.Empty;
        return "The fact-checker flagged the following claims as low-confidence: " +
               string.Join("; ", low.Select(c => $"\"{c.Claim}\""));
    }
}
