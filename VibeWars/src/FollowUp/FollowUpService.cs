using System.Text;
using System.Text.Json;

namespace VibeWars.FollowUp;

public record FollowUpTopic(string Topic, string Rationale, string Difficulty);

public class FollowUpService
{
    /// <summary>
    /// Parses follow-up topics from a JSON response (handles markdown-wrapped JSON).
    /// Returns empty list on failure.
    /// </summary>
    public static List<FollowUpTopic> ParseFollowUps(string json)
    {
        try
        {
            var cleaned = json.Trim();
            if (cleaned.StartsWith("```"))
            {
                var firstNewline = cleaned.IndexOf('\n');
                if (firstNewline >= 0) cleaned = cleaned[(firstNewline + 1)..];
                var lastFence = cleaned.LastIndexOf("```");
                if (lastFence >= 0) cleaned = cleaned[..lastFence];
                cleaned = cleaned.Trim();
            }

            using var doc = JsonDocument.Parse(cleaned);
            var topics = new List<FollowUpTopic>();

            if (doc.RootElement.TryGetProperty("topics", out var topicsArray))
            {
                foreach (var item in topicsArray.EnumerateArray())
                {
                    var topic = item.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : "";
                    var rationale = item.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";
                    var difficulty = item.TryGetProperty("difficulty", out var d) ? d.GetString() ?? "medium" : "medium";
                    topics.Add(new FollowUpTopic(topic, rationale, difficulty));
                }
            }

            return topics;
        }
        catch
        {
            return new List<FollowUpTopic>();
        }
    }

    /// <summary>
    /// Builds the prompt used to generate follow-up topics from a synthesis.
    /// </summary>
    public static string BuildFollowUpPrompt(string synthesis)
    {
        return "Based on this debate synthesis:\n" + synthesis + "\n\n" +
               "Generate 3-5 follow-up debate topics that would naturally extend or deepen this conversation.\n" +
               "Respond with JSON in this exact format:\n" +
               "{\n" +
               "  \"topics\": [\n" +
               "    {\"topic\": \"...\", \"rationale\": \"...\", \"difficulty\": \"easy|medium|hard\"},\n" +
               "    ...\n" +
               "  ]\n" +
               "}";
    }

    /// <summary>
    /// Formats follow-up topics for console display.
    /// </summary>
    public static string FormatFollowUpDisplay(IList<FollowUpTopic> topics)
    {
        if (topics.Count == 0) return "No follow-up topics available.";

        var sb = new StringBuilder();
        sb.AppendLine("💡 Suggested next debates:");
        sb.AppendLine();
        for (int i = 0; i < topics.Count; i++)
        {
            var t = topics[i];
            sb.AppendLine($"  {i + 1}. {t.Topic}");
            sb.AppendLine($"     Rationale: {t.Rationale}");
            sb.AppendLine($"     Difficulty: {t.Difficulty}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sorts topics by how frequently the topic string appears in previousTopics.
    /// </summary>
    public static List<FollowUpTopic> SortByRecurrence(
        IList<FollowUpTopic> topics,
        IList<FollowUpTopic> previousTopics)
    {
        return topics
            .OrderByDescending(t => previousTopics.Count(p =>
                string.Equals(p.Topic, t.Topic, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(t => t.Topic)
            .ToList();
    }
}
