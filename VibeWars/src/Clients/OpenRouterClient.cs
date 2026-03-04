using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Text;
using VibeWars.Models;

namespace VibeWars.Clients;

/// <summary>
/// Chat client that calls the OpenRouter API using the OpenAI-compatible chat completions endpoint.
/// </summary>
public sealed class OpenRouterClient : IChatClient
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new SnakeCaseNamingStrategy()
        },
        NullValueHandling = NullValueHandling.Ignore
    };

    private readonly HttpClient _http;

    public string ProviderName => "OpenRouter";
    public string ModelId { get; }

    public OpenRouterClient(string apiKey, string modelId = "openai/gpt-4o-mini")
    {
        ModelId = modelId;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<(string Reply, TokenUsage Usage)> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
    {
        var messages = new List<OpenRouterMessage>
        {
            new("system", systemPrompt)
        };

        foreach (var msg in history)
            messages.Add(new OpenRouterMessage(msg.Role, msg.Content));

        var request = new OpenRouterRequest(ModelId, messages);
        var json = JsonConvert.SerializeObject(request, JsonSettings);
        var requestContent = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("chat/completions", requestContent, ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter error {(int)response.StatusCode}: {body}");

        var parsed = JsonConvert.DeserializeObject<OpenRouterResponse>(body, JsonSettings)
            ?? throw new InvalidOperationException("Empty response from OpenRouter");

        var content = parsed.Choices?.FirstOrDefault()?.Message?.Content
            ?? throw new InvalidOperationException("No content in OpenRouter response");

        var u = parsed.Usage;
        var usage = u is not null
            ? new TokenUsage(u.PromptTokens, u.CompletionTokens, u.TotalTokens, null)
            : TokenUsage.Empty;

        return (content, usage);
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<OpenRouterMessage> { new("system", systemPrompt) };
        foreach (var msg in history)
            messages.Add(new OpenRouterMessage(msg.Role, msg.Content));

        var request = new OpenRouterStreamRequest(ModelId, messages, true);
        var json = JsonConvert.SerializeObject(request, JsonSettings);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = response.IsSuccessStatusCode ? string.Empty : await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter error {(int)response.StatusCode}: {body}");

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..];
            if (data == "[DONE]") break;

            string? chunk = null;
            try
            {
                var doc = JObject.Parse(data);
                var choices = doc["choices"] as JArray;
                if (choices != null && choices.Count > 0)
                {
                    var delta = choices[0]["delta"];
                    if (delta != null && delta["content"] != null)
                        chunk = delta["content"].Value<string>();
                }
            }
            catch { /* skip malformed chunks */ }

            if (!string.IsNullOrEmpty(chunk))
                yield return chunk;
        }
    }

    public void Dispose() => _http.Dispose();

    // ── Internal serialization models ───────────────────────────────────────────

    private record OpenRouterRequest(
        [property: JsonProperty("model")] string Model,
        [property: JsonProperty("messages")] List<OpenRouterMessage> Messages
    );

    private record OpenRouterStreamRequest(
        [property: JsonProperty("model")] string Model,
        [property: JsonProperty("messages")] List<OpenRouterMessage> Messages,
        [property: JsonProperty("stream")] bool Stream
    );

    private record OpenRouterMessage(
        [property: JsonProperty("role")] string Role,
        [property: JsonProperty("content")] string Content
    );

    private record OpenRouterUsage(
        [property: JsonProperty("prompt_tokens")] int PromptTokens,
        [property: JsonProperty("completion_tokens")] int CompletionTokens,
        [property: JsonProperty("total_tokens")] int TotalTokens
    );

    private record OpenRouterResponse(
        [property: JsonProperty("choices")] List<OpenRouterChoice>? Choices,
        [property: JsonProperty("usage")] OpenRouterUsage? Usage
    );

    private record OpenRouterChoice(
        [property: JsonProperty("message")] OpenRouterMessage? Message
    );
}
