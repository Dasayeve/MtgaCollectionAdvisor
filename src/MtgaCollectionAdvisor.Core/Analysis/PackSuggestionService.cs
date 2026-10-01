using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Analysis;

/// <summary>
/// Suggested packs for one analysed deck (#84): its missing cards, with every printing the deck
/// can use, handed to <see cref="PackSuggester"/>. A read of the card database only; no network.
/// </summary>
public sealed class PackSuggestionService(CardDatabaseStore cards)
{
    /// <param name="deck">The deck as shown: without non-basic lands when those are excluded.</param>
    public async Task<PackSuggestion> SuggestAsync(DeckAnalysisResult deck, FormatDefinition format, CancellationToken ct = default)
    {
        var missing = new List<MissingCard>();

        // Basic lands cost nothing, and a card not on Arena comes from no pack: neither is listed.
        var gaps = deck.Gaps
            .Where(g => g.Missing > 0 && g.AvailableOnArena && g.Rarity != CardRarity.Basic)
            .GroupBy(g => g.CardName, StringComparer.OrdinalIgnoreCase);

        foreach (var gap in gaps)
        {
            var printings = await cards.FindByNameAsync(gap.Key, ct);

            // The printings WildcardCalculator counts for this deck, so a card from a pack counts toward it.
            var legal = printings.Where(p => p.IsLegalIn(format)).ToList();
            var usable = legal.Count > 0 ? legal : printings;
            if (usable.Count == 0) continue;

            // Missing copies as the deck's wildcard cost counts them, main deck and sideboard together.
            missing.Add(new MissingCard(gap.First().CardName, gap.Sum(g => g.Missing), usable));
        }

        return PackSuggester.Suggest(missing);
    }
}
