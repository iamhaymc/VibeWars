using System.Text.Json;
using System.Text.RegularExpressions;
using VibeWars.Clients;

namespace VibeWars.Challenges;

public enum ChallengeType
{
    CitationNeeded,
    PointOfOrder,
    PersonalFoul,
    PointOfInformation,
    DirectChallenge,
}

/// <summary>Result from the challenge detector LLM call.</summary>
public record ChallengeDetectorResult(bool ShouldChallenge, string Type, string Target);

/// <summary>Represents a formal interruption during a debate.</summary>
public record DebateInterruption(
    string BotName,
    int Round,
    ChallengeType Type,
    string TargetClaim,
    bool IsGranted);

/// <summary>
/// Detects challengeable claims in debate arguments and injects formal interruptions.
/// </summary>
public class ChallengeService
{
    private readonly IChatClient _client;

    public ChallengeService(IChatClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Parses the challenge detector JSON response. Handles markdown-wrapped JSON.
    /// Returns null on failure.
    /// </summary>
    public static ChallengeDetectorResult? ParseChallengeResult(string json)
    {
        try
        {
            // Strip markdown code fences if present
            var cleaned = Regex.Replace(json.Trim(), @"^```(?:json)?\s*|\s*```$", "", RegexOptions.Multiline).Trim();

            using var doc        = JsonDocument.Parse(cleaned);
            var root             = doc.RootElement;
            bool shouldChallenge = root.GetProperty("should_challenge").GetBoolean();
            string type          = root.GetProperty("type").GetString() ?? "";
            string target        = root.GetProperty("target").GetString() ?? "";
            return new ChallengeDetectorResult(shouldChallenge, type, target);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Builds the challenge detector prompt for a given debate turn.</summary>
    public string BuildChallengePrompt(string turnText) =>
        "Does the following argument contain any of: (a) a specific factual claim that could be challenged for lack of evidence, " +
        "(b) an ad hominem attack, (c) a logical fallacy? If yes, identify the type and the specific claim. " +
        "Return JSON: {\"should_challenge\": true|false, \"type\": \"...\", \"target\": \"...\"}" +
        $"\n\nArgument:\n{turnText}";

    /// <summary>
    /// Builds the POINT OF ORDER injection text to prepend to the challenger's next prompt.
    /// </summary>
    public string BuildInterruptionInjection(string challengerBot, ChallengeDetectorResult challenge) =>
        $"POINT OF ORDER: {challengerBot} challenges the claim that \"{challenge.Target}\". " +
        "Address this challenge directly in your next turn before advancing your argument.";

    /// <summary>Formats a DebateInterruption for display.</summary>
    public static string FormatInterruption(DebateInterruption interruption) =>
        $"⚡ [{interruption.BotName}] {interruption.Type}: \"{interruption.TargetClaim}\"";
}
