using System.Text;
using VibeWars.Elo;

namespace VibeWars.Tournament;

public record SwissStanding(
    TournamentContestant Contestant,
    int Points,
    int Wins,
    int Draws,
    int Losses,
    double BuchholzScore,
    double EloRating
);

public sealed class SwissTournament
{
    public static int TotalRounds(int contestantCount)
        => contestantCount <= 1 ? 1 : (int)Math.Ceiling(Math.Log2(contestantCount)) + 1;

    /// <summary>
    /// Generates Swiss pairings using a simplified Monrad system:
    /// group contestants by current score, pair within groups, avoid rematches.
    /// </summary>
    public static IReadOnlyList<TournamentMatch> GenerateSwissPairings(
        IReadOnlyList<TournamentContestant> contestants,
        IReadOnlyList<TournamentResult> pastResults,
        int round)
    {
        // Track who has played whom already
        var played = new HashSet<(string, string)>();
        foreach (var r in pastResults)
        {
            var a = r.Match.ContestantA.Name;
            var b = r.Match.ContestantB.Name;
            played.Add((a, b));
            played.Add((b, a));
        }

        // Compute current scores
        var scores = contestants.ToDictionary(c => c.Name, _ => 0);
        foreach (var r in pastResults)
        {
            if (r.WinnerScore == r.LoserScore)
            {
                // Draw
                scores[r.Match.ContestantA.Name] += 1;
                scores[r.Match.ContestantB.Name] += 1;
            }
            else
            {
                scores[r.Winner.Name] += 3;
            }
        }

        // Sort by score descending
        var ordered = contestants.OrderByDescending(c => scores[c.Name]).ToList();
        var unpaired = new List<TournamentContestant>(ordered);
        var matches  = new List<TournamentMatch>();
        var matchId  = (pastResults.Count > 0 ? pastResults.Max(r => r.Match.MatchId) : 0) + 1;

        while (unpaired.Count >= 2)
        {
            var first = unpaired[0];
            unpaired.RemoveAt(0);

            // Find the highest-scored opponent that first hasn't played
            var opponentIdx = unpaired.FindIndex(c => !played.Contains((first.Name, c.Name)));
            if (opponentIdx < 0) opponentIdx = 0; // fallback: allow rematch

            var opponent = unpaired[opponentIdx];
            unpaired.RemoveAt(opponentIdx);
            matches.Add(new TournamentMatch(matchId++, first, opponent));
        }

        return matches;
    }

    public static IReadOnlyList<SwissStanding> ComputeStandings(
        IReadOnlyList<TournamentContestant> contestants,
        IReadOnlyList<TournamentResult> results)
    {
        var points  = contestants.ToDictionary(c => c.Name, _ => 0);
        var wins    = contestants.ToDictionary(c => c.Name, _ => 0);
        var draws   = contestants.ToDictionary(c => c.Name, _ => 0);
        var losses  = contestants.ToDictionary(c => c.Name, _ => 0);

        foreach (var r in results)
        {
            if (r.WinnerScore == r.LoserScore)
            {
                points[r.Match.ContestantA.Name] += 1;
                points[r.Match.ContestantB.Name] += 1;
                draws[r.Match.ContestantA.Name]++;
                draws[r.Match.ContestantB.Name]++;
            }
            else
            {
                points[r.Winner.Name] += 3;
                wins[r.Winner.Name]++;
                losses[r.Loser.Name]++;
            }
        }

        // Buchholz: sum of opponents' scores
        var buchholz = contestants.ToDictionary(c => c.Name, c =>
        {
            var opponentNames = results
                .Where(r => r.Match.ContestantA.Name == c.Name || r.Match.ContestantB.Name == c.Name)
                .Select(r => r.Match.ContestantA.Name == c.Name ? r.Match.ContestantB.Name : r.Match.ContestantA.Name);
            return opponentNames.Sum(name => points.TryGetValue(name, out var p) ? (double)p : 0.0);
        });

        return contestants
            .Select(c => new SwissStanding(
                c,
                points[c.Name],
                wins[c.Name],
                draws[c.Name],
                losses[c.Name],
                buchholz[c.Name],
                1200.0))
            .OrderByDescending(s => s.Points)
            .ThenByDescending(s => s.BuchholzScore)
            .ToList();
    }

    public static string RenderSwissStandings(IReadOnlyList<SwissStanding> standings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔════╦══════════════════╦═══════╦═════════╦══════════╦════════╗");
        sb.AppendLine("║ #  ║ Contestant       ║ Score ║ W/D/L   ║ Buchholz ║ ELO    ║");
        sb.AppendLine("╠════╬══════════════════╬═══════╬═════════╬══════════╬════════╣");
        for (var i = 0; i < standings.Count; i++)
        {
            var s    = standings[i];
            var name = s.Contestant.Name.Length > 16 ? s.Contestant.Name[..16] : s.Contestant.Name;
            var wdl  = $"{s.Wins}/{s.Draws}/{s.Losses}";
            sb.AppendLine($"║ {i + 1,-2} ║ {name,-16} ║ {s.Points,-5} ║ {wdl,-7} ║ {s.BuchholzScore,-8:F1} ║ {s.EloRating,-6:F0} ║");
        }
        sb.AppendLine("╚════╩══════════════════╩═══════╩═════════╩══════════╩════════╝");
        return sb.ToString();
    }
}
