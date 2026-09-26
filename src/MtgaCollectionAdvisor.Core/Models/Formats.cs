namespace MtgaCollectionAdvisor.Core.Models;

/// <summary>
/// A constructed format the advisor can rank decks for. Legality comes from Scryfall, but
/// results are restricted to cards that actually exist on Arena. <paramref name="MinimumDeckSize"/>
/// counts the commander: a list shorter than that is a draft, and would rank as cheap.
/// </summary>
public sealed record FormatDefinition(
    string Key,
    string DisplayName,
    string ScryfallLegalityKey,
    string MoxfieldFormatCode,
    int MinimumDeckSize = 60);

public static class Formats
{
    public static readonly FormatDefinition Standard = new(
        Key: "standard",
        DisplayName: "Standard",
        ScryfallLegalityKey: "standard",
        MoxfieldFormatCode: "standard");

    public static readonly FormatDefinition Pioneer = new(
        Key: "pioneer",
        DisplayName: "Pioneer",
        ScryfallLegalityKey: "pioneer",
        MoxfieldFormatCode: "pioneer");

    /// <summary>
    /// Arena's 100-card Brawl, formerly Historic Brawl (#76): a commander and 99 cards, singleton,
    /// from the non-rotating Historic pool. Not Standard Brawl (60 cards), which Arena's saved
    /// decks call "Brawl". Scryfall's key carries the official banned list; the app keeps none.
    /// </summary>
    public static readonly FormatDefinition Brawl = new(
        Key: "brawl",
        DisplayName: "Brawl",
        ScryfallLegalityKey: "brawl",
        MoxfieldFormatCode: "historicbrawl",
        MinimumDeckSize: 100);

    public static readonly IReadOnlyList<FormatDefinition> All = [Standard, Pioneer, Brawl];
}
