namespace VibeWars.Tournament;

public record TournamentMatch(int MatchId, TournamentContestant ContestantA, TournamentContestant ContestantB);
public record TournamentResult(TournamentMatch Match, TournamentContestant Winner, TournamentContestant Loser, int WinnerScore, int LoserScore);

public sealed class TournamentBracket
{
    private readonly List<TournamentContestant> _contestants;
    public string TournamentId { get; } = Guid.NewGuid().ToString("N")[..8];

    public TournamentBracket(IEnumerable<TournamentContestant> contestants)
        => _contestants = contestants.ToList();

    /// <summary>
    /// Generates rounds of a single-elimination bracket.
    /// Returns a list of rounds, each containing a list of matches.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TournamentMatch>> GenerateRounds()
    {
        var rounds = new List<IReadOnlyList<TournamentMatch>>();
        var currentContestants = new List<TournamentContestant>(_contestants);
        
        // Pad to next power of 2 with BYEs (represented by null, but we skip those)
        var n = currentContestants.Count;
        var matchId = 1;
        var round = new List<TournamentMatch>();
        
        for (var i = 0; i + 1 < n; i += 2)
            round.Add(new TournamentMatch(matchId++, currentContestants[i], currentContestants[i + 1]));
        
        // If odd number, last contestant gets a bye (skip for now)
        rounds.Add(round);
        return rounds;
    }

    /// <summary>
    /// Given match results, determine who advances to the next round.
    /// </summary>
    public IReadOnlyList<TournamentContestant> GetWinners(IReadOnlyList<TournamentResult> results)
        => results.Select(r => r.Winner).ToList();

    /// <summary>
    /// Render a bracket visualization using box-drawing characters.
    /// </summary>
    public static string RenderBracket(
        IReadOnlyList<TournamentContestant> contestants,
        IReadOnlyList<TournamentResult>? results = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════╗");
        sb.AppendLine("║         VibeWars Tournament          ║");
        sb.AppendLine("╠══════════════════════════════════════╣");
        
        var resultMap = results?.ToDictionary(
            r => (r.Match.ContestantA.Name, r.Match.ContestantB.Name),
            r => r.Winner.Name) ?? new();

        for (var i = 0; i + 1 < contestants.Count; i += 2)
        {
            var a = contestants[i].Name;
            var b = contestants[i + 1].Name;
            var key = (a, b);
            var winner = resultMap.TryGetValue(key, out var w)
                ? $" → {(w.Length > 5 ? w[..5] : w)}"
                : "";
            var aShort = a.Length > 12 ? a[..12] : a;
            var bShort = b.Length > 12 ? b[..12] : b;
            sb.AppendLine($"║  {aShort,-12} vs {bShort,-12}{winner,-8}║");
        }
        
        sb.AppendLine("╚══════════════════════════════════════╝");
        return sb.ToString();
    }
}
