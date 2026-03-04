namespace VibeWars.HumanPlayer;

/// <summary>
/// Reads human input for debate participation. Accepts an injectable TextReader for testing.
/// </summary>
public sealed class HumanInputReader
{
    private readonly TextReader _reader;

    public HumanInputReader(TextReader? reader = null) => _reader = reader ?? Console.In;

    /// <summary>
    /// Prompts the human for their argument. A blank entry returns <paramref name="fallback"/>.
    /// </summary>
    public string ReadArgument(string prompt, string fallback = "")
    {
        Console.Write(prompt);
        var input = _reader.ReadLine()?.Trim() ?? string.Empty;
        return string.IsNullOrEmpty(input) ? fallback : input;
    }

    /// <summary>
    /// Prompts the human for a judge verdict. Returns (winner, reasoning).
    /// </summary>
    public (string Winner, string Reasoning) ReadJudgeVerdict()
    {
        Console.Write("  Winner (Bot A / Bot B / Tie): ");
        var winner    = _reader.ReadLine()?.Trim() ?? "Tie";
        Console.Write("  Reasoning: ");
        var reasoning = _reader.ReadLine()?.Trim() ?? string.Empty;
        return (string.IsNullOrEmpty(winner) ? "Tie" : winner, reasoning);
    }
}
