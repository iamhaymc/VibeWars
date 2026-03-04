using System.Text;
using System.Text.Json;
using VibeWars.Clients;
using VibeWars.Models;

namespace VibeWars.ArgumentGraph;

/// <summary>
/// Extracts argument graphs from debate text using LLM-based parsing.
/// </summary>
public sealed class ArgumentGraphService
{
    private readonly IChatClient _client;

    private const string ClaimExtractorSystem = """
Identify each distinct claim in the following argument. For each claim, specify its type.
Return JSON array only, no explanation: [{"text": "...", "type": "Assertion|Evidence|Rebuttal|Concession|Question|Synthesis"}]
""";

    private const string RelationExtractorSystem = """
Map the new claims to previous claims using relation types.
Return JSON array only: [{"fromIndex": 0, "toIndex": 0, "relation": "Supports|Challenges|Extends|Answers|Concedes"}]
where fromIndex is an index into the new claims array and toIndex is an index into the previous claims array.
""";

    public ArgumentGraphService(IChatClient client) => _client = client;

    public async Task<IReadOnlyList<ArgumentNode>> ExtractClaimsAsync(
        string argument, Guid sessionId, int round, string botName, CancellationToken ct = default)
    {
        try
        {
            var (reply, _) = await _client.ChatAsync(
                ClaimExtractorSystem,
                [new ChatMessage("user", argument)],
                ct);
            return ParseClaims(reply, sessionId, round, botName);
        }
        catch
        {
            // Fallback: treat the whole argument as one assertion
            return [new ArgumentNode(Guid.NewGuid(), sessionId, round, botName,
                argument.Length > 200 ? argument[..200] : argument, ClaimType.Assertion)];
        }
    }

    public static IReadOnlyList<ArgumentNode> ParseClaims(
        string json, Guid sessionId, int round, string botName)
    {
        try
        {
            var trimmed = json.Trim();
            if (trimmed.StartsWith("```"))
            {
                var start = trimmed.IndexOf('[');
                var end   = trimmed.LastIndexOf(']');
                if (start >= 0 && end > start) trimmed = trimmed[start..(end + 1)];
            }
            using var doc  = JsonDocument.Parse(trimmed);
            var nodes = new List<ArgumentNode>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var type = item.TryGetProperty("type", out var ty) && 
                           Enum.TryParse<ClaimType>(ty.GetString(), ignoreCase: true, out var ct2) ? ct2 : ClaimType.Assertion;
                if (!string.IsNullOrWhiteSpace(text))
                    nodes.Add(new ArgumentNode(Guid.NewGuid(), sessionId, round, botName, text, type));
            }
            return nodes;
        }
        catch
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<ArgumentEdge>> ExtractRelationsAsync(
        IReadOnlyList<ArgumentNode> newClaims,
        IReadOnlyList<ArgumentNode> previousClaims,
        CancellationToken ct = default)
    {
        if (newClaims.Count == 0 || previousClaims.Count == 0) return [];
        
        try
        {
            var newClaimsList  = string.Join("\n", newClaims.Select((c, i) => $"{i}: {c.ClaimText}"));
            var prevClaimsList = string.Join("\n", previousClaims.Select((c, i) => $"{i}: {c.ClaimText}"));
            var prompt = $"New claims:\n{newClaimsList}\n\nPrevious claims:\n{prevClaimsList}";
            
            var (reply, _) = await _client.ChatAsync(
                RelationExtractorSystem,
                [new ChatMessage("user", prompt)],
                ct);
            
            return ParseRelations(reply, newClaims, previousClaims);
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<ArgumentEdge> ParseRelations(
        string json,
        IReadOnlyList<ArgumentNode> newClaims,
        IReadOnlyList<ArgumentNode> previousClaims)
    {
        try
        {
            var trimmed = json.Trim();
            if (trimmed.StartsWith("```"))
            {
                var start = trimmed.IndexOf('[');
                var end   = trimmed.LastIndexOf(']');
                if (start >= 0 && end > start) trimmed = trimmed[start..(end + 1)];
            }
            using var doc  = JsonDocument.Parse(trimmed);
            var edges = new List<ArgumentEdge>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var fromIdx = item.TryGetProperty("fromIndex", out var fi) ? fi.GetInt32() : -1;
                var toIdx   = item.TryGetProperty("toIndex",   out var ti) ? ti.GetInt32() : -1;
                var rel     = item.TryGetProperty("relation",  out var r) &&
                              Enum.TryParse<RelationType>(r.GetString(), ignoreCase: true, out var rv) ? rv : RelationType.Supports;
                
                if (fromIdx >= 0 && fromIdx < newClaims.Count &&
                    toIdx   >= 0 && toIdx   < previousClaims.Count)
                    edges.Add(new ArgumentEdge(newClaims[fromIdx].Id, previousClaims[toIdx].Id, rel));
            }
            return edges;
        }
        catch
        {
            return [];
        }
    }

    public static string ToMermaid(IReadOnlyList<ArgumentNode> nodes, IReadOnlyList<ArgumentEdge> edges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("graph TD");
        foreach (var node in nodes)
        {
            var label = node.ClaimText.Length > 50 ? node.ClaimText[..50] + "…" : node.ClaimText;
            label = label.Replace("\"", "'").Replace("\n", " ")
                         .Replace("[", "&#91;").Replace("]", "&#93;")
                         .Replace("{", "&#123;").Replace("}", "&#125;")
                         .Replace("|", "&#124;");
            var id = node.Id.ToString("N")[..8];
            sb.AppendLine($"  {id}[\"{label} [{node.BotName}/{node.ClaimType}]\"]");
        }
        var nodeIds = nodes.ToDictionary(n => n.Id, n => n.Id.ToString("N")[..8]);
        foreach (var edge in edges)
        {
            if (!nodeIds.TryGetValue(edge.FromId, out var from) || !nodeIds.TryGetValue(edge.ToId, out var to)) continue;
            var arrow = edge.Relation switch
            {
                RelationType.Supports   => "-->",
                RelationType.Challenges => "-.->",
                RelationType.Concedes   => "==>",
                _                       => "-->"
            };
            sb.AppendLine($"  {from} {arrow}|{edge.Relation}| {to}");
        }
        return sb.ToString();
    }

    public static string ToDot(IReadOnlyList<ArgumentNode> nodes, IReadOnlyList<ArgumentEdge> edges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph VibeWarsArguments {");
        sb.AppendLine("  rankdir=LR;");
        foreach (var node in nodes)
        {
            var label = node.ClaimText.Replace("\"", "'").Replace("\n", " ");
            if (label.Length > 60) label = label[..60] + "...";
            var id = "n" + node.Id.ToString("N")[..8];
            sb.AppendLine($"  {id} [label=\"{label}\" shape=box tooltip=\"{node.BotName}: {node.ClaimType}\"];");
        }
        var nodeIds = nodes.ToDictionary(n => n.Id, n => "n" + n.Id.ToString("N")[..8]);
        foreach (var edge in edges)
        {
            if (!nodeIds.TryGetValue(edge.FromId, out var from) || !nodeIds.TryGetValue(edge.ToId, out var to)) continue;
            sb.AppendLine($"  {from} -> {to} [label=\"{edge.Relation}\"];");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static (int TotalClaims, double RebuttalRate, Dictionary<string, int> ConcessionsByBot, ArgumentNode? MostChallenged)
        ComputeStats(IReadOnlyList<ArgumentNode> nodes, IReadOnlyList<ArgumentEdge> edges)
    {
        var totalClaims  = nodes.Count;
        var rebuttals    = edges.Count(e => e.Relation == RelationType.Challenges);
        var rebuttalRate = totalClaims == 0 ? 0.0 : (double)rebuttals / totalClaims;

        var concessionsByBot = nodes
            .Where(n => n.ClaimType == ClaimType.Concession)
            .GroupBy(n => n.BotName)
            .ToDictionary(g => g.Key, g => g.Count());

        var challengeCounts = edges
            .Where(e => e.Relation == RelationType.Challenges)
            .GroupBy(e => e.ToId)
            .ToDictionary(g => g.Key, g => g.Count());

        var mostChallengedId = challengeCounts.Count > 0
            ? challengeCounts.OrderByDescending(kv => kv.Value).First().Key
            : default;
        var mostChallenged   = mostChallengedId != default ? nodes.FirstOrDefault(n => n.Id == mostChallengedId) : null;

        return (totalClaims, rebuttalRate, concessionsByBot, mostChallenged);
    }
}
