namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>
/// What a card import did: how the cards came in (null when MTG Arena's card database wasn't
/// used) and, when it couldn't be read, why (#101). The import worked either way.
/// </summary>
public sealed record CardImportResult(CardMergeCounts? Arena, string? ArenaFailure);

/// <summary>
/// Every card import, manual, first-run or automatic: Scryfall's bulk file, merged with MTG
/// Arena's own card database when there is one (#101), stored with the Scryfall file's date
/// (#89) and the Arena file it was checked against.
/// </summary>
public sealed class CardImportService(ScryfallBulkImporter scryfall, ArenaCardSource arena, CardDatabaseStore cards)
{
    public async Task<CardImportResult> ImportAsync(CancellationToken ct = default)
    {
        var file = await scryfall.GetDefaultCardsAsync(ct);
        var import = await scryfall.ImportWithArenaPrintsAsync(file, ct);

        // Read after the download, so a file Arena replaced meanwhile is the one merged.
        var read = await arena.ReadAsync(ct);
        var merge = CardSourceMerge.Merge(import.WithId, import.WithoutId, read.Cards);

        await cards.ReplaceAllAsync(merge.Cards.ToAsyncEnumerable(), file.UpdatedAt, ct, read.Used ? read.File : null);
        return new CardImportResult(read.Used ? merge.Counts : null, read.Failure);
    }
}
