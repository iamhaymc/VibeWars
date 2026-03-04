namespace VibeWars.Models;

public record DebateStrategy(
    string TacticName,
    string TargetWeakness,
    string ExecutionHint,
    double ConfidenceScore
);

public record StrategyRecord(
    string ContestantId,
    string TacticName,
    int UsedInRound,
    string SessionId,
    int RoundWon  // 1 = won, 0 = lost
);
