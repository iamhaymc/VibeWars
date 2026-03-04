using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace VibeWars.Clients;

/// <summary>
/// Embedding client using AWS Bedrock Titan Embeddings v2.
/// </summary>
public sealed class BedrockEmbeddingClient : IEmbeddingClient
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;

    public BedrockEmbeddingClient(string region = "us-east-1", string modelId = "amazon.titan-embed-text-v2:0")
    {
        _modelId = modelId;
        _client  = new AmazonBedrockRuntimeClient(RegionEndpoint.GetBySystemName(region));
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var body    = JsonConvert.SerializeObject(new { inputText = text });
        var request = new InvokeModelRequest
        {
            ModelId     = _modelId,
            ContentType = "application/json",
            Accept      = "application/json",
            Body        = new MemoryStream(Encoding.UTF8.GetBytes(body)),
        };
        var response    = await _client.InvokeModelAsync(request, ct);
        using var reader = new StreamReader(response.Body);
        var json        = await reader.ReadToEndAsync(ct);
        var doc         = JObject.Parse(json);
        var embedding   = doc["embedding"] as JArray;
        return embedding?.Select(e => e.Value<float>()).ToArray() ?? Array.Empty<float>();
    }

    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var tasks = texts.Select(t => EmbedAsync(t, ct));
        return await Task.WhenAll(tasks);
    }

    public void Dispose() => _client.Dispose();
}
