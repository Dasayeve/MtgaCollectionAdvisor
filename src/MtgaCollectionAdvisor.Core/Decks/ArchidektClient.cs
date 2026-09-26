using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Decks;

/// <summary>
/// Client for Archidekt's public deck-search API (not officially documented, but
/// reachable without the Cloudflare bot-challenge that blocks Moxfield and
/// MTGGoldfish). Deck format ids were extracted from Archidekt's own compiled
/// frontend (its "GameFormat" enum): Standard = 1, Pioneer = 15.
/// </summary>
public sealed class ArchidektClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, int> FormatIds = new()
    {
        [Formats.Standard.Key] = 1,
        [Formats.Pioneer.Key] = 15,
        // Archidekt's "Historic Brawl" (100 cards) and "Brawl" (Standard Brawl, 60). Checked
        // 2026-09-25 (#75). The site has swapped these before; legality comes from Scryfall.
        [Formats.Brawl.Key] = 20,
        [Formats.StandardBrawl.Key] = 13,
    };

    public const string SourcePrefix = "archidekt:";

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://archidekt.com/") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MtgaCollectionAdvisor/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <summary>
    /// One page of public decks in a format, most recently updated first. Archidekt ignores
    /// <c>pageSize</c> and always sends 60 per page, and stops listing after 1000 results.
    /// Sorting by views instead would return the same all-time top decks on every fetch,
    /// most of them long rotated out of the format.
    /// </summary>
    public async Task<ArchidektSearchPage> SearchRecentAsync(FormatDefinition format, int page, CancellationToken ct = default)
    {
        if (!FormatIds.TryGetValue(format.Key, out var formatId))
        {
            throw new ArchidektUnavailableException($"Archidekt does not support the {format.DisplayName} format.");
        }

        ArchidektSearchResponse? search;
        try
        {
            search = await httpClient.GetFromJsonAsync<ArchidektSearchResponse>(
                $"api/decks/v3/?deckFormat={formatId}&orderBy=-updatedAt&page={page}", JsonOptions, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new ArchidektUnavailableException(
                $"Could not search {format.DisplayName} decks on Archidekt.", ex);
        }

        var listings = (search?.Results ?? [])
            .Where(r => !r.Private && !r.Unlisted)
            .Select(r => new ArchidektListing(r.Id, r.Name, r.ViewCount, r.UpdatedAt))
            .ToList();

        return new ArchidektSearchPage(listings, HasNext: search?.Next is not null);
    }

    /// <summary>
    /// A listed deck in detail. Null when it is not a real constructed deck (under 60
    /// mainboard cards); throws <see cref="ArchidektUnavailableException"/> when Archidekt
    /// did not answer, so the caller can stop asking rather than carry on regardless.
    /// </summary>
    public async Task<CandidateDeck?> ReadDeckAsync(
        ArchidektListing listing, FormatDefinition format, DateTimeOffset fetchedAt, CancellationToken ct = default)
    {
        try
        {
            return await ReadDeckDetailAsync(listing.Id, listing.Name, listing.ViewCount, format, fetchedAt, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new ArchidektUnavailableException($"Could not read deck {listing.Id} from Archidekt.", ex);
        }
    }

    /// <summary>
    /// One deck by id, e.g. linked from a creator video. Null when it cannot be read or is
    /// not a real constructed deck (under 60 cards), the same rules as a fetched deck.
    /// </summary>
    public async Task<CandidateDeck?> TryFetchDeckAsync(int id, FormatDefinition format, CancellationToken ct = default)
    {
        try
        {
            return await ReadDeckDetailAsync(id, "", 0, format, DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<CandidateDeck?> ReadDeckDetailAsync(
        int id, string listedName, int viewCount, FormatDefinition format, DateTimeOffset fetchedAt, CancellationToken ct)
    {
        var detail = await httpClient.GetFromJsonAsync<ArchidektDeckDetail>(
            $"api/decks/{id}/", JsonOptions, ct);
        if (detail is null) return null;

        var cards = new List<DeckCardRef>();
        foreach (var entry in detail.Cards ?? [])
        {
            var name = entry.Card?.OracleCard?.Name;
            if (string.IsNullOrWhiteSpace(name) || entry.Quantity <= 0) continue;

            // Archidekt sends "categories": null (not []) for untagged cards, which
            // System.Text.Json writes over the property initializer.
            var categories = entry.Categories ?? [];

            if (categories.Any(c => c.Equals("Maybeboard", StringComparison.OrdinalIgnoreCase)))
                continue;

            // A creator video's link is read without knowing its format, so the category decides.
            var board = categories.Any(c => c.Equals("Commander", StringComparison.OrdinalIgnoreCase))
                ? DeckBoard.Commander
                : categories.Any(c => c.Equals("Sideboard", StringComparison.OrdinalIgnoreCase))
                    ? DeckBoard.Sideboard
                    : DeckBoard.Main;

            cards.Add(new DeckCardRef(name, entry.Quantity, board));
        }

        // Anyone can save a 5-card scratch deck as "Standard"; a real deck has at least the
        // format's size (60, or 100 for Brawl with its commander). A half-built one would rank
        // as cheap.
        if (cards.Where(c => c.Board != DeckBoard.Sideboard).Sum(c => c.Quantity) < format.MinimumDeckSize)
        {
            return null;
        }

        return new CandidateDeck(
            SourceId: $"{SourcePrefix}{id}",
            Name: string.IsNullOrWhiteSpace(detail.Name) ? listedName : detail.Name,
            Url: $"https://archidekt.com/decks/{id}",
            FormatKey: format.Key,
            Popularity: viewCount,
            Cards: cards,
            FetchedAt: fetchedAt);
    }
}

/// <summary>A deck as Archidekt's search lists it, before its cards are read.</summary>
public sealed record ArchidektListing(int Id, string Name, int ViewCount, DateTimeOffset UpdatedAt)
{
    public string SourceId => $"{ArchidektClient.SourcePrefix}{Id}";
}

public sealed record ArchidektSearchPage(IReadOnlyList<ArchidektListing> Decks, bool HasNext);

public sealed class ArchidektUnavailableException : Exception
{
    public ArchidektUnavailableException(string message) : base(message) { }
    public ArchidektUnavailableException(string message, Exception inner) : base(message, inner) { }
}
