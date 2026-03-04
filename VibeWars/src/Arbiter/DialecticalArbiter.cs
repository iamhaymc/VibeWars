using System.Text;
using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.Arbiter;

public record ArbiterSynthesis(
    string CoreThesis,
    string CoreAntithesis,
    string Synthesis,
    string[] OpenQuestions
);

public sealed class DialecticalArbiter
{
    private readonly IChatClient _client;

    private const string ArbiterSystem = """
You are a Hegelian dialectical synthesizer. Given a completed debate, extract the core defensible thesis, the core defensible antithesis, and forge a synthesis position that genuinely integrates the strongest elements of both. The synthesis must not be a compromise — it must transcend both positions by resolving the contradiction. Identify residual open questions that neither position could settle.
Return JSON: {"thesis": "...", "antithesis": "...", "synthesis": "...", "open_questions": ["...", "..."]}
""";

    private const string MiniSynthesisSystem = """
After this debate exchange, what emergent insight has become available that neither debater stated explicitly? Be concise.
""";

    public DialecticalArbiter(IChatClient client) => _client = client;

    public async Task<ArbiterSynthesis> SynthesizeAsync(
        IReadOnlyList<MemoryEntry> debateEntries,
        string finalSynthesis,
        CancellationToken ct = default)
    {
        try
        {
            var transcript = string.Join("\n\n", debateEntries
                .Where(e => e.BotName is "Bot A" or "Bot B")
                .Select(e => $"[{e.BotName}]: {e.Content}"));
            var prompt = $"{transcript}\n\nFinal judge synthesis: {finalSynthesis}";
            var (reply, _) = await _client.ChatAsync(ArbiterSystem,
                [new ChatMessage("user", prompt)], ct);
            return ParseSynthesis(reply, finalSynthesis);
        }
        catch
        {
            return new ArbiterSynthesis("", "", finalSynthesis, []);
        }
    }

    public async Task<string> MiniSynthesizeAsync(
        string roundExchange, CancellationToken ct = default)
    {
        try
        {
            var (reply, _) = await _client.ChatAsync(MiniSynthesisSystem,
                [new ChatMessage("user", roundExchange)], ct);
            return reply.Trim();
        }
        catch { return ""; }
    }

    public static ArbiterSynthesis ParseSynthesis(string json, string fallbackSynthesis = "")
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
            using var doc  = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var thesis     = root.TryGetProperty("thesis",      out var t)  ? t.GetString()  ?? "" : "";
            var antithesis = root.TryGetProperty("antithesis",  out var an) ? an.GetString() ?? "" : "";
            var synthesis  = root.TryGetProperty("synthesis",   out var sy) ? sy.GetString() ?? fallbackSynthesis : fallbackSynthesis;
            var questions  = new List<string>();
            if (root.TryGetProperty("open_questions", out var oq))
                foreach (var item in oq.EnumerateArray())
                    if (item.GetString() is string q && !string.IsNullOrWhiteSpace(q))
                        questions.Add(q);
            return new ArbiterSynthesis(thesis, antithesis, synthesis, [.. questions]);
        }
        catch { return new ArbiterSynthesis("", "", fallbackSynthesis, []); }
    }

    public static string RenderSynthesis(ArbiterSynthesis synthesis)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔════════════════════════════════════════════════════════╗");
        sb.AppendLine("║              🌀 Dialectical Synthesis                  ║");
        sb.AppendLine("╠════════════════════════════════════════════════════════╣");
        AppendWrapped(sb, "Thesis:    ", synthesis.CoreThesis);
        AppendWrapped(sb, "Antithesis:", synthesis.CoreAntithesis);
        AppendWrapped(sb, "Synthesis: ", synthesis.Synthesis);
        if (synthesis.OpenQuestions.Length > 0)
        {
            sb.AppendLine("║ Unresolved:                                            ║");
            foreach (var q in synthesis.OpenQuestions)
                AppendWrapped(sb, "  •", q);
        }
        sb.AppendLine("╚════════════════════════════════════════════════════════╝");
        return sb.ToString();
    }

    private static void AppendWrapped(StringBuilder sb, string label, string text)
    {
        var content = string.IsNullOrWhiteSpace(text) ? "(none)" : text;
        var line    = $"║ {label} {content}";
        if (line.Length > 57) line = line[..57] + "…";
        sb.AppendLine($"{line,-57}║");
    }
}
