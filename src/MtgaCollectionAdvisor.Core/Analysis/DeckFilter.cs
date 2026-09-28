using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Analysis;

/// <summary>Which half of the pool a list is showing.</summary>
public enum DeckSourceFilter
{
    Any,

    /// <summary>Decks pulled from a public source.</summary>
    Fetched,

    /// <summary>Decks the user added by hand.</summary>
    User
}

/// <summary>
/// An optional per-rarity cap on a deck's wildcard cost. A null cap is "no limit"; a cap of
/// zero is a real constraint ("no mythics needed"), which is why these are nullable ints
/// rather than a plain count.
///
/// Rare and mythic wildcards are the scarce ones, so a single total is the wrong unit for
/// the decision the user is actually making.
/// </summary>
public sealed record RarityBudget(int? Commons, int? Uncommons, int? Rares, int? Mythics)
{
    public static RarityBudget Unlimited { get; } = new(null, null, null, null);

    public bool IsUnlimited =>
        Commons is null && Uncommons is null && Rares is null && Mythics is null;

    public bool Allows(WildcardNeed need) =>
        Within(Commons, need.Commons)
        && Within(Uncommons, need.Uncommons)
        && Within(Rares, need.Rares)
        && Within(Mythics, need.Mythics);

    // A negative cap is nonsense the number input can still produce; treat it as zero
    // rather than as "nothing qualifies", matching how MaxWildcards is already clamped.
    private static bool Within(int? cap, int needed) =>
        cap is not { } c || needed <= Math.Max(0, c);
}

/// <summary>
/// Everything the deck list can be narrowed by. Lives here rather than in the UI so the
/// rules can be tested without rendering a component or touching a database.
/// </summary>
public sealed record DeckFilterCriteria
{
    public IReadOnlySet<char> Colors { get; init; } = new HashSet<char>();

    /// <summary>Match the colour combination exactly, instead of "includes these colours".</summary>
    public bool ExactColors { get; init; }

    public bool OnlyCraftable { get; init; }
    public int? MaxWildcards { get; init; }

    /// <summary>Optional per-rarity caps on the wildcard cost, independent of MaxWildcards.</summary>
    public RarityBudget RarityBudget { get; init; } = RarityBudget.Unlimited;

    /// <summary>
    /// Keep only decks at least this far complete, as a fraction of total copies (0.0-1.0).
    /// Null is no threshold; the UI normalises "0%" to null so an untouched slider does not
    /// read as an active filter.
    /// </summary>
    public double? MinOwnedFraction { get; init; }
    public string NameSearch { get; init; } = "";

    /// <summary>Every one of these cards must be in the deck.</summary>
    public IReadOnlyList<string> ContainsCards { get; init; } = [];

    /// <summary>None of these cards may be in the deck.</summary>
    public IReadOnlyList<string> ExcludesCards { get; init; } = [];

    /// <summary>Show only decks the user is tracking.</summary>
    public bool OnlyPinned { get; init; }

    public IReadOnlySet<string> PinnedSourceIds { get; init; } = new HashSet<string>();

    /// <summary>
    /// Keep decks that are illegal in the format or use cards missing from Arena. The
    /// recommended list leaves this off; a list of the user's own decks turns it on,
    /// because a deck someone imported by hand should never silently disappear.
    ///
    /// Deliberately absent from <see cref="IsEmpty"/>: this is which list is being shown,
    /// not a filter the user chose, so it must not offer them a "clear filters" link.
    /// </summary>
    public bool IncludeUnplayable { get; init; }

    /// <summary>
    /// Show only fetched decks, only the user's own, or both. Absent from
    /// <see cref="IsEmpty"/> for the same reason as <see cref="IncludeUnplayable"/>.
    /// </summary>
    public DeckSourceFilter Source { get; init; } = DeckSourceFilter.Any;

    public bool IsEmpty => ActiveCount == 0;

    /// <summary>
    /// How many filters are set, one per control the player touched (the colours count once,
    /// the per-rarity budget once), so a collapsed filter column can say that filters apply.
    /// </summary>
    public int ActiveCount =>
        (Colors.Count > 0 ? 1 : 0)
        + (OnlyCraftable ? 1 : 0)
        + (MaxWildcards is not null ? 1 : 0)
        + (RarityBudget.IsUnlimited ? 0 : 1)
        + (MinOwnedFraction is not null ? 1 : 0)
        + (string.IsNullOrWhiteSpace(NameSearch) ? 0 : 1)
        + (ContainsCards.Count > 0 ? 1 : 0)
        + (ExcludesCards.Count > 0 ? 1 : 0)
        + (OnlyPinned ? 1 : 0);
}

public static class DeckFilter
{
    public static IReadOnlyList<DeckAnalysisResult> Apply(
        IEnumerable<DeckAnalysisResult> decks,
        DeckFilterCriteria criteria,
        WildcardInventory? wallet)
    {
        IEnumerable<DeckAnalysisResult> query = decks;

        query = criteria.Source switch
        {
            DeckSourceFilter.User => query.Where(d => d.Deck.IsUserDeck),
            DeckSourceFilter.Fetched => query.Where(d => !d.Deck.IsUserDeck),
            _ => query
        };

        // Deck sources let anyone file any list under any format, and a list can name a
        // card that never came to Arena. Dropping those is right for suggestions and
        // wrong for a deck the user asked for, so it is a switch rather than a rule.
        if (!criteria.IncludeUnplayable)
        {
            query = query.Where(d => d.LegalInFormat && d.FullyPlayableOnArena);
        }

        if (criteria.Colors.Count > 0)
        {
            query = criteria.ExactColors
                ? query.Where(d => d.Colors.Length == criteria.Colors.Count && d.Colors.All(criteria.Colors.Contains))
                : query.Where(d => criteria.Colors.All(c => d.Colors.Contains(c)));
        }

        // Unknown wildcards (#57) can't say what is craftable; the option is disabled in the UI,
        // and here it filters nothing rather than everything.
        if (criteria.OnlyCraftable && wallet is not null)
        {
            query = query.Where(d => d.IsCraftableWith(wallet));
        }

        // A deck with unrecognised cards has a cost that is only a floor (#87): it can't be
        // said to fit a budget, so the budgets leave it out.
        if (criteria.MaxWildcards is { } max)
        {
            query = query.Where(d => d.FullyPlayableOnArena && d.Needed.Total <= Math.Max(0, max));
        }

        if (!criteria.RarityBudget.IsUnlimited)
        {
            query = query.Where(d => d.FullyPlayableOnArena && criteria.RarityBudget.Allows(d.Needed));
        }

        if (criteria.MinOwnedFraction is { } minOwned)
        {
            query = query.Where(d => d.OwnedFraction >= Math.Clamp(minOwned, 0, 1));
        }

        if (!string.IsNullOrWhiteSpace(criteria.NameSearch))
        {
            query = query.Where(d => d.Deck.Name.Contains(criteria.NameSearch, StringComparison.OrdinalIgnoreCase));
        }

        // Every required card must be present; any excluded card disqualifies the deck.
        if (criteria.ContainsCards.Count > 0)
        {
            query = query.Where(d => criteria.ContainsCards.All(card => PlaysCard(d, card)));
        }

        if (criteria.ExcludesCards.Count > 0)
        {
            query = query.Where(d => !criteria.ExcludesCards.Any(card => PlaysCard(d, card)));
        }

        if (criteria.OnlyPinned)
        {
            query = query.Where(d => criteria.PinnedSourceIds.Contains(d.Deck.SourceId));
        }

        return query.ToList();
    }

    /// <summary>
    /// Sideboard cards count as played: they still cost wildcards, and a deck that only
    /// sideboards a card is still a deck built around owning it.
    /// </summary>
    private static bool PlaysCard(DeckAnalysisResult deck, string cardName) =>
        deck.Deck.Cards.Any(c => c.Name.Equals(cardName, StringComparison.OrdinalIgnoreCase));
}
