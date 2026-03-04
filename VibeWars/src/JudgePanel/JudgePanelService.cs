using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.JudgePanel;

/// <summary>
/// Aggregates verdicts from multiple judge clients, reducing single-model bias.
/// </summary>
public sealed class JudgePanelService : IDisposable
{
    private readonly (string Name, IChatClient Client)[] _panelists;

    public JudgePanelService(IEnumerable<(string Name, IChatClient Client)> panelists)
        => _panelists = panelists.ToArray();

    public async Task<JudgeVerdict> EvaluateAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string judgePrompt,
        CancellationToken ct = default)
    {
        if (_panelists.Length == 0)
            throw new InvalidOperationException("Judge panel has no panelists.");

        // Call all judges in parallel
        var tasks = _panelists.Select(async p =>
        {
            var h = new List<ChatMessage>(history) { new("user", judgePrompt) };
            try
            {
                var (reply, _) = await p.Client.ChatAsync(systemPrompt, h, ct);
                return (p.Name, Reply: reply, Error: false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Panel] {p.Name} failed: {ex.Message}");
                return (p.Name, Reply: string.Empty, Error: true);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // Parse each verdict
        var verdicts = results
            .Where(r => !r.Error && !string.IsNullOrEmpty(r.Reply))
            .Select(r => (r.Name, Verdict: ParseVerdict(r.Reply)))
            .ToList();

        if (verdicts.Count == 0)
            return new JudgeVerdict("Tie", "All panelists failed.", string.Empty);

        // Display individual verdicts
        foreach (var (name, v) in verdicts)
            Console.WriteLine($"  [{name}] → {v.Winner}");

        return Aggregate(verdicts.Select(v => v.Verdict).ToList());
    }

    public static JudgeVerdict Aggregate(IList<JudgeVerdict> verdicts)
    {
        if (verdicts.Count == 0) return new JudgeVerdict("Tie", "No verdicts.", string.Empty);
        if (verdicts.Count == 1) return verdicts[0];

        var botACount = verdicts.Count(v => v.Winner.Trim().StartsWith("Bot A", StringComparison.OrdinalIgnoreCase)
                                           || v.Winner.Trim().Equals("A", StringComparison.OrdinalIgnoreCase));
        var botBCount = verdicts.Count(v => v.Winner.Trim().StartsWith("Bot B", StringComparison.OrdinalIgnoreCase)
                                           || v.Winner.Trim().Equals("B", StringComparison.OrdinalIgnoreCase));

        var winner = botACount > botBCount ? "Bot A" :
                     botBCount > botACount ? "Bot B" : "Tie";

        // Consensus reasoning
        var reasonings = verdicts.Select(v => v.Reasoning).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var reasoning = reasonings.Count == 1
            ? $"Panel consensus: {reasonings[0]}"
            : $"Panel consensus: {verdicts[0].Reasoning} Dissent: {string.Join("; ", reasonings.Skip(1))}";

        // Union of unique new ideas
        var allIdeas = verdicts
            .SelectMany(v => v.NewIdeas.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var newIdeas = string.Join("; ", allIdeas);

        return new JudgeVerdict(winner, reasoning, newIdeas);
    }

    /// <summary>
    /// Computes controversy score: 0.0 = unanimous, 1.0 = maximum disagreement.
    /// Formula: 1.0 - (max_vote_count / total_judges).
    /// </summary>
    public static double ComputeControversy(IList<JudgeVerdict> verdicts)
    {
        if (verdicts.Count <= 1) return 0.0;
        var groups = verdicts.GroupBy(v => v.Winner, StringComparer.OrdinalIgnoreCase);
        var maxVotes = groups.Max(g => g.Count());
        return 1.0 - (double)maxVotes / verdicts.Count;
    }

    private static JudgeVerdict ParseVerdict(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return new JudgeVerdict(
                root.TryGetProperty("winner",    out var w)  ? w.GetString()  ?? "Tie" : "Tie",
                root.TryGetProperty("reasoning", out var rs) ? rs.GetString() ?? raw   : raw,
                root.TryGetProperty("new_ideas", out var ni) ? ni.GetString() ?? ""    : "");
        }
        catch
        {
            return new JudgeVerdict("Tie", raw, string.Empty);
        }
    }

    public void Dispose()
    {
        foreach (var (_, client) in _panelists) client.Dispose();
    }
}
