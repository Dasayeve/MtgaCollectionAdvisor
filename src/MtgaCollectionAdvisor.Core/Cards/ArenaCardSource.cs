namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>
/// One read of MTG Arena's card database: the file (null when there is none) and its cards, or
/// why it couldn't be read. <see cref="Used"/> only when the cards are there to merge.
/// </summary>
public sealed record ArenaCardRead(ArenaCardFile? File, IReadOnlyList<ArenaDatabaseCard> Cards, string? Failure)
{
    public bool Used => File is not null && Failure is null;
}

/// <summary>
/// Finds and reads MTG Arena's card database for a card import (#101), and never fails one: no
/// file is an empty read, a file that can't be read is an empty read with the reason, for the
/// caller to log. The folder of the last read is remembered by the import, so Arena needn't run.
/// </summary>
/// <param name="find">Where to look, given the remembered folder; <see cref="ArenaCardDatabase.Find"/> unless a test says otherwise.</param>
public sealed class ArenaCardSource(CardDatabaseStore cards, Func<string?, ArenaCardFile?>? find = null)
{
    private readonly Func<string?, ArenaCardFile?> _find = find ?? ArenaCardDatabase.Find;

    public async Task<ArenaCardFile?> FindAsync(CancellationToken ct = default) =>
        _find((await cards.GetArenaSourceAsync(ct)).RawFolder);

    public async Task<ArenaCardRead> ReadAsync(CancellationToken ct = default) =>
        await FindAsync(ct) is { } file ? await ReadAsync(file, ct) : new ArenaCardRead(null, [], null);

    public static async Task<ArenaCardRead> ReadAsync(ArenaCardFile file, CancellationToken ct = default)
    {
        try
        {
            return new ArenaCardRead(file, await ArenaCardDatabase.ReadAsync(file.Path, ct), null);
        }
        catch (ArenaCardDatabaseException ex)
        {
            return new ArenaCardRead(file, [], ex.Message);
        }
    }
}
