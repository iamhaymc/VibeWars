using Newtonsoft.Json;
using System.Text;
using VibeWars.Models;

namespace VibeWars.Notifications;

/// <summary>
/// Sends debate notifications to a Discord channel via an Incoming Webhook URL.
/// Payloads use the Discord embed format for rich, structured messages.
/// </summary>
public class DiscordNotifier
{
    private readonly HttpClient _httpClient;

    // Discord embed color constants (decimal values)
    private const int ColorGold   = 0xFFD700; // winner announcement
    private const int ColorBlue   = 0x5865F2; // round result

    public DiscordNotifier(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Posts a full debate summary to Discord after the debate completes.
    /// </summary>
    public async Task PostDebateSummaryAsync(
        string webhookUrl,
        DebateSession session,
        CancellationToken ct = default)
    {
        var topic = session.Topic;
        var winner = session.OverallWinner;
        var synthesis = session.FinalSynthesis;
        var truncatedSynthesis = synthesis.Length > 1000 ? synthesis[..1000] + "…" : synthesis;

        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"⚔️ VibeWars Debate: {topic}",
                    color = ColorGold,
                    fields = new object[]
                    {
                        new { name = "🏆 Winner",         value = winner,             inline = true },
                        new { name = "📅 Date",           value = session.StartedAt.ToString("yyyy-MM-dd HH:mm") + " UTC", inline = true },
                        new { name = "📋 Final Synthesis", value = truncatedSynthesis, inline = false }
                    },
                    footer = new { text = $"Session {session.SessionId}" }
                }
            }
        };

        await PostAsync(webhookUrl, payload, ct);
    }

    /// <summary>
    /// Posts a compact round result to Discord after each debate round.
    /// </summary>
    public async Task PostRoundResultAsync(
        string webhookUrl,
        int round,
        string winner,
        string reasoning,
        CancellationToken ct = default)
    {
        var truncatedReasoning = reasoning.Length > 500 ? reasoning[..500] + "…" : reasoning;

        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"⚔️ Round {round} Result",
                    color = ColorBlue,
                    fields = new object[]
                    {
                        new { name = "🏆 Round Winner", value = winner,            inline = true },
                        new { name = "📋 Reasoning",    value = truncatedReasoning, inline = false }
                    }
                }
            }
        };

        await PostAsync(webhookUrl, payload, ct);
    }

    private async Task PostAsync(string webhookUrl, object payload, CancellationToken ct)
    {
        try
        {
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(webhookUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Discord] Failed to post notification. Status: {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Discord] Error posting notification: {ex.Message}");
        }
    }
}
