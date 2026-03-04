using System.Text;

namespace VibeWars.Tournament;

public sealed class RoundRobinTournament
{
    /// <summary>
    /// Generates a complete round-robin schedule using the circle (Berger) method.
    /// Each round contains pairs so no contestant plays more than once per round.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TournamentMatch>> GenerateSchedule(
        IReadOnlyList<TournamentContestant> contestants)
    {
        var list = contestants.ToList();
        var n    = list.Count;
        TournamentContestant? bye = null;

        // Add a dummy "BYE" if odd number
        if (n % 2 != 0)
        {
            bye  = new TournamentContestant("BYE", "", "", "");
            list.Add(bye);
            n++;
        }

        var rounds  = new List<IReadOnlyList<TournamentMatch>>();
        var matchId = 1;

        // Circle method: fix first element, rotate rest
        for (var r = 0; r < n - 1; r++)
        {
            var round = new List<TournamentMatch>();
            for (var i = 0; i < n / 2; i++)
            {
                var a = list[i];
                var b = list[n - 1 - i];
                if (a != bye && b != bye)
                    round.Add(new TournamentMatch(matchId++, a, b));
            }
            rounds.Add(round);

            // Rotate all except the first
            var last = list[n - 1];
            for (var i = n - 1; i > 1; i--)
                list[i] = list[i - 1];
            list[1] = last;
        }

        return rounds;
    }

    public static string RenderResultsMatrix(
        IReadOnlyList<TournamentContestant> contestants,
        IReadOnlyList<TournamentResult> results)
    {
        var n   = contestants.Count;
        var idx = contestants.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i);
        var matrix = new string[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                matrix[i, j] = i == j ? "  — " : "    ";

        foreach (var r in results)
        {
            if (!idx.TryGetValue(r.Match.ContestantA.Name, out var ai)) continue;
            if (!idx.TryGetValue(r.Match.ContestantB.Name, out var bi)) continue;
            var aWon = r.Winner.Name == r.Match.ContestantA.Name;
            matrix[ai, bi] = aWon ? "  W " : "  L ";
            matrix[bi, ai] = aWon ? "  L " : "  W ";
        }

        var sb  = new StringBuilder();
        const int pad = 10;
        sb.Append("".PadLeft(pad));
        for (var j = 0; j < n; j++)
        {
            var header = contestants[j].Name.Length > pad - 2 ? contestants[j].Name[..(pad - 2)] : contestants[j].Name;
            sb.Append($" {header.PadRight(pad - 1)}");
        }
        sb.AppendLine();
        sb.AppendLine(new string('─', pad + n * pad));

        for (var i = 0; i < n; i++)
        {
            var rowLabel = contestants[i].Name.Length > pad - 2 ? contestants[i].Name[..(pad - 2)] : contestants[i].Name;
            sb.Append(rowLabel.PadRight(pad));
            for (var j = 0; j < n; j++)
                sb.Append(matrix[i, j].PadRight(pad));
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
