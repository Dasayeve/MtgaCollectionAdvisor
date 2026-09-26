using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Arena;

/// <summary>A deck saved in Arena, as the Import deck dialog offers it.</summary>
public sealed record ArenaDeckChoice(ArenaDeck Deck, FormatDefinition? Format, bool InApp, int CardCount, int UnknownCards)
{
    /// <summary>False for a format the app does not rank yet; listed so the user sees it was read.</summary>
    public bool IsSupported => Format is not null;
}

/// <summary>
/// Moving decks the player built in Arena into the app, where they are priced, tracked and
/// kept even after being deleted in Arena. The player picks which ones; nothing syncs by
/// itself. Imported decks are user decks, so they export and go back to Arena like any
/// other through Copy for Arena.
/// </summary>
public static class ArenaDeckImport
{
    /// <summary>
    /// Arena's format names to the app's. Explorer is Arena's name for Pioneer - the two
    /// have been unified, so older decks may still carry either name. Arena's saved decks call
    /// the 100-card Brawl "HistoricBrawl" and Standard Brawl just "Brawl" (#75). A new format is
    /// one line here.
    /// </summary>
    private static readonly Dictionary<string, FormatDefinition> FormatsByArenaName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Standard"] = Formats.Standard,
        ["TraditionalStandard"] = Formats.Standard,
        ["Explorer"] = Formats.Pioneer,
        ["TraditionalExplorer"] = Formats.Pioneer,
        ["Pioneer"] = Formats.Pioneer,
        ["HistoricBrawl"] = Formats.Brawl,
        ["Brawl"] = Formats.StandardBrawl,
    };

    public static FormatDefinition? FormatFor(string arenaFormat) =>
        FormatsByArenaName.TryGetValue(arenaFormat, out var format) ? format : null;

    /// <summary>
    /// A user-deck id tied to the Arena deck, so the dialog knows what is already in the app
    /// and re-importing updates the same deck - its pin included - instead of adding a copy.
    /// </summary>
    public static string SourceIdFor(string arenaDeckId) => $"{CandidateDeck.ManualSourcePrefix}arena-{arenaDeckId}";

    /// <summary>The player's own decks - never Wizards' - supported formats first.</summary>
    public static IReadOnlyList<ArenaDeckChoice> Choices(
        IReadOnlyList<ArenaDeck> decks, IReadOnlyDictionary<int, string> names, IReadOnlySet<string> sourceIdsInApp) =>
        decks
            .Where(d => !d.IsWizardsDeck)
            .Select(d =>
            {
                var cards = ImportedCards(d).ToList();
                return new ArenaDeckChoice(
                    d,
                    FormatFor(d.Format),
                    sourceIdsInApp.Contains(SourceIdFor(d.Id)),
                    cards.Sum(c => c.Quantity),
                    cards.Where(c => !names.ContainsKey(c.GrpId)).Sum(c => c.Quantity));
            })
            .OrderBy(c => c.IsSupported ? 0 : 1)
            .ThenBy(c => c.Format?.DisplayName ?? c.Deck.Format, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Deck.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The picker's format filter value for decks in formats the app does not rank.</summary>
    public const string OtherFormats = "other";

    /// <summary>
    /// The choices under one filter: a format key, <see cref="OtherFormats"/> for the
    /// unsupported ones, or null for everything.
    /// </summary>
    public static IReadOnlyList<ArenaDeckChoice> Filter(IReadOnlyList<ArenaDeckChoice> choices, string? filter) => filter switch
    {
        null => choices,
        OtherFormats => choices.Where(c => !c.IsSupported).ToList(),
        _ => choices.Where(c => c.Format?.Key == filter).ToList(),
    };

    /// <summary>The deck as a user deck, or null when its format is not supported.</summary>
    public static CandidateDeck? ToCandidateDeck(ArenaDeck deck, IReadOnlyDictionary<int, string> names, DateTimeOffset now)
    {
        if (FormatFor(deck.Format) is not { } format) return null;

        var cards = deck.Commander.Select(c => (c, DeckBoard.Commander))
            .Concat(deck.Main.Select(c => (c, DeckBoard.Main)))
            .Concat(deck.Sideboard.Select(c => (c, DeckBoard.Sideboard)))
            .Where(x => names.ContainsKey(x.c.GrpId))
            .Select(x => new DeckCardRef(names[x.c.GrpId], x.c.Quantity, x.Item2))
            .ToList();

        return new CandidateDeck(
            SourceId: SourceIdFor(deck.Id),
            Name: deck.Name,
            Url: "",
            FormatKey: format.Key,
            Popularity: 0,
            Cards: cards,
            FetchedAt: now);
    }

    /// <summary>
    /// Commander, main deck and sideboard. Arena lists a companion in the sideboard as well as
    /// in its own section, so the section is left out rather than counting it twice.
    /// </summary>
    private static IEnumerable<ArenaCard> ImportedCards(ArenaDeck deck) =>
        deck.Commander.Concat(deck.Main).Concat(deck.Sideboard);
}
