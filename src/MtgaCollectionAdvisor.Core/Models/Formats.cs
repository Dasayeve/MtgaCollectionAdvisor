namespace MtgaCollectionAdvisor.Core.Models;

/// <summary>
/// A constructed format the advisor can rank decks for. Legality comes from Scryfall, but
/// results are restricted to cards that actually exist on Arena. <paramref name="MinimumDeckSize"/>
/// counts the commander: a list shorter than that is a draft, and would rank as cheap.
/// <paramref name="MaximumDeckSize"/> is set for the Brawl formats, whose decks have an exact size.
/// </summary>
public sealed record FormatDefinition(
    string Key,
    string DisplayName,
    string ScryfallLegalityKey,
    string MoxfieldFormatCode,
    int MinimumDeckSize = 60,
    bool HasCommander = false,
    int? MaximumDeckSize = null)
{
    /// <summary>
    /// Whether a list has this format's shape: a commander exactly when the format has one, and
    /// its size, commander included. Legality says nothing about shape: a 60-card Historic deck
    /// is all legal in Brawl, whose pool is Historic's (#76), and a 100-card Brawl deck is not a
    /// Standard Brawl one however legal its cards are.
    /// </summary>
    public bool FitsShapeOf(CandidateDeck deck)
    {
        var size = deck.Cards.Where(c => c.Board != DeckBoard.Sideboard).Sum(c => c.Quantity);
        return deck.Cards.Any(c => c.Board == DeckBoard.Commander) == HasCommander
               && size >= MinimumDeckSize
               && (MaximumDeckSize is not { } maximum || size <= maximum);
    }
}

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
        MinimumDeckSize: 100,
        HasCommander: true,
        MaximumDeckSize: 100);

    /// <summary>
    /// Standard Brawl: a commander and 59 cards, singleton, from the Standard pool. Arena's saved
    /// decks call it "Brawl" and Archidekt calls it "Brawl" too; the 100-card format is the one
    /// named after Historic in both.
    /// </summary>
    public static readonly FormatDefinition StandardBrawl = new(
        Key: "standardbrawl",
        DisplayName: "Standard Brawl",
        ScryfallLegalityKey: "standardbrawl",
        MoxfieldFormatCode: "standardbrawl",
        MinimumDeckSize: 60,
        HasCommander: true,
        MaximumDeckSize: 60);

    public static readonly IReadOnlyList<FormatDefinition> All = [Standard, Pioneer, Brawl, StandardBrawl];

    /// <summary>The formats offered as buttons; the rest sit in a "More" menu, so the switch stays one row.</summary>
    public static readonly IReadOnlyList<FormatDefinition> Main = [Standard, Pioneer, Brawl];

    public static IReadOnlyList<FormatDefinition> More { get; } = [.. All.Where(f => !Main.Contains(f))];
}
