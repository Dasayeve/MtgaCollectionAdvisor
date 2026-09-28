using System.Globalization;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Creators;

public enum CreatorVideoSort
{
    Newest,

    /// <summary>Priced videos by wildcards needed; videos without a known deck after them.</summary>
    Cheapest
}

/// <summary>Everything the Creators tab can be narrowed by, kept out of the page so it can be tested.</summary>
public sealed record CreatorVideoFilterCriteria
{
    /// <summary>One creator's videos; null or empty for all.</summary>
    public string? Creator { get; init; }

    /// <summary>Only videos whose deck was read and priced.</summary>
    public bool OnlyWithDeck { get; init; }

    /// <summary>Only decks the current wildcards can finish, rarity by rarity.</summary>
    public bool OnlyCraftable { get; init; }

    /// <summary>Only decks legal in a format the app ranks (Standard or Pioneer).</summary>
    public bool OnlyAppFormats { get; init; }

    /// <summary>
    /// Videos from channels that do not speak English. The app ships mainly in English, so
    /// the page starts this from the user's own language: see <see cref="IncludeNonEnglishByDefault"/>.
    /// </summary>
    public bool IncludeNonEnglish { get; init; } = true;

    public CreatorVideoSort Sort { get; init; } = CreatorVideoSort.Newest;

    /// <summary>On for anyone whose Windows is not in English; an English user never sees them unasked.</summary>
    public static bool IncludeNonEnglishByDefault(CultureInfo uiCulture) =>
        !uiCulture.TwoLetterISOLanguageName.Equals(CreatorChannel.English, StringComparison.OrdinalIgnoreCase);
}

public static class CreatorVideoFilter
{
    public static IReadOnlyList<CreatorVideoCard> Apply(
        IEnumerable<CreatorVideoCard> cards,
        CreatorVideoFilterCriteria criteria,
        WildcardInventory? wallet)
    {
        var query = cards;

        if (!string.IsNullOrEmpty(criteria.Creator))
        {
            query = query.Where(c => c.Video.Creator.Equals(criteria.Creator, StringComparison.Ordinal));
        }

        if (!criteria.IncludeNonEnglish)
        {
            query = query.Where(c => c.Video.IsEnglish);
        }

        if (criteria.OnlyWithDeck)
        {
            query = query.Where(c => c.Analysis is not null);
        }

        // Unknown wildcards (#57): filter nothing rather than everything; the UI disables it.
        if (criteria.OnlyCraftable && wallet is not null)
        {
            query = query.Where(c => c.Analysis?.IsCraftableWith(wallet) == true);
        }

        if (criteria.OnlyAppFormats)
        {
            query = query.Where(c => c.IsLegalInAppFormat);
        }

        query = criteria.Sort == CreatorVideoSort.Cheapest
            ? query
                .OrderBy(c => c.Analysis is null)
                // A cost with unrecognised cards is only a floor (#87): after every known cost.
                .ThenBy(c => c.Analysis?.FullyPlayableOnArena == false)
                .ThenBy(c => c.Analysis?.Needed.Total ?? 0)
                .ThenByDescending(c => c.Video.Published)
            : query.OrderByDescending(c => c.Video.Published);

        return query.ToList();
    }
}
