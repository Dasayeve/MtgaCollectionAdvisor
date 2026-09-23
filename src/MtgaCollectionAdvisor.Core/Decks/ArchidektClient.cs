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

    public async Task<IReadOnlyList<CandidateDeck>> FetchTopDecksAsync(
        FormatDefinition format, int count, CancellationToken ct = default)
    {
        if (!FormatIds.TryGetValue(format.Key, out var formatId))
        {
            throw new ArchidektUnavailableException($"Archidekt does not support the {format.DisplayName} format.");
        }

        ArchidektSearchResponse? search;
        try
        {
            search = await httpClient.GetFromJsonAsync<ArchidektSearchResponse>(
                $"api/decks/v3/?deckFormat={formatId}&pageSize={count}&orderBy=-viewCount", JsonOptions, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new ArchidektUnavailableException(
                $"Could not fetch {format.DisplayName} decks from Archidekt.", ex);
        }

        var candidates = (search?.Results ?? []).Where(r => !r.Private && !r.Unlisted).ToList();
        if (candidates.Count == 0)
        {
            throw new ArchidektUnavailableException($"Archidekt returned no public {format.DisplayName} decks.");
        }

        var fetchedAt = DateTimeOffset.UtcNow;
        var decks = new List<CandidateDeck>(candidates.Count);

        foreach (var summary in candidates)
        {
            var deck = await TryFetchDeckDetailAsync(summary, format, fetchedAt, ct);
            if (deck is not null) decks.Add(deck);
        }

        if (decks.Count == 0)
        {
            throw new ArchidektUnavailableException(
                $"Archidekt listed {format.DisplayName} decks, but none could be read in detail.");
        }

        return decks;
    }

    /// <summary>One deck by id, e.g. linked from a video description. Null if unreadable.</summary>
    public Task<CandidateDeck?> TryFetchDeckAsync(int id, FormatDefinition format, CancellationToken ct = default) =>
        TryFetchDeckDetailAsync(new ArchidektDeckSummary { Id = id }, format, DateTimeOffset.UtcNow, ct);

    private async Task<CandidateDeck?> TryFetchDeckDetailAsync(
        ArchidektDeckSummary summary, FormatDefinition format, DateTimeOffset fetchedAt, CancellationToken ct)
    {
        try
        {
            var detail = await httpClient.GetFromJsonAsync<ArchidektDeckDetail>(
                $"api/decks/{summary.Id}/", JsonOptions, ct);
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

                var board = categories.Any(c => c.Equals("Sideboard", StringComparison.OrdinalIgnoreCase))
                    ? DeckBoard.Sideboard
                    : DeckBoard.Main;

                cards.Add(new DeckCardRef(name, entry.Quantity, board));
            }

            // Anyone can save a 5-card scratch deck as "Standard"; a real constructed
            // deck has at least a legal 60-card mainboard.
            const int minimumMainboardSize = 60;
            if (cards.Where(c => c.Board == DeckBoard.Main).Sum(c => c.Quantity) < minimumMainboardSize)
            {
                return null;
            }

            return new CandidateDeck(
                SourceId: $"{SourcePrefix}{summary.Id}",
                Name: string.IsNullOrWhiteSpace(detail.Name) ? summary.Name : detail.Name,
                Url: $"https://archidekt.com/decks/{summary.Id}",
                FormatKey: format.Key,
                Popularity: summary.ViewCount,
                Cards: cards,
                FetchedAt: fetchedAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }
}

public sealed class ArchidektUnavailableException : Exception
{
    public ArchidektUnavailableException(string message) : base(message) { }
    public ArchidektUnavailableException(string message, Exception inner) : base(message, inner) { }
}
