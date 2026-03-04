using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Commentator;

public enum CommentatorStyle
{
    Sports,
    Academic,
    Snarky,
    Dramatic,
    Dry,
}

/// <summary>
/// Provides live commentary on debate exchanges using a configurable LLM client and style.
/// </summary>
public class CommentatorService
{
    private readonly IChatClient _client;
    private readonly CommentatorStyle _style;

    public CommentatorService(IChatClient client, CommentatorStyle style = CommentatorStyle.Sports)
    {
        _client = client;
        _style  = style;
    }

    /// <summary>Parses a style string case-insensitively; defaults to Sports on failure.</summary>
    public static CommentatorStyle ParseStyle(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return CommentatorStyle.Sports;
        return Enum.TryParse<CommentatorStyle>(input, ignoreCase: true, out var result)
            ? result
            : CommentatorStyle.Sports;
    }

    /// <summary>Returns a style-specific instruction appended to the system prompt.</summary>
    public static string GetStyleInstruction(CommentatorStyle style) => style switch
    {
        CommentatorStyle.Sports   => "You are an enthusiastic sports announcer — high energy, use dramatic pauses, and build hype.",
        CommentatorStyle.Academic => "You are a measured academic moderator — analytical, precise, and reference rhetorical frameworks by name.",
        CommentatorStyle.Snarky   => "You are a sharp-tongued cynical critic — witty, occasionally sarcastic, but always insightful.",
        CommentatorStyle.Dramatic => "You are a theatrical drama critic — every exchange is operatic, emotions run high, and you speak in vivid metaphors.",
        CommentatorStyle.Dry      => "You are a deadpan dry commentator — understate everything, let silence do the work, minimal words maximum impact.",
        _                         => "You are an enthusiastic sports announcer — high energy, use dramatic pauses, and build hype.",
    };

    /// <summary>Returns the full system prompt for the commentator.</summary>
    public string GetSystemPrompt(CommentatorStyle style)
    {
        var basePrompt =
            "You are a witty debate commentator. " +
            "After each exchange, deliver a 1-2 sentence live commentary: note the rhetorical move used, " +
            "react to surprising turns, and build suspense. Never pick a side explicitly.";

        return basePrompt + " " + GetStyleInstruction(style);
    }

    /// <summary>
    /// Generates live commentary for a bot exchange.
    /// </summary>
    public async Task<string> CommentAsync(
        string botAArgument,
        string botBArgument,
        CancellationToken ct = default)
    {
        var systemPrompt = GetSystemPrompt(_style);
        var userMessage  = $"Bot A argued:\n{botAArgument}\n\nBot B responded:\n{botBArgument}\n\nProvide your live commentary.";

        var (reply, _) = await _client.ChatAsync(systemPrompt, [new("user", userMessage)], ct);
        return reply;
    }
}
