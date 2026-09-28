using System.Text.Json;
using MtgaCollectionAdvisor.Core.Hosting;

namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>
/// Whether to re-import the card database now, and why this check learnt nothing new (#74):
/// <see cref="ReadFailure"/> is null when every read due worked, or none was due.
/// </summary>
public sealed record CardRefreshCheck(bool ShouldImport, string? ReadFailure);

/// <summary>
/// Decides whether the card database should refresh itself for a new set (#89). The rules are
/// <see cref="CardRefreshSchedule"/>'s; this reads what they need, each read within its ceiling:
/// card-data.json at most once every 6 hours, and Scryfall's listing at most once every 6 hours
/// and only while a flag is pending. A failed read is "no news", never retried early.
/// </summary>
public sealed class CardRefreshService(
    HttpClient github,
    ScryfallBulkImporter scryfall,
    CardRefreshStore store,
    CardDatabaseStore cards,
    string flagUrl = CardDataFlag.RemoteUrl)
{
    public async Task<CardRefreshCheck> CheckAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var state = await store.LoadAsync(ct);
        string? failure = null;

        var flagJson = state.FlagJson;
        if (CardRefreshSchedule.IsDue(state.FlagReadAt, now))
        {
            string? fetched = null;
            try
            {
                fetched = await github.GetStringAsync(flagUrl, ct);
                if (!IsFlagFile(fetched))
                {
                    failure = "card-data.json is not a JSON object";
                    fetched = null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failure = $"card-data.json: {FailureText.Describe(ex)}";
            }

            await store.RecordFlagReadAsync(now, fetched, ct);
            flagJson = fetched ?? flagJson;
        }

        var flag = CardDataFlag.Parse(flagJson);
        var importedSource = await cards.GetImportedSourceAsync(ct);
        if (!CardRefreshSchedule.IsPending(flag, importedSource, now)) return new CardRefreshCheck(false, failure);

        var fileAt = state.ScryfallFileAt;
        if (CardRefreshSchedule.IsDue(state.ScryfallCheckedAt, now))
        {
            DateTimeOffset? read = null;
            try
            {
                read = (await scryfall.GetDefaultCardsAsync(ct)).UpdatedAt;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                           or InvalidOperationException && !ct.IsCancellationRequested)
            {
                failure = $"Scryfall's bulk-data listing: {FailureText.Describe(ex)}";
            }

            await store.RecordScryfallCheckAsync(now, read, ct);
            fileAt = read ?? fileAt;
        }

        return new CardRefreshCheck(
            CardRefreshSchedule.ShouldImport(flag, importedSource, fileAt, state.AutoImportAt, now),
            failure);
    }

    /// <summary>Recorded before an automatic import starts, so a failing one is tried once per window.</summary>
    public Task RecordAutoImportAsync(DateTimeOffset now, CancellationToken ct = default) =>
        store.RecordAutoImportAsync(now, ct);

    /// <summary>A file worth keeping: a JSON object, whatever its date. A null date clears the flag.</summary>
    private static bool IsFlagFile(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
