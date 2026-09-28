namespace MtgaCollectionAdvisor.Core.Models;

/// <summary>
/// Per-card gap between what the deck needs and what the collection owns.
/// GrpId is null when the card could not be matched to an Arena printing at all
/// (i.e. it does not exist on Arena, regardless of collection). The image URLs are those of
/// the printing matched, so the hover preview shows the card whose rarity and cost the line uses.
/// </summary>
public sealed record CardGap(
    string CardName,
    DeckBoard Board,
    int Needed,
    int Owned,
    int? GrpId,
    CardRarity Rarity,
    string? ImageUrl = null,
    string? BackImageUrl = null,
    bool IsNonBasicLand = false)
{
    public int Missing => Math.Max(0, Needed - Owned);
    public bool AvailableOnArena => GrpId is not null;
}

public sealed record WildcardNeed(int Commons, int Uncommons, int Rares, int Mythics)
{
    public int Total => Commons + Uncommons + Rares + Mythics;

    public static WildcardNeed Zero { get; } = new(0, 0, 0, 0);

    public static WildcardNeed FromGap(CardGap gap)
    {
        if (gap.Missing <= 0 || !gap.AvailableOnArena) return Zero;
        return gap.Rarity switch
        {
            CardRarity.Common => new WildcardNeed(gap.Missing, 0, 0, 0),
            CardRarity.Uncommon => new WildcardNeed(0, gap.Missing, 0, 0),
            CardRarity.Rare => new WildcardNeed(0, 0, gap.Missing, 0),
            CardRarity.Mythic => new WildcardNeed(0, 0, 0, gap.Missing),
            _ => Zero
        };
    }

    public WildcardNeed Add(WildcardNeed other) => new(
        Commons + other.Commons,
        Uncommons + other.Uncommons,
        Rares + other.Rares,
        Mythics + other.Mythics);

    /// <summary>
    /// Wildcards are rarity-specific in Arena (a Rare wildcard can't craft a Mythic),
    /// so affordability must be checked per rarity, not just by total count.
    /// </summary>
    public bool IsAffordableWith(WildcardInventory wallet) =>
        wallet.Commons >= Commons && wallet.Uncommons >= Uncommons &&
        wallet.Rares >= Rares && wallet.Mythics >= Mythics;

    /// <summary>The wildcards still missing once the wallet is spent, per rarity; zero when affordable.</summary>
    public WildcardNeed ShortfallAgainst(WildcardInventory wallet) => new(
        Math.Max(0, Commons - wallet.Commons),
        Math.Max(0, Uncommons - wallet.Uncommons),
        Math.Max(0, Rares - wallet.Rares),
        Math.Max(0, Mythics - wallet.Mythics));

    /// <summary>
    /// "4 rares short", "1 mythic, 2 rares short": why a deck can't be crafted yet, rarest first,
    /// so a "no" in the deck list says what it would take. Null when nothing is missing.
    /// </summary>
    public string? DescribeShortfall()
    {
        var parts = new List<string>();
        if (Mythics > 0) parts.Add(Plural(Mythics, "mythic"));
        if (Rares > 0) parts.Add(Plural(Rares, "rare"));
        if (Uncommons > 0) parts.Add(Plural(Uncommons, "uncommon"));
        if (Commons > 0) parts.Add(Plural(Commons, "common"));
        return parts.Count == 0 ? null : $"{string.Join(", ", parts)} short";

        static string Plural(int count, string rarity) => $"{count} {rarity}{(count == 1 ? "" : "s")}";
    }
}

public sealed record DeckAnalysisResult(
    CandidateDeck Deck,
    WildcardNeed Needed,
    int OwnedCopies,
    int TotalCopies,
    IReadOnlyList<CardGap> Gaps,
    IReadOnlyList<string> UnavailableOnArena,
    IReadOnlyList<string> IllegalInFormat,
    string Colors)
{
    public double OwnedFraction => TotalCopies == 0 ? 0 : (double)OwnedCopies / TotalCopies;
    public bool FullyPlayableOnArena => UnavailableOnArena.Count == 0;

    /// <summary>
    /// Deck sources let users file a deck under any format they like, so a "Standard"
    /// deck may well contain cards that rotated out years ago.
    /// </summary>
    public bool LegalInFormat => IllegalInFormat.Count == 0;

    public bool HasNonBasicLands => Gaps.Any(g => g.IsNonBasicLand);

    /// <summary>
    /// The same deck without its non-basic lands (#61), for pricing a deck before its mana base,
    /// which is often most of its rare wildcards. Cost and owned totals are recomputed the way
    /// WildcardCalculator computes them, and the deck's own list loses those cards too, so what
    /// is shown and what is exported agree.
    /// </summary>
    public DeckAnalysisResult WithoutNonBasicLands()
    {
        if (!HasNonBasicLands) return this;

        var excluded = Gaps.Where(g => g.IsNonBasicLand).Select(g => g.CardName).ToHashSet(StringComparer.Ordinal);
        var gaps = Gaps.Where(g => !g.IsNonBasicLand).ToList();

        return this with
        {
            Deck = Deck with { Cards = [.. Deck.Cards.Where(c => !excluded.Contains(c.Name))] },
            Gaps = gaps,
            Needed = gaps.Where(g => g.Rarity != CardRarity.Basic)
                .Aggregate(WildcardNeed.Zero, (sum, g) => sum.Add(WildcardNeed.FromGap(g))),
            OwnedCopies = gaps.Sum(g => Math.Min(g.Owned, g.Needed)),
            TotalCopies = gaps.Sum(g => g.Needed),
            UnavailableOnArena = [.. UnavailableOnArena.Where(n => !excluded.Contains(n))],
            IllegalInFormat = [.. IllegalInFormat.Where(n => !excluded.Contains(n))],
        };
    }

    /// <summary>How many copies the non-basic lands are, and the wildcards they still cost.</summary>
    public (int Copies, WildcardNeed Cost) NonBasicLandShare()
    {
        var lands = Gaps.Where(g => g.IsNonBasicLand).ToList();
        return (lands.Sum(g => g.Needed), lands.Aggregate(WildcardNeed.Zero, (sum, g) => sum.Add(WildcardNeed.FromGap(g))));
    }
}
