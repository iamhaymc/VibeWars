using System.Text;
using VibeWars.Models;
using VibeWars.Reports;

namespace VibeWars.Reports;

/// <summary>
/// Generates Markdown and HTML debate reports from stored session data.
/// </summary>
public static class DebateReportGenerator
{
    public static string GenerateMarkdown(DebateSession session, IReadOnlyList<MemoryEntry> entries)
    {
        var sb = new StringBuilder();

        // YAML front matter
        sb.AppendLine("---");
        sb.AppendLine($"topic: \"{EscapeYaml(session.Topic)}\"");
        sb.AppendLine($"date: {session.StartedAt:yyyy-MM-dd}");
        sb.AppendLine($"winner: \"{EscapeYaml(session.OverallWinner)}\"");
        sb.AppendLine($"format: \"{EscapeYaml(session.Format)}\"");
        if (session.Complexity != "Standard")
            sb.AppendLine($"complexity: \"{EscapeYaml(session.Complexity)}\"");
        if (session.TotalTokens > 0)
            sb.AppendLine($"totalTokens: {session.TotalTokens}");
        if (session.EstimatedCostUsd.HasValue)
            sb.AppendLine($"estimatedCostUsd: {session.EstimatedCostUsd.Value:F4}");
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("## VibeWars Debate Report");
        sb.AppendLine();
        sb.AppendLine($"**Topic:** {session.Topic}");
        sb.AppendLine($"**Date:** {session.StartedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"**Format:** {session.Format}");
        if (session.Complexity != "Standard")
            sb.AppendLine($"**Complexity:** {session.Complexity}");
        sb.AppendLine($"**Winner:** {session.OverallWinner}");
        sb.AppendLine();

        var rounds = entries.Where(e => e.Round > 0).GroupBy(e => e.Round).OrderBy(g => g.Key);

        sb.AppendLine("### Transcript");
        sb.AppendLine();

        foreach (var roundGroup in rounds)
        {
            sb.AppendLine($"#### Round {roundGroup.Key}");
            sb.AppendLine();
            foreach (var entry in roundGroup.OrderBy(e => e.Timestamp))
            {
                if (IsJudgeVerdict(entry)) continue;
                var prefix = entry.BotName == "Bot A" ? "> **Bot A:**" :
                             entry.BotName == "Bot B" ? "> **Bot B:**" :
                             entry.BotName == "Human" ? "> **Human:**" : $"> **{entry.BotName}:**";
                sb.AppendLine(prefix);
                foreach (var line in entry.Content.Split('\n'))
                    sb.AppendLine($"> {line}");
                sb.AppendLine();
            }

            var judgeEntry = roundGroup.FirstOrDefault(IsJudgeVerdict);
            if (judgeEntry != null)
            {
                sb.AppendLine("**⚖ Judge:**");
                sb.AppendLine();
                sb.AppendLine($"> {judgeEntry.Content.Replace("\n", "\n> ")}");
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(session.FinalSynthesis))
        {
            sb.AppendLine("### Final Synthesis");
            sb.AppendLine();
            sb.AppendLine(session.FinalSynthesis);
            sb.AppendLine();
        }

        var factCheckEntries = entries.Where(e => e.Tags.Contains("fact-check")).ToList();
        if (factCheckEntries.Count > 0)
        {
            sb.AppendLine("### Fact-Check Summary");
            sb.AppendLine();
            foreach (var fce in factCheckEntries)
            {
                sb.AppendLine($"**Round {fce.Round} — {fce.BotName}:**");
                sb.AppendLine(fce.Content);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public static string GenerateHtml(DebateSession session, IReadOnlyList<MemoryEntry> entries)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine($"<meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>VibeWars: {HtmlEncode(session.Topic)}</title>");
        sb.AppendLine(GetEmbeddedCss());
        sb.AppendLine("</head><body>");

        // Sticky header
        sb.AppendLine("<header class=\"sticky-header\">");
        sb.AppendLine($"  <h1>⚔ VibeWars Debate</h1>");
        sb.AppendLine($"  <div class=\"topic\">{HtmlEncode(session.Topic)}</div>");
        var complexityLabel = session.Complexity != "Standard" ? $" | Complexity: {HtmlEncode(session.Complexity)}" : "";
        sb.AppendLine($"  <div class=\"meta\">Winner: <strong>{HtmlEncode(session.OverallWinner)}</strong> | {session.StartedAt:yyyy-MM-dd} | Format: {HtmlEncode(session.Format)}{complexityLabel}</div>");
        sb.AppendLine("</header>");

        sb.AppendLine("<main>");

        var rounds = entries.Where(e => e.Round > 0).GroupBy(e => e.Round).OrderBy(g => g.Key);
        foreach (var roundGroup in rounds)
        {
            sb.AppendLine($"<section class=\"round\"><h2>Round {roundGroup.Key}</h2>");
            sb.AppendLine("<div class=\"exchange\">");

            var botA = roundGroup.FirstOrDefault(e => e.BotName is "Bot A" or "Human" && e.Role == "assistant");
            var botB = roundGroup.FirstOrDefault(e => e.BotName == "Bot B" && e.Role == "assistant");

            sb.AppendLine("<div class=\"bot-a-col\">");
            if (botA != null)
            {
                sb.AppendLine($"<div class=\"message bot-a\"><div class=\"label\">{HtmlEncode(botA.BotName)}</div>");
                sb.AppendLine($"<div class=\"content\">{HtmlEncode(botA.Content).Replace("\n", "<br>")}</div></div>");
            }
            sb.AppendLine("</div>");

            sb.AppendLine("<div class=\"bot-b-col\">");
            if (botB != null)
            {
                sb.AppendLine($"<div class=\"message bot-b\"><div class=\"label\">Bot B</div>");
                sb.AppendLine($"<div class=\"content\">{HtmlEncode(botB.Content).Replace("\n", "<br>")}</div></div>");
            }
            sb.AppendLine("</div></div>");

            var judge = roundGroup.FirstOrDefault(IsJudgeVerdict);
            if (judge != null)
            {
                sb.AppendLine($"<div class=\"message judge\"><div class=\"label\">⚖ Judge</div>");
                sb.AppendLine($"<div class=\"content\">{HtmlEncode(judge.Content).Replace("\n", "<br>")}</div></div>");
            }

            sb.AppendLine("</section>");
        }

        if (!string.IsNullOrWhiteSpace(session.FinalSynthesis))
        {
            sb.AppendLine("<section class=\"synthesis\"><h2>⚔ Final Synthesis</h2>");
            sb.AppendLine($"<p>{HtmlEncode(session.FinalSynthesis).Replace("\n", "<br>")}</p></section>");
        }

        var factCheckEntries = entries.Where(e => e.Tags.Contains("fact-check")).ToList();
        if (factCheckEntries.Count > 0)
        {
            sb.AppendLine("<section class=\"factcheck\"><h2>🔍 Fact-Check Summary</h2>");
            foreach (var fce in factCheckEntries)
            {
                sb.AppendLine($"<h3>Round {fce.Round} — {HtmlEncode(fce.BotName)}</h3>");
                sb.AppendLine($"<pre>{HtmlEncode(fce.Content)}</pre>");
            }
            sb.AppendLine("</section>");
        }

        sb.AppendLine("</main></body></html>");
        return sb.ToString();
    }

    private static string GetEmbeddedCss() => """
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background: #0f0f0f; color: #e0e0e0; line-height: 1.6; }
.sticky-header { position: sticky; top: 0; background: #1a1a2e; padding: 16px 24px; border-bottom: 1px solid #333; z-index: 100; }
.sticky-header h1 { font-size: 1.4rem; color: #e94560; }
.sticky-header .topic { font-size: 1.1rem; font-weight: 600; margin-top: 4px; }
.sticky-header .meta { font-size: 0.85rem; color: #aaa; margin-top: 4px; }
main { max-width: 1200px; margin: 0 auto; padding: 24px; }
.round { margin-bottom: 32px; border: 1px solid #333; border-radius: 8px; padding: 16px; background: #141414; }
.round h2 { font-size: 1rem; color: #888; margin-bottom: 12px; text-transform: uppercase; letter-spacing: 1px; }
.exchange { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin-bottom: 16px; }
.message { border-radius: 8px; padding: 14px; }
.message .label { font-size: 0.75rem; font-weight: 700; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 8px; }
.message .content { font-size: 0.9rem; }
.bot-a { background: #0d1b3e; border-left: 3px solid #4a9eff; }
.bot-a .label { color: #4a9eff; }
.bot-b { background: #0d3e1b; border-left: 3px solid #4aff8a; }
.bot-b .label { color: #4aff8a; }
.judge { background: #2a2000; border-left: 3px solid #ffcc00; padding: 14px; border-radius: 8px; margin-top: 8px; }
.judge .label { color: #ffcc00; font-size: 0.75rem; font-weight: 700; text-transform: uppercase; margin-bottom: 8px; }
.synthesis { background: #1a1a2e; border: 2px solid #e94560; border-radius: 8px; padding: 20px; margin-top: 24px; }
.synthesis h2 { color: #e94560; margin-bottom: 12px; }
.factcheck { background: #1a1a2e; border: 1px solid #555; border-radius: 8px; padding: 20px; margin-top: 24px; }
.factcheck h2 { color: #ccc; margin-bottom: 12px; }
pre { background: #0a0a0a; padding: 12px; border-radius: 4px; overflow-x: auto; font-size: 0.85rem; }
</style>
""";

    private static string HtmlEncode(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscapeYaml(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    public static string GeneratePodcast(
        DebateSession session,
        IReadOnlyList<MemoryEntry> entries,
        string? voiceA = null,
        string? voiceB = null) =>
        PodcastScriptGenerator.Generate(session, entries, voiceA, voiceB);

    private static bool IsJudgeVerdict(MemoryEntry e) =>
        e.BotName == "Judge" && e.Tags.Contains("verdict");
}
