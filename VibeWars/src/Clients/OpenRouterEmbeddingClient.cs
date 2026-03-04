using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Text;

namespace VibeWars.Clients;

/// <summary>
/// Embedding client calling the OpenRouter/OpenAI-compatible embeddings endpoint.
/// </summary>
public sealed class OpenRouterEmbeddingClient : IEmbeddingClient
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
    private readonly string _model;

    public OpenRouterEmbeddingClient(string apiKey, string model = "openai/text-embedding-3-small")
    {
        _model = model;
        _http  = new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results[0];
    }

    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var inputList = texts.ToList();
        var request   = new EmbedRequest(_model, inputList);
        var json      = JsonConvert.SerializeObject(request, JsonSettings);
        var content   = new StringContent(json, Encoding.UTF8, "application/json");
        var response  = await _http.PostAsync("embeddings", content, ct);
        var body      = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter embedding error {(int)response.StatusCode}: {body}");

        var parsed = JsonConvert.DeserializeObject<EmbedResponse>(body, JsonSettings)
            ?? throw new InvalidOperationException("Empty embedding response");

        return parsed.Data.Select(d => d.Embedding).ToArray();
    }

    public void Dispose() => _http.Dispose();

    private record EmbedRequest(
        [property: JsonProperty("model")]  string Model,
        [property: JsonProperty("input")]  List<string> Input);

    private record EmbedData(
        [property: JsonProperty("embedding")] float[] Embedding);

    private record EmbedResponse(
        [property: JsonProperty("data")] List<EmbedData> Data);
}
