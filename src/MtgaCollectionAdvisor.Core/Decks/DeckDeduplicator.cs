using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Decks;

/// <summary>
/// Public deck sites are full of near-identical copies of the same archetype. Two decks
/// count as the same list when their mainboards overlap almost entirely, measured as
/// shared copies over the larger deck; of each group only the most popular survives. Brawl
/// decks with different commanders are never the same list (#76), however much they share.
/// </summary>
public static class DeckDeduplicator
{
    public const double DefaultSimilarityThreshold = 0.90;

    public static IReadOnlyList<CandidateDeck> Deduplicate(
        IReadOnlyList<CandidateDeck> decks, double threshold = DefaultSimilarityThreshold)
    {
        var ordered = decks.OrderByDescending(d => d.Popularity).ToList();
        var kept = new List<(CandidateDeck Deck, Dictionary<string, int> Main, int Total, string Commander)>();

        foreach (var deck in ordered)
        {
            var main = MainboardOf(deck);
            var total = main.Values.Sum();
            if (total == 0) continue;

            var commander = CommanderOf(deck);
            var isDuplicate = kept.Any(k => k.Commander == commander && Similarity(main, total, k.Main, k.Total) >= threshold);
            if (!isDuplicate) kept.Add((deck, main, total, commander));
        }

        return kept.Select(k => k.Deck).ToList();
    }

    private static string CommanderOf(CandidateDeck deck) => string.Join("|", deck.Cards
        .Where(c => c.Board == DeckBoard.Commander)
        .Select(c => c.Name.ToUpperInvariant())
        .Order(StringComparer.Ordinal));

    private static Dictionary<string, int> MainboardOf(CandidateDeck deck)
    {
        var main = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in deck.Cards.Where(c => c.Board != DeckBoard.Sideboard))
        {
            main[card.Name] = main.TryGetValue(card.Name, out var existing) ? existing + card.Quantity : card.Quantity;
        }
        return main;
    }

    private static double Similarity(
        Dictionary<string, int> a, int totalA, Dictionary<string, int> b, int totalB)
    {
        var shared = 0;
        foreach (var (name, quantity) in a)
        {
            if (b.TryGetValue(name, out var other)) shared += Math.Min(quantity, other);
        }
        return (double)shared / Math.Max(totalA, totalB);
    }
}
