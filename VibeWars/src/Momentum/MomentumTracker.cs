namespace VibeWars.Momentum;

public enum MomentumEventType { Comeback, Streak, MomentumShift, Upset, ClutchRound, Blowout }

public record MomentumEvent(MomentumEventType Type, int Round, string BotName, string Description);

/// <summary>
/// Tracks round-over-round changes in verdicts, audience support, and argument
/// scores to detect dramatic momentum shifts, comebacks, and decisive breaks.
/// </summary>
public sealed class MomentumTracker
{
    private readonly List<MomentumEvent> _events = [];
    private readonly List<string> _roundWinners = [];
    private readonly List<(int SupportA, int SupportB)> _audienceHistory = [];

    public IReadOnlyList<MomentumEvent> Events => _events;

    public void RecordRound(int round, string winner, int? audienceSupportA = null, int? audienceSupportB = null)
    {
        _roundWinners.Add(winner);
        if (audienceSupportA.HasValue && audienceSupportB.HasValue)
            _audienceHistory.Add((audienceSupportA.Value, audienceSupportB.Value));

        DetectEvents(round, winner);
    }

    private void DetectEvents(int round, string winner)
    {
        // Streak detection: 3+ consecutive wins by the same bot
        if (_roundWinners.Count >= 3)
        {
            var lastThree = _roundWinners.TakeLast(3).ToList();
            if (lastThree.All(w => w == lastThree[0]) && lastThree[0] != "Tie")
                _events.Add(new MomentumEvent(MomentumEventType.Streak, round, lastThree[0],
                    $"{lastThree[0]} is on a {ConsecutiveWins(lastThree[0])}-round winning streak!"));
        }

        // Comeback detection: trailing by 2+ rounds, then winning current
        if (_roundWinners.Count >= 3 && winner != "Tie")
        {
            var botAWins = _roundWinners.SkipLast(1).Count(w => w.Contains("Bot A", StringComparison.OrdinalIgnoreCase));
            var botBWins = _roundWinners.SkipLast(1).Count(w => w.Contains("Bot B", StringComparison.OrdinalIgnoreCase));
            if (winner.Contains("Bot A", StringComparison.OrdinalIgnoreCase) && botBWins - botAWins >= 2)
                _events.Add(new MomentumEvent(MomentumEventType.Comeback, round, "Bot A",
                    "Bot A mounts a comeback after trailing by 2+ rounds!"));
            else if (winner.Contains("Bot B", StringComparison.OrdinalIgnoreCase) && botAWins - botBWins >= 2)
                _events.Add(new MomentumEvent(MomentumEventType.Comeback, round, "Bot B",
                    "Bot B mounts a comeback after trailing by 2+ rounds!"));
        }

        // Momentum shift: audience flips from favoring one bot to the other
        if (_audienceHistory.Count >= 2)
        {
            var prev = _audienceHistory[^2];
            var curr = _audienceHistory[^1];
            if (prev.SupportA > prev.SupportB && curr.SupportB > curr.SupportA)
                _events.Add(new MomentumEvent(MomentumEventType.MomentumShift, round, "Bot B",
                    "The audience has flipped! Bot B now leads in audience support."));
            else if (prev.SupportB > prev.SupportA && curr.SupportA > curr.SupportB)
                _events.Add(new MomentumEvent(MomentumEventType.MomentumShift, round, "Bot A",
                    "The audience has flipped! Bot A now leads in audience support."));
        }
    }

    /// <summary>Detect upset if the bot with lower ELO is leading.</summary>
    public void CheckUpset(int round, double eloA, double eloB, int winsA, int winsB)
    {
        if (eloA < eloB - 100 && winsA > winsB)
            _events.Add(new MomentumEvent(MomentumEventType.Upset, round, "Bot A",
                "Upset alert! The lower-rated Bot A is winning."));
        else if (eloB < eloA - 100 && winsB > winsA)
            _events.Add(new MomentumEvent(MomentumEventType.Upset, round, "Bot B",
                "Upset alert! The lower-rated Bot B is winning."));
    }

    /// <summary>Detect clutch round (final round is decisive).</summary>
    public void CheckClutchRound(int round, int maxRounds, int winsA, int winsB)
    {
        if (round == maxRounds && Math.Abs(winsA - winsB) <= 1)
            _events.Add(new MomentumEvent(MomentumEventType.ClutchRound, round, "",
                "Clutch round! Everything comes down to this final exchange."));
    }

    /// <summary>Detect blowout (one bot winning every round).</summary>
    public void CheckBlowout(int round, int winsA, int winsB)
    {
        if (round >= 3 && (winsA == 0 || winsB == 0))
        {
            var dominant = winsA > 0 ? "Bot A" : "Bot B";
            _events.Add(new MomentumEvent(MomentumEventType.Blowout, round, dominant,
                $"{dominant} is dominating — a clean sweep so far."));
        }
    }

    private int ConsecutiveWins(string bot)
    {
        var count = 0;
        for (var i = _roundWinners.Count - 1; i >= 0; i--)
        {
            if (_roundWinners[i] == bot) count++;
            else break;
        }
        return count;
    }

    public IReadOnlyList<MomentumEvent> GetEventsForRound(int round)
        => _events.Where(e => e.Round == round).ToList();

    public string RenderMomentumBar(int winsA, int winsB, int maxRounds)
    {
        const int barWidth = 30;
        var filledA = (int)Math.Round(barWidth * (double)winsA / Math.Max(1, maxRounds));
        filledA = Math.Clamp(filledA, 0, barWidth);
        var filledB = Math.Clamp((int)Math.Round(barWidth * (double)winsB / Math.Max(1, maxRounds)), 0, barWidth - filledA);
        var empty = barWidth - filledA - filledB;
        return $"[Bot A] {new string('=', filledA)}{new string('.', empty)}{new string('=', filledB)} [Bot B]";
    }
}
