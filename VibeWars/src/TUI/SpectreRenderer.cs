using Spectre.Console;

namespace VibeWars.TUI;

/// <summary>
/// Renders debate output using Spectre.Console panels, tables, and styled text.
/// </summary>
public sealed class SpectreRenderer
{
    /// <summary>Render the startup banner with FigletText and a config table.</summary>
    public void PrintBanner(
        string botAModel, string botAProvider, string botAPersona,
        string botBModel, string botBProvider, string botBPersona,
        string judgeModel, string judgeProvider,
        int maxRounds, string memoryBackend, bool noMemory, string debateFormat)
    {
        AnsiConsole.Write(new FigletText("VibeWars").Color(Color.Red));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Bot[/]")
            .AddColumn("[bold]Model[/]")
            .AddColumn("[bold]Provider[/]")
            .AddColumn("[bold]Persona[/]");

        table.AddRow("[bold blue]Bot A[/]",   Markup.Escape(botAModel),   Markup.Escape(botAProvider),  Markup.Escape(botAPersona));
        table.AddRow("[bold green]Bot B[/]",  Markup.Escape(botBModel),   Markup.Escape(botBProvider),  Markup.Escape(botBPersona));
        table.AddRow("[bold yellow]Judge[/]", Markup.Escape(judgeModel),  Markup.Escape(judgeProvider), "-");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"  [dim]Max rounds: {maxRounds} | Format: {Markup.Escape(debateFormat)} | Memory: {(noMemory ? "disabled" : Markup.Escape(memoryBackend))}[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>Render a bot argument inside a styled panel.</summary>
    public void PrintBotMessage(string botName, string color, string text)
    {
        var panel = new Panel(Markup.Escape(text))
        {
            Header = new PanelHeader($"[bold {color}]{Markup.Escape(botName)}[/]"),
            Border = BoxBorder.Rounded,
            Expand = false,
        };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
    }

    /// <summary>Render the judge evaluation in a yellow panel.</summary>
    public void PrintJudgeVerdict(string winner, string reasoning, string? newIdeas)
    {
        var content = $"🏆 [bold]Winner:[/] {Markup.Escape(winner)}\n📋 {Markup.Escape(reasoning)}";
        if (!string.IsNullOrWhiteSpace(newIdeas))
            content += $"\n💡 [dim]New ideas:[/] {Markup.Escape(newIdeas)}";

        var panel = new Panel(content)
        {
            Header = new PanelHeader("[bold yellow]⚖ Judge[/]"),
            Border = BoxBorder.Rounded,
        };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
    }

    /// <summary>Render the final verdict panel.</summary>
    public void PrintFinalVerdict(string overallWinner, string? synthesis, int botAWins, int botBWins, int ties)
    {
        var winnerColor = overallWinner == "Bot A" ? "blue" : overallWinner == "Bot B" ? "green" : "dim";
        var content = $"[bold {winnerColor}]Overall Winner: {Markup.Escape(overallWinner)}[/]\n" +
                      $"[dim]Bot A: {botAWins} wins  |  Bot B: {botBWins} wins  |  Ties: {ties}[/]";
        if (!string.IsNullOrWhiteSpace(synthesis))
            content += $"\n\n{Markup.Escape(synthesis)}";

        var panel = new Panel(content)
        {
            Header = new PanelHeader("[bold]⚔ Final Verdict[/]"),
            Border = BoxBorder.Double,
            Expand = true,
        };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>Render a round header.</summary>
    public void PrintRoundHeader(int round, int maxRounds)
    {
        var rule = new Rule($"[bold]Round {round} of {maxRounds}[/]") { Style = Style.Parse("dim") };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(rule);
    }

    /// <summary>Render a "thinking..." status while waiting for API.</summary>
    public void PrintThinking(string botName)
    {
        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(botName)} is thinking…[/]");
    }

    /// <summary>Print a cost summary line.</summary>
    public void PrintCostSummary(string summary)
    {
        AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(summary)}[/]");
    }
}
