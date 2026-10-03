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

/// <summary>
/// One read of Scryfall's file (#101): the cards with an arena_id, and the Arena prints without
/// one yet, keyed by <see cref="CardSourceMerge.Key"/>, each with a GrpId of 0 until merged.
/// </summary>
public sealed record ScryfallImport(
    IReadOnlyList<CardInfo> WithId,
    IReadOnlyDictionary<(string Set, string Number), IReadOnlyList<CardInfo>> WithoutId);

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

    /// <summary>
    /// The cards Scryfall lists with an arena_id, and the Arena prints it lists without one yet,
    /// by set and collector number: a new set's, before Scryfall publishes its ids (#101).
    /// <see cref="CardSourceMerge"/> gives them the ids from MTG Arena's own card database.
    /// </summary>
    public async Task<ScryfallImport> ImportWithArenaPrintsAsync(ScryfallBulkFile file, CancellationToken ct = default)
    {
        var withId = new List<CardInfo>();
        var withoutId = new Dictionary<(string Set, string Number), List<CardInfo>>();
        await foreach (var card in ReadAsync(file, ct))
        {
            if (card.ArenaId is { } arenaId)
            {
                withId.Add(ToCardInfo(card, arenaId));
            }
            else if (card.Games?.Contains("arena") == true && !string.IsNullOrWhiteSpace(card.CollectorNumber))
            {
                var key = CardSourceMerge.Key(card.Set, card.CollectorNumber);
                if (!withoutId.TryGetValue(key, out var prints)) withoutId[key] = prints = [];
                prints.Add(ToCardInfo(card, arenaId: 0));
            }
        }
        return new ScryfallImport(withId, withoutId.ToDictionary(p => p.Key, IReadOnlyList<CardInfo> (p) => p.Value));
    }

    private async IAsyncEnumerable<ScryfallCard> ReadAsync(
        ScryfallBulkFile file,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
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

            if (card is not null) yield return card;
        }
    }

    private static CardInfo ToCardInfo(ScryfallCard card, int arenaId)
    {
        var (image, backImage) = card.NormalImageUrls();
        return new CardInfo(
            GrpId: arenaId,
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
            IsNonBasicLand: card.IsNonBasicLand(),
            SetName: string.IsNullOrWhiteSpace(card.SetName) ? null : card.SetName,
            SetReleasedAt: DateOnly.TryParseExact(card.ReleasedAt, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var released) ? released : null,
            ManaValue: card.Cmc);
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
