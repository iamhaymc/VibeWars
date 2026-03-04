namespace VibeWars.Models;

/// <summary>A single chat message in a conversation.</summary>
public record ChatMessage(string Role, string Content);

/// <summary>Result returned by the judge after evaluating a debate round.</summary>
public record JudgeVerdict(string Winner, string Reasoning, string NewIdeas);

/// <summary>A single unit of memory from a past debate.</summary>
public record MemoryEntry(
    Guid Id,
    string BotName,
    string Topic,
    int Round,
    string Role,
    string Content,
    DateTimeOffset Timestamp,
    string[] Tags
);

/// <summary>Groups all MemoryEntry rows for one debate run.</summary>
public record DebateSession(
    Guid SessionId,
    string Topic,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string OverallWinner,
    string FinalSynthesis,
    string Format = "Freeform",
    int TotalTokens = 0,
    decimal? EstimatedCostUsd = null,
    string Complexity = "Standard"
);
