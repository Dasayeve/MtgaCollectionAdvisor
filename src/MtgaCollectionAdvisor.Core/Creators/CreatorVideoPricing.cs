using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Creators;

public static class CreatorVideoPricing
{
    /// <summary>The id a video's deck carries during a ranking pass, to match results back.</summary>
    public static string DraftSourceId(string videoId) => $"video:{videoId}";

    /// <summary>
    /// A video's format is only in its title, so its deck is priced under every format the
    /// app ranks and the best fit is kept: first a format whose shape the list has (a
    /// commander and 100 cards for Brawl, neither for the others), then the most nearly legal,
    /// then <see cref="Formats.All"/> order. Legality alone called a 60-card Historic list
    /// Brawl, because Brawl's pool is Historic's (#76).
    /// </summary>
    public static (FormatDefinition Format, DeckAnalysisResult Analysis) PickBest(
        IReadOnlyList<(FormatDefinition Format, DeckAnalysisResult Analysis)> perFormat)
    {
        if (perFormat.Count == 0) throw new ArgumentException("Nothing to pick from.", nameof(perFormat));

        return perFormat
            .Select((candidate, index) => (candidate, index))
            .OrderByDescending(x => x.candidate.Format.FitsShapeOf(x.candidate.Analysis.Deck))
            .ThenBy(x => x.candidate.Analysis.IllegalInFormat.Count)
            .ThenBy(x => FormatOrder(x.candidate.Format))
            .ThenBy(x => x.index)
            .First()
            .candidate;
    }

    private static int FormatOrder(FormatDefinition format)
    {
        for (var i = 0; i < Formats.All.Count; i++)
        {
            if (Formats.All[i].Key == format.Key) return i;
        }
        return int.MaxValue;
    }
}
