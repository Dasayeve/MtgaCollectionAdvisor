namespace MtgaCollectionAdvisor.Core.Models;

public enum DeckBoard
{
    Main,
    Sideboard,

    /// <summary>
    /// A Brawl deck's commander (#76). Part of the deck for cost and colours, but written in its
    /// own section: Arena's importer takes the commander from the "Commander" section only.
    /// </summary>
    Commander
}

public sealed record DeckCardRef(string Name, int Quantity, DeckBoard Board);

/// <summary>
/// A candidate decklist fetched from a public source (Moxfield), identified by
/// card name rather than grpId - names are resolved against the local card
/// database at analysis time so the same deck can be matched across sets.
/// </summary>
public sealed record CandidateDeck(
    string SourceId,
    string Name,
    string Url,
    string FormatKey,
    int Popularity,
    IReadOnlyList<DeckCardRef> Cards,
    DateTimeOffset FetchedAt)
{
    /// <summary>Source-id prefix for decks the user pasted in by hand.</summary>
    public const string ManualSourcePrefix = "manual:";

    /// <summary>
    /// A deck the user added rather than one fetched from a public source. These are
    /// never filtered out of the UI: the user asked for them by name.
    /// </summary>
    public bool IsUserDeck => SourceId.StartsWith(ManualSourcePrefix, StringComparison.Ordinal);
}
