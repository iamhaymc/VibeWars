namespace VibeWars.FactChecker;

public record FactClaim(string Claim, string Confidence, string Note);

public record FactCheckResult(IReadOnlyList<FactClaim> Claims);
