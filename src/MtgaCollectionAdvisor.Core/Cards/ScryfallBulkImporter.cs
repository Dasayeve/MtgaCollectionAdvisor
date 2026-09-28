using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>
/// Downloads Scryfall's public "default_cards" bulk data file and projects it down to
/// the subset of fields this app needs, keyed by Arena's grpId (Scryfall's arena_id).
/// Per Scryfall's guidance, bulk data (not the live per-card API) is the correct way
/// to obtain the full card database.
/// </summary>
/// <summary>Scryfall's "default_cards" file: where to download it, and when it was generated.</summary>
public sealed record ScryfallBulkFile(string DownloadUri, DateTimeOffset? UpdatedAt);

public sealed class ScryfallBulkImporter(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MtgaCollectionAdvisor", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    /// <summary>
    /// The small /bulk-data call: where today's "default_cards" file is, and when Scryfall
    /// generated it. An import records that date (#89), so the app knows which file its cards
    /// came from.
    /// </summary>
    public async Task<ScryfallBulkFile> GetDefaultCardsAsync(CancellationToken ct = default)
    {
        var listResponse = await httpClient.GetFromJsonAsync<ScryfallBulkDataResponse>(
            "https://api.scryfall.com/bulk-data", JsonOptions, ct);

        var defaultCards = listResponse?.Data.FirstOrDefault(d => d.Type == "default_cards")
            ?? throw new InvalidOperationException("Scryfall bulk-data listing did not contain a 'default_cards' entry.");

        if (string.IsNullOrEmpty(defaultCards.JsonlDownloadUri))
            throw new InvalidOperationException("Scryfall bulk-data 'default_cards' entry has no jsonl_download_uri.");

        return new ScryfallBulkFile(defaultCards.JsonlDownloadUri, defaultCards.UpdatedAt);
    }

    public async IAsyncEnumerable<CardInfo> ImportAsync(
        ScryfallBulkFile file,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var rawStream = await httpClient.GetStreamAsync(file.DownloadUri, ct);
        await using var gzipStream = new GZipStream(rawStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            ScryfallCard? card;
            try
            {
                card = JsonSerializer.Deserialize<ScryfallCard>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // skip any malformed line rather than aborting the whole import
            }

            if (card is null || card.ArenaId is null) continue;

            var (image, backImage) = card.NormalImageUrls();
            yield return new CardInfo(
                GrpId: card.ArenaId.Value,
                Name: card.Name,
                SetCode: card.Set,
                ManaCost: card.EffectiveManaCost,
                Colors: card.EffectiveColors(),
                Rarity: MapRarity(card),
                StandardLegal: card.IsLegal("standard"),
                PioneerLegal: card.IsLegal("pioneer"),
                BrawlLegal: card.IsLegal(Formats.Brawl.ScryfallLegalityKey),
                StandardBrawlLegal: card.IsLegal(Formats.StandardBrawl.ScryfallLegalityKey),
                ImageUrl: image,
                BackImageUrl: backImage,
                IsNonBasicLand: card.IsNonBasicLand());
        }
    }

    private static CardRarity MapRarity(ScryfallCard card)
    {
        // By the "Basic" supertype, not the text "Basic Land": that missed "Basic Snow Land",
        // which then cost a common wildcard.
        if (card.IsBasicLand()) return CardRarity.Basic;
        return card.Rarity switch
        {
            "common" => CardRarity.Common,
            "uncommon" => CardRarity.Uncommon,
            "rare" => CardRarity.Rare,
            "mythic" => CardRarity.Mythic,
            _ => CardRarity.Unknown
        };
    }
}
