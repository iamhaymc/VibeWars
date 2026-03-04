using System.Text;
using Newtonsoft.Json;
using VibeWars.Models;
using VibeWars.Notifications;

namespace VibeWars.Webhook;

public enum WebhookProvider
{
    Discord,
    Slack,
    Teams,
    Generic
}

public class WebhookConfig
{
    public string? WebhookUrl { get; set; }
    public WebhookProvider WebhookProvider { get; set; } = WebhookProvider.Generic;
    public bool WebhookOnComplete { get; set; } = false;
    public bool WebhookOnRound { get; set; } = false;
}

public class WebhookService
{
    private readonly HttpClient _httpClient;
    private readonly DiscordNotifier _discordNotifier;
    private readonly SlackNotifier _slackNotifier;

    // Private record so the $schema key serialises correctly with JsonProperty.
    private sealed record AdaptiveCardContent(
        [property: JsonProperty("type")]    string Type,
        [property: JsonProperty("$schema")] string Schema,
        [property: JsonProperty("version")] string Version,
        [property: JsonProperty("body")]    object[] Body);

    public WebhookService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _discordNotifier = new DiscordNotifier(httpClient);
        _slackNotifier = new SlackNotifier(httpClient);
    }

    public static WebhookConfig LoadFromEnvironment()
    {
        var config = new WebhookConfig
        {
            WebhookUrl = Environment.GetEnvironmentVariable("VIBEWARS_WEBHOOK_URL")
        };

        var providerStr = Environment.GetEnvironmentVariable("VIBEWARS_WEBHOOK_PROVIDER");
        if (Enum.TryParse<WebhookProvider>(providerStr, true, out var provider))
            config.WebhookProvider = provider;

        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_WEBHOOK_ON_COMPLETE"), "true", StringComparison.OrdinalIgnoreCase))
            config.WebhookOnComplete = true;
        if (string.Equals(Environment.GetEnvironmentVariable("VIBEWARS_WEBHOOK_ON_ROUND"), "true", StringComparison.OrdinalIgnoreCase))
            config.WebhookOnRound = true;

        return config;
    }

    private static void ValidateWebhookUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"[Webhook] Warning: webhook URL '{url}' does not use HTTPS. Sensitive content may be transmitted in plaintext.");
    }

    public async Task PostDebateSummaryAsync(
        DebateSession session,
        IReadOnlyList<MemoryEntry> entries,
        WebhookConfig webhookConfig,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(webhookConfig.WebhookUrl)) return;
        ValidateWebhookUrl(webhookConfig.WebhookUrl);

        // Delegate to dedicated notifiers for richer, platform-native payloads.
        if (webhookConfig.WebhookProvider == WebhookProvider.Discord)
        {
            await _discordNotifier.PostDebateSummaryAsync(webhookConfig.WebhookUrl, session, ct);
            return;
        }
        if (webhookConfig.WebhookProvider == WebhookProvider.Slack)
        {
            await _slackNotifier.PostDebateSummaryAsync(webhookConfig.WebhookUrl, session, ct);
            return;
        }

        try
        {
            var topic = session.Topic;
            var winner = session.OverallWinner;
            var synthesis = session.FinalSynthesis;

            string json = webhookConfig.WebhookProvider switch
            {
                WebhookProvider.Teams => JsonConvert.SerializeObject(new
                {
                    type = "message",
                    attachments = new[]
                    {
                        new
                        {
                            contentType = "application/vnd.microsoft.card.adaptive",
                            content = new AdaptiveCardContent(
                                Type: "AdaptiveCard",
                                Schema: "http://adaptivecards.io/schemas/adaptive-card.json",
                                Version: "1.2",
                                Body: new object[]
                                {
                                    new { type = "TextBlock", text = $"VibeWars: {topic}", weight = "Bolder" },
                                    new { type = "TextBlock", text = $"Winner: {winner}" }
                                })
                        }
                    }
                }),
                _ => JsonConvert.SerializeObject(new
                {
                    sessionId = session.SessionId.ToString(),
                    topic,
                    winner,
                    synthesis,
                    startedAt = session.StartedAt.ToString("O"),
                    endedAt = session.EndedAt.ToString("O")
                })
            };

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(webhookConfig.WebhookUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Webhook] Failed to post debate summary. Status: {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Webhook] Error posting debate summary: {ex.Message}");
        }
    }

    public async Task PostRoundSummaryAsync(
        int round,
        string roundWinner,
        string reasoning,
        WebhookConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.WebhookUrl)) return;
        ValidateWebhookUrl(config.WebhookUrl);

        try
        {
            string json = config.WebhookProvider switch
            {
                WebhookProvider.Discord => JsonConvert.SerializeObject(new
                {
                    embeds = new[]
                    {
                        new
                        {
                            title = $"⚔️ Round {round} Result",
                            color = 5793266,
                            fields = new object[]
                            {
                                new { name = "🏆 Round Winner", value = roundWinner, inline = true },
                                new { name = "📋 Reasoning",    value = reasoning.Length > 500 ? reasoning[..500] + "…" : reasoning, inline = false }
                            }
                        }
                    }
                }),
                WebhookProvider.Slack => JsonConvert.SerializeObject(new
                {
                    blocks = new object[]
                    {
                        new
                        {
                            type = "section",
                            fields = new object[]
                            {
                                new { type = "mrkdwn", text = $"*Round {round}*" },
                                new { type = "mrkdwn", text = $"*Winner:* 🏆 {roundWinner}" }
                            }
                        },
                        new
                        {
                            type = "context",
                            elements = new object[]
                            {
                                new { type = "mrkdwn", text = reasoning.Length > 300 ? reasoning[..300] + "…" : reasoning }
                            }
                        }
                    }
                }),
                WebhookProvider.Teams => JsonConvert.SerializeObject(new
                {
                    type = "message",
                    attachments = new[]
                    {
                        new
                        {
                            contentType = "application/vnd.microsoft.card.adaptive",
                            content = new AdaptiveCardContent(
                                Type: "AdaptiveCard",
                                Schema: "http://adaptivecards.io/schemas/adaptive-card.json",
                                Version: "1.2",
                                Body: new object[]
                                {
                                    new { type = "TextBlock", text = $"Round {round} Result", weight = "Bolder" },
                                    new { type = "TextBlock", text = $"Winner: {roundWinner}" }
                                })
                        }
                    }
                }),
                _ => JsonConvert.SerializeObject(new { round, winner = roundWinner, reasoning })
            };

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(config.WebhookUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"[Webhook] Failed to post round summary. Status: {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Webhook] Error posting round summary: {ex.Message}");
        }
    }

    public async Task<bool> TestWebhookAsync(WebhookConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.WebhookUrl)) return false;

        try
        {
            var json = JsonConvert.SerializeObject(new
            {
                test = true,
                source = "VibeWars",
                message = "Webhook test from VibeWars CLI"
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(config.WebhookUrl, content, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Webhook] Test failed: {ex.Message}");
            return false;
        }
    }
}
