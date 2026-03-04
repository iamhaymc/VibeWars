using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace VibeWars.Resilience;

public static class ResilienceHelper
{
    public static ResiliencePipeline<T> BuildChatPipeline<T>(int retryMax, int baseDelayMs)
    {
        return new ResiliencePipelineBuilder<T>()
            .AddRetry(new RetryStrategyOptions<T>
            {
                MaxRetryAttempts = retryMax,
                Delay = TimeSpan.FromMilliseconds(baseDelayMs),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true, // Randomizes retry delays to prevent thundering herd during outages
                ShouldHandle = new PredicateBuilder<T>()
                    .Handle<HttpRequestException>()
                    .Handle<InvalidOperationException>(),
                OnRetry = args =>
                {
                    Console.Error.WriteLine($"[Retry {args.AttemptNumber}] {args.Outcome.Exception?.Message} — waiting {args.RetryDelay.TotalSeconds:F1} s…");
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<T>
            {
                FailureRatio = 0.6,
                MinimumThroughput = 3,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(60),
                ShouldHandle = new PredicateBuilder<T>()
                    .Handle<HttpRequestException>()
                    .Handle<InvalidOperationException>(),
                OnOpened = static _ =>
                {
                    Console.Error.WriteLine("API circuit breaker open — debate paused for 60 s");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }
}
