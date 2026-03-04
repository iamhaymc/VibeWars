namespace VibeWars.Tournament;

public record TournamentContestant(
    string Name,
    string Provider,
    string Model,
    string Persona,
    string Region = "us-east-1"
);
