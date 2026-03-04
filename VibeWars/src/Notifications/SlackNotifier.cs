using Newtonsoft.Json;
using System.Text;
using VibeWars.Models;

namespace VibeWars.Notifications;

/// <summary>
/// Sends debate notifications to a Slack channel via an Incoming Webhook URL.
/// Payloads use the Slack Block Kit format for rich, structured messages.
/// </summary>
public class SlackNotifier
{
    private readonly HttpClient _httpClient;

    public SlackNotifier(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Posts a full debate summary to Slack after the debate completes.
    /// </summary>
    public async Task PostDebateSummaryAsync(
        string webhookUrl,
        DebateSession session,
        CancellationToken ct = default)
    {
        var topic = session.Topic;
        var winner = session.OverallWinner;
        var synthesis = session.FinalSynthesis;
        var truncatedSynthesis = synthesis.Length > 600 ? synthesis[..600] + "…" : synthesis;

        var payload = new
        {
            blocks = new object[]
            {
                new
                {
                    type = "header",
                    text = new { type = "plain_text", text = $"⚔️ VibeWars Debate Complete", emoji = true }
                },
                new
                {
                    type = "section",
                    fields = new object[]
                    {
                        new { type = "mrkdwn", text = $"*Topic*\n{topic}" },
                        new { type = "mrkdwn", text = $"*Winner*\n🏆 {winner}" }
                    }
                },
                new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = $"*Final Synthesis*\n{truncatedSynthesis}" }
                },
                new
                {
                    type = "context",
                    elements = new object[]
                    {
                        new { type = "mrkdwn", text = $"Session `{session.SessionId}` · {session.StartedAt:yyyy-MM-dd HH:mm} UTC" }
                    }
                },
                new { type = "divider" }
            }
        };

        await PostAsync(webhookUrl, payload, ct);
    }

    /// <summary>
    /// Posts a compact round result to Slack after each debate round.
    /// </summary>
    public async Task PostRoundResultAsync(
        string webhookUrl,
        int round,
        string winner,
        string reasoning,
        CancellationToken ct = default)
    {
        var truncatedReasoning = reasoning.Length > 300 ? reasoning[..300] + "…" : reasoning;

        var payload = new
        {
            blocks = new object[]
            {
                new
                {
                    type = "section",
                    fields = new object[]
                    {
                        new { type = "mrkdwn", text = $"*Round {round}*" },
                        new { type = "mrkdwn", text = $"*Winner:* 🏆 {winner}" }
                    }
                },
                new
                {
                    type = "context",
                    elements = new object[]
                    {
                        new { type = "mrkdwn", text = truncatedReasoning }
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
                Console.Error.WriteLine($"[Slack] Failed to post notification. Status: {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Slack] Error posting notification: {ex.Message}");
        }
    }
}
