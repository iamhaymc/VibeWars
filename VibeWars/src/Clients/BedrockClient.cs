using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using VibeWars.Models;
using VibeWarsTokenUsage = VibeWars.Models.TokenUsage;

namespace VibeWars.Clients;

/// <summary>
/// Chat client that calls AWS Bedrock using the Converse API,
/// which provides a unified interface across all supported models.
/// </summary>
public sealed class BedrockClient : IChatClient
{
    private readonly AmazonBedrockRuntimeClient _client;

    public string ProviderName => "Bedrock";
    public string ModelId { get; }

    /// <param name="modelId">Bedrock model ID, e.g. "amazon.nova-lite-v1:0" or "anthropic.claude-3-5-haiku-20241022-v1:0".</param>
    /// <param name="region">AWS region where Bedrock is enabled, defaults to us-east-1.</param>
    public BedrockClient(string modelId = "amazon.nova-lite-v1:0", string region = "us-east-1")
    {
        ModelId = modelId;
        _client = new AmazonBedrockRuntimeClient(RegionEndpoint.GetBySystemName(region));
    }

    public async Task<(string Reply, VibeWarsTokenUsage Usage)> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
    {
        var messages = history
            .Select(m => new Message
            {
                Role = m.Role == "user" ? ConversationRole.User : ConversationRole.Assistant,
                Content = [new ContentBlock { Text = m.Content }]
            })
            .ToList();

        var request = new ConverseRequest
        {
            ModelId = ModelId,
            System = [new SystemContentBlock { Text = systemPrompt }],
            Messages = messages,
            InferenceConfig = new InferenceConfiguration { MaxTokens = 2048 }
        };

        var response = await _client.ConverseAsync(request, ct).ConfigureAwait(false);

        var content = response.Output?.Message?.Content?.FirstOrDefault()?.Text
            ?? throw new InvalidOperationException("No content in Bedrock Converse response");

        var inputTokens  = response.Usage?.InputTokens  ?? 0;
        var outputTokens = response.Usage?.OutputTokens ?? 0;
        var usage = new VibeWarsTokenUsage(inputTokens, outputTokens, inputTokens + outputTokens, null);

        return (content, usage);
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = history
            .Select(m => new Message
            {
                Role = m.Role == "user" ? ConversationRole.User : ConversationRole.Assistant,
                Content = [new ContentBlock { Text = m.Content }]
            })
            .ToList();

        var request = new ConverseStreamRequest
        {
            ModelId = ModelId,
            System = [new SystemContentBlock { Text = systemPrompt }],
            Messages = messages,
            InferenceConfig = new InferenceConfiguration { MaxTokens = 2048 }
        };

        var response = await _client.ConverseStreamAsync(request, ct).ConfigureAwait(false);

        await foreach (var streamEvent in response.Stream.WithCancellation(ct))
        {
            if (streamEvent is ContentBlockDeltaEvent deltaEvent)
            {
                var text = deltaEvent.Delta?.Text;
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
        }
    }

    public void Dispose() => _client.Dispose();
}
