namespace VibeWars.Models;

public record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens, decimal? EstimatedCostUsd)
{
    public static readonly TokenUsage Empty = new(0, 0, 0, null);
}

public sealed class CostAccumulator
{
    public int TotalPromptTokens { get; private set; }
    public int TotalCompletionTokens { get; private set; }
    public int TotalTokens { get; private set; }
    public decimal? TotalEstimatedCostUsd { get; private set; }

    public void Add(TokenUsage usage)
    {
        TotalPromptTokens += usage.PromptTokens;
        TotalCompletionTokens += usage.CompletionTokens;
        TotalTokens += usage.TotalTokens;
        if (usage.EstimatedCostUsd.HasValue)
            TotalEstimatedCostUsd = (TotalEstimatedCostUsd ?? 0m) + usage.EstimatedCostUsd.Value;
    }

    public bool ExceedsBudget(decimal? maxCostUsd)
    {
        if (!maxCostUsd.HasValue || !TotalEstimatedCostUsd.HasValue) return false;
        return TotalEstimatedCostUsd.Value >= maxCostUsd.Value;
    }

    public string FormatSummary() =>
        TotalEstimatedCostUsd.HasValue
            ? $"📊 Session cost: ~${TotalEstimatedCostUsd.Value:F4}  (prompt: {TotalPromptTokens:N0} | completion: {TotalCompletionTokens:N0} | total: {TotalTokens:N0} tokens)"
            : $"📊 Session tokens: prompt: {TotalPromptTokens:N0} | completion: {TotalCompletionTokens:N0} | total: {TotalTokens:N0}";
}
