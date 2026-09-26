using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Analysis;

/// <summary>
/// Compares one candidate decklist (card names, as fetched from Moxfield) against the
/// user's Arena collection. Card names are resolved to Arena printings (grpId) via the
/// local Scryfall-derived card database; a card can have several printings (different
/// sets/arts), so owned copies are summed across all of them, while the *rarity* used
/// for wildcard cost is taken from the cheapest printing that is actually legal in the
/// target format (the most charitable, and realistic, assumption - Arena lets you craft
/// whichever printing you like).
/// </summary>
public sealed class WildcardCalculator(CardDatabaseStore cardStore)
{
    public async Task<DeckAnalysisResult> AnalyzeAsync(
        CandidateDeck deck,
        CollectionSnapshot collection,
        FormatDefinition format,
        Dictionary<string, IReadOnlyList<CardInfo>> nameCache,
        CancellationToken ct = default)
    {
        var gaps = new List<CardGap>();
        var unavailable = new List<string>();
        var illegal = new List<string>();
        var colors = new HashSet<char>();
        var needed = WildcardNeed.Zero;
        var ownedCopies = 0;
        var totalCopies = 0;

        foreach (var cardRef in deck.Cards)
        {
            if (!nameCache.TryGetValue(cardRef.Name, out var printings))
            {
                printings = await cardStore.FindByNameAsync(cardRef.Name, ct);
                nameCache[cardRef.Name] = printings;
            }

            totalCopies += cardRef.Quantity;

            if (printings.Count == 0)
            {
                unavailable.Add(cardRef.Name);
                gaps.Add(new CardGap(cardRef.Name, cardRef.Board, cardRef.Quantity, 0, null, CardRarity.Unknown));
                continue;
            }

            var legalPrintings = printings
                .Where(p => p.IsLegalIn(format))
                .ToList();
            if (legalPrintings.Count == 0) illegal.Add(cardRef.Name);
            var candidates = legalPrintings.Count > 0 ? legalPrintings : printings;
            var cheapest = candidates.OrderBy(p => RarityRank(p.Rarity)).First();
            var ownedAcrossPrintings = candidates.Sum(p => collection.OwnedQuantity(p.GrpId));

            if (cardRef.Board != DeckBoard.Sideboard)
            {
                foreach (var color in cheapest.Colors) colors.Add(color);
            }

            ownedCopies += Math.Min(ownedAcrossPrintings, cardRef.Quantity);

            var gap = new CardGap(cardRef.Name, cardRef.Board, cardRef.Quantity, ownedAcrossPrintings, cheapest.GrpId, cheapest.Rarity,
                cheapest.ImageUrl, cheapest.BackImageUrl, cheapest.IsNonBasicLand == true);
            gaps.Add(gap);

            if (cheapest.Rarity != CardRarity.Basic)
            {
                needed = needed.Add(WildcardNeed.FromGap(gap));
            }
        }

        return new DeckAnalysisResult(
            deck, needed, ownedCopies, totalCopies, gaps, unavailable, illegal, SortColors(colors));
    }

    /// <summary>Colors in WUBRG order, the way Magic always writes them.</summary>
    private static string SortColors(HashSet<char> colors) =>
        new([.. "WUBRG".Where(colors.Contains)]);

    private static int RarityRank(CardRarity rarity) => rarity switch
    {
        CardRarity.Basic => 0,
        CardRarity.Common => 1,
        CardRarity.Uncommon => 2,
        CardRarity.Rare => 3,
        CardRarity.Mythic => 4,
        _ => 5
    };
}
