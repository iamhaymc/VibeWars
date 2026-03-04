namespace VibeWars.Models;

public enum ClaimType    { Assertion, Evidence, Rebuttal, Concession, Question, Synthesis }
public enum RelationType { Supports, Challenges, Extends, Answers, Concedes }

public record ArgumentNode(
    Guid Id,
    Guid SessionId,
    int Round,
    string BotName,
    string ClaimText,
    ClaimType ClaimType
);

public record ArgumentEdge(
    Guid FromId,
    Guid ToId,
    RelationType Relation
);
