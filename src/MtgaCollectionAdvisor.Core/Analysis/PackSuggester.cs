using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Analysis;

/// <summary>One card a deck is missing, and every printing of it the deck can use.</summary>
public sealed record MissingCard(string Name, int Missing, IReadOnlyList<CardInfo> Printings);

/// <summary>A card a pack can give, at its rarity in that set; <see cref="AlsoIn"/> names the other pack sets it comes in.</summary>
public sealed record PackCard(
    string Name, int Missing, CardRarity Rarity, string? ImageUrl, string? BackImageUrl, IReadOnlyList<string> AlsoIn);

public sealed record SuggestedPack(string SetCode, string SetName, IReadOnlyList<PackCard> Cards)
{
    public int Copies(CardRarity rarity) => Cards.Where(c => c.Rarity == rarity).Sum(c => c.Missing);
    public int RareAndMythicCopies => Copies(CardRarity.Rare) + Copies(CardRarity.Mythic);
    public int TotalCopies => Cards.Sum(c => c.Missing);
}

/// <summary>The packs to open, in order, and the missing cards no pack can give.</summary>
public sealed record PackSuggestion(IReadOnlyList<SuggestedPack> Packs, IReadOnlyList<PackCard> WildcardsOnly)
{
    public static readonly PackSuggestion Empty = new([], []);
}

/// <summary>
/// Which packs would get a deck the most of what it is missing (#84), in rounds: each round takes
/// the set with the most missing rare and mythic copies, then the most missing copies, then the
/// newest; that set takes every card it can give, and the next round looks at what is left. So a
/// reprint goes to the set that covers the most and every card appears once. A card with no
/// printing in <see cref="PackSets"/> counts for no pack: it is only listed as wildcards-only.
/// </summary>
public static class PackSuggester
{
    public static PackSuggestion Suggest(IReadOnlyList<MissingCard> missing)
    {
        var wanted = missing.Where(m => m.Missing > 0).ToList();

        // Per card, its printing in each pack set: the lowest rarity when a set prints it twice.
        var inSets = wanted.ToDictionary(
            m => m,
            m => m.Printings
                .Where(PackSets.HasPacks)
                .GroupBy(p => p.SetCode.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.OrderBy(p => RarityRank(p.Rarity)).First()));

        var wildcardsOnly = wanted
            .Where(m => inSets[m].Count == 0)
            .Select(m => ToCard(m, m.Printings.OrderBy(p => RarityRank(p.Rarity)).FirstOrDefault(), []))
            .OrderByDescending(c => RarityRank(c.Rarity)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var left = wanted.Where(m => inSets[m].Count > 0).ToList();
        var packs = new List<SuggestedPack>();
        while (left.Count > 0)
        {
            var best = left
                .SelectMany(m => inSets[m].Select(entry => (Set: entry.Key, Card: m, Printing: entry.Value)))
                .GroupBy(x => x.Set)
                .Select(g => (
                    Set: g.Key,
                    Rares: g.Where(x => x.Printing.Rarity is CardRarity.Rare or CardRarity.Mythic).Sum(x => x.Card.Missing),
                    Copies: g.Sum(x => x.Card.Missing),
                    Released: g.Max(x => x.Printing.SetReleasedAt) ?? DateOnly.MinValue,
                    Name: g.Select(x => x.Printing.SetName).FirstOrDefault(n => n is not null)))
                .OrderByDescending(s => s.Rares)
                .ThenByDescending(s => s.Copies)
                .ThenByDescending(s => s.Released)
                .ThenBy(s => s.Set, StringComparer.Ordinal)
                .First();

            var taken = left.Where(m => inSets[m].ContainsKey(best.Set)).ToList();
            var cards = taken
                .Select(m => ToCard(m, inSets[m][best.Set], AlsoIn(inSets[m], best.Set)))
                .OrderByDescending(c => RarityRank(c.Rarity)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            packs.Add(new SuggestedPack(best.Set.ToUpperInvariant(), best.Name ?? best.Set.ToUpperInvariant(), cards));
            left = left.Except(taken).ToList();
        }

        return new PackSuggestion(packs, wildcardsOnly);
    }

    // The other pack sets the card comes in, newest first, by name.
    private static IReadOnlyList<string> AlsoIn(Dictionary<string, CardInfo> inSets, string chosen) =>
        inSets
            .Where(entry => entry.Key != chosen)
            .OrderByDescending(entry => entry.Value.SetReleasedAt ?? DateOnly.MinValue)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Value.SetName ?? entry.Key.ToUpperInvariant())
            .ToList();

    private static PackCard ToCard(MissingCard card, CardInfo? printing, IReadOnlyList<string> alsoIn) =>
        new(card.Name, card.Missing, printing?.Rarity ?? CardRarity.Unknown, printing?.ImageUrl, printing?.BackImageUrl, alsoIn);

    private static int RarityRank(CardRarity rarity) => rarity switch
    {
        CardRarity.Basic => 0,
        CardRarity.Common => 1,
        CardRarity.Uncommon => 2,
        CardRarity.Rare => 3,
        CardRarity.Mythic => 4,
        _ => 5,
    };
}
