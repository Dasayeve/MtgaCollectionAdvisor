using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Analysis;

/// <summary>Odds for an opening hand drawn from a library (#126), exact, not sampled.</summary>
/// <param name="ExactlyLands">Index k is the chance of exactly k lands, k from 0 to <paramref name="HandSize"/>.</param>
public sealed record OpeningHandOdds(
    int LibrarySize,
    int Lands,
    int HandSize,
    IReadOnlyList<double> ExactlyLands)
{
    /// <summary>A hand most players keep by land count: 2 to 4 lands.</summary>
    public double TwoToFourLands => Enumerable.Range(2, 3).Where(k => k <= HandSize).Sum(k => ExactlyLands[k]);

    public double AverageLands => LibrarySize == 0 ? 0 : (double)HandSize * Lands / LibrarySize;
}

/// <summary>
/// Sample opening hands from a deck (#126): the main board shuffled, 7 dealt. The commander and
/// the sideboard are not in the library, so a Brawl deck draws from its 99 (or 59).
/// </summary>
public static class TestDraw
{
    public const int OpeningHandSize = 7;

    /// <summary>The main board, one entry per copy.</summary>
    public static IReadOnlyList<CardGap> Library(IEnumerable<CardGap> gaps) =>
        [.. gaps.Where(g => g.Board == DeckBoard.Main).SelectMany(g => Enumerable.Repeat(g, Math.Max(0, g.Needed)))];

    /// <summary>
    /// A uniformly random hand of min(count, library size) cards: a partial Fisher–Yates shuffle
    /// over a copy of the library. OrderBy(random) is not uniform.
    /// </summary>
    public static IReadOnlyList<CardGap> Deal(IReadOnlyList<CardGap> library, Random random, int count = OpeningHandSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var cards = library.ToArray();
        var dealt = Math.Min(count, cards.Length);
        for (var i = 0; i < dealt; i++)
        {
            var j = random.Next(i, cards.Length);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        return cards[..dealt];
    }

    /// <summary>Hypergeometric odds of each land count in a hand of min(7, library size).</summary>
    public static OpeningHandOdds Odds(IReadOnlyList<CardGap> library)
    {
        var size = library.Count;
        var lands = library.Count(g => g.IsLand);
        var hand = Math.Min(OpeningHandSize, size);
        var hands = Choose(size, hand);
        var exactly = Enumerable.Range(0, hand + 1)
            .Select(k => Choose(lands, k) * Choose(size - lands, hand - k) / hands)
            .ToList();
        return new OpeningHandOdds(size, lands, hand, exactly);
    }

    // In double, multiplicatively: C(250, 7) is about 6e12, far inside its exact range.
    private static double Choose(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        var result = 1.0;
        for (var i = 1; i <= k; i++)
        {
            result = result * (n - k + i) / i;
        }
        return result;
    }
}
