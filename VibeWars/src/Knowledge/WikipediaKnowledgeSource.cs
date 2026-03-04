using System.Net.Http.Json;
using System.Text.Json;

namespace VibeWars.Knowledge;

/// <summary>
/// Retrieves relevant passages from Wikipedia's REST API for grounding debate arguments in real facts.
/// Uses the Wikipedia search + extract endpoints to find and retrieve article summaries.
/// </summary>
public sealed class WikipediaKnowledgeSource : IKnowledgeSource, IDisposable
{
    private readonly HttpClient _http;

    public WikipediaKnowledgeSource()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://en.wikipedia.org/") };
        _http.DefaultRequestHeaders.Add("User-Agent", "VibeWars/1.0 (debate-research-tool)");
    }

    public async Task<IReadOnlyList<KnowledgePassage>> SearchAsync(string query, int topK = 3, CancellationToken ct = default)
    {
        try
        {
            var searchUrl = $"w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit={topK}&format=json";
            var searchResponse = await _http.GetStringAsync(searchUrl, ct);
            using var searchDoc = JsonDocument.Parse(searchResponse);
            var searchResults = searchDoc.RootElement
                .GetProperty("query").GetProperty("search")
                .EnumerateArray()
                .Select(e => e.GetProperty("title").GetString() ?? "")
                .Where(t => !string.IsNullOrEmpty(t))
                .Take(topK)
                .ToList();

            var passages = new List<KnowledgePassage>();
            foreach (var title in searchResults)
            {
                try
                {
                    var extractUrl = $"w/api.php?action=query&titles={Uri.EscapeDataString(title)}&prop=extracts&exintro=true&explaintext=true&exsentences=5&format=json";
                    var extractResponse = await _http.GetStringAsync(extractUrl, ct);
                    using var extractDoc = JsonDocument.Parse(extractResponse);
                    var pages = extractDoc.RootElement.GetProperty("query").GetProperty("pages");
                    foreach (var page in pages.EnumerateObject())
                    {
                        if (page.Value.TryGetProperty("extract", out var extract))
                        {
                            var text = extract.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(text))
                                passages.Add(new KnowledgePassage(title, text, $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(title)}"));
                        }
                    }
                }
                catch { /* skip individual article failures */ }
            }
            return passages;
        }
        catch
        {
            return [];
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Formats retrieved knowledge passages for injection into bot prompts.</summary>
public static class KnowledgeFormatter
{
    public static string FormatForPrompt(IReadOnlyList<KnowledgePassage> passages)
    {
        if (passages.Count == 0) return "";
        var lines = passages.Select((p, i) => $"[{i + 1}] {p.Title}: {p.Content}");
        return $"AVAILABLE EVIDENCE (use citations where applicable):\n{string.Join("\n", lines)}";
    }
}
