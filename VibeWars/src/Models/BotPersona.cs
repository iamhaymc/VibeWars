namespace VibeWars.Models;

public enum PersonaArchetype
{
    Pragmatist,
    Idealist,
    DevilsAdvocate,
    DomainExpert,
    Empiricist,
    Ethicist,
    Custom
}

public record BotPersona(
    string Name,
    PersonaArchetype Archetype,
    string StyleDescription,
    string StrengthBias,
    string WeaknessBias
);
