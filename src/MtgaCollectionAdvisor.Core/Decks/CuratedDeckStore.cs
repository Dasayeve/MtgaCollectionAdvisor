using Microsoft.Data.Sqlite;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;

namespace MtgaCollectionAdvisor.Core.Decks;

/// <summary>
/// Stores the pool of candidate decks - both the ones fetched automatically and the
/// ones the user pasted in by hand. Fetched decks are merged in as a source lists them
/// and pruned once they fall out of its window; manual ones are only ever added or
/// deleted individually. A pinned deck is never written, updated or deleted by a fetch:
/// the user is tracking it as it was when pinned.
/// </summary>
public sealed class CuratedDeckStore(Database database)
{
    /// <summary>The source version of every deck read in detail for a format, kept or rejected.</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LoadSourceVersionsAsync(
        FormatDefinition format, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, source_updated_at FROM source_deck_versions WHERE format_key = $format";
        command.Parameters.AddWithValue("$format", format.Key);

        var versions = new Dictionary<string, DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions[reader.GetString(0)] = DateTimeOffset.Parse(reader.GetString(1));
        }
        return versions;
    }

    /// <summary>
    /// Records what a fetch read and merges the kept decks into the pool: new ones added,
    /// changed ones replaced. A rejected deck (<see cref="FetchedDeck.Deck"/> null) takes its
    /// stored copy with it. Pinned decks are skipped entirely.
    /// </summary>
    public async Task<DeckMergeResult> MergeFetchedAsync(
        FormatDefinition format, IReadOnlyList<FetchedDeck> fetched, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var pinned = await LoadPinnedIdsAsync(connection, ct);
        int added = 0, updated = 0;

        foreach (var item in fetched)
        {
            if (pinned.Contains(item.SourceId)) continue;

            await using (var version = connection.CreateCommand())
            {
                version.CommandText = """
                    INSERT INTO source_deck_versions (source_id, format_key, source_updated_at, kept)
                    VALUES ($id, $format, $updatedAt, $kept)
                    ON CONFLICT (source_id) DO UPDATE SET
                        format_key = excluded.format_key,
                        source_updated_at = excluded.source_updated_at,
                        kept = excluded.kept
                    """;
                version.Parameters.AddWithValue("$id", item.SourceId);
                version.Parameters.AddWithValue("$format", format.Key);
                version.Parameters.AddWithValue("$updatedAt", ToStored(item.SourceUpdatedAt));
                version.Parameters.AddWithValue("$kept", item.Deck is null ? 0 : 1);
                await version.ExecuteNonQueryAsync(ct);
            }

            bool existed;
            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM decks WHERE source_id = $id";
                delete.Parameters.AddWithValue("$id", item.SourceId);
                existed = await delete.ExecuteNonQueryAsync(ct) > 0;
            }

            if (item.Deck is null) continue;

            await InsertDeckAsync(connection, item.Deck, ct);
            if (existed) updated++; else added++;
        }

        await transaction.CommitAsync(ct);
        return new DeckMergeResult(added, updated);
    }

    /// <summary>
    /// Removes the fetched decks of a format whose last update is before
    /// <paramref name="cutoff"/>, and those stored before versions were recorded. Pinned
    /// decks and the user's own are never removed. Returns how many decks went.
    /// </summary>
    public async Task<int> PruneFetchedAsync(
        FormatDefinition format, string sourcePrefix, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        int removed;
        await using (var decks = connection.CreateCommand())
        {
            decks.CommandText = """
                DELETE FROM decks
                WHERE format_key = $format AND source_id LIKE $prefix
                  AND source_id NOT IN (SELECT source_id FROM pinned_decks)
                  AND source_id NOT IN (
                      SELECT source_id FROM source_deck_versions
                      WHERE kept = 1 AND source_updated_at >= $cutoff)
                """;
            decks.Parameters.AddWithValue("$format", format.Key);
            decks.Parameters.AddWithValue("$prefix", sourcePrefix + "%");
            decks.Parameters.AddWithValue("$cutoff", ToStored(cutoff));
            removed = await decks.ExecuteNonQueryAsync(ct);
        }

        // Versions out of the window are never consulted again: the walk stops before them.
        await using (var versions = connection.CreateCommand())
        {
            versions.CommandText = """
                DELETE FROM source_deck_versions
                WHERE format_key = $format AND source_updated_at < $cutoff
                """;
            versions.Parameters.AddWithValue("$format", format.Key);
            versions.Parameters.AddWithValue("$cutoff", ToStored(cutoff));
            await versions.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return removed;
    }

    /// <summary>
    /// Removes fetched decks by id - duplicates of a more popular list. Their versions stay
    /// recorded, so an unchanged duplicate is not read again. Pinned and user decks are
    /// never removed.
    /// </summary>
    public async Task<int> RemoveFetchedAsync(IReadOnlyCollection<string> sourceIds, CancellationToken ct = default)
    {
        if (sourceIds.Count == 0) return 0;

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM decks
            WHERE source_id = $id
              AND source_id NOT LIKE $manual
              AND source_id NOT IN (SELECT source_id FROM pinned_decks)
            """;
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.AddWithValue("$manual", CandidateDeck.ManualSourcePrefix + "%");

        var removed = 0;
        foreach (var id in sourceIds)
        {
            idParameter.Value = id;
            removed += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return removed;
    }

    public async Task<DeckSyncState> LoadSyncStateAsync(FormatDefinition format, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_sync_at, full_walk_at FROM deck_sync_state WHERE format_key = $format";
        command.Parameters.AddWithValue("$format", format.Key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new DeckSyncState(null, null);

        return new DeckSyncState(
            reader.IsDBNull(0) ? null : DateTimeOffset.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)));
    }

    public async Task SaveSyncStateAsync(FormatDefinition format, DeckSyncState state, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO deck_sync_state (format_key, last_sync_at, full_walk_at)
            VALUES ($format, $lastSync, $fullWalk)
            ON CONFLICT (format_key) DO UPDATE SET
                last_sync_at = excluded.last_sync_at,
                full_walk_at = excluded.full_walk_at
            """;
        command.Parameters.AddWithValue("$format", format.Key);
        command.Parameters.AddWithValue("$lastSync", state.LastSyncAt is { } s ? ToStored(s) : DBNull.Value);
        command.Parameters.AddWithValue("$fullWalk", state.FullWalkAt is { } w ? ToStored(w) : DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> LoadPinnedIdsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id FROM pinned_decks";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>UTC and round-trip format, so stored timestamps compare correctly as text in SQL.</summary>
    private static string ToStored(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    public async Task AddDeckAsync(CandidateDeck deck, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await InsertDeckAsync(connection, deck, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task InsertDeckAsync(SqliteConnection connection, CandidateDeck deck, CancellationToken ct)
    {
        await using (var insertDeck = connection.CreateCommand())
        {
            insertDeck.CommandText = """
                INSERT INTO decks (source_id, format_key, name, url, popularity, fetched_at)
                VALUES ($id, $format, $name, $url, $popularity, $fetchedAt)
                """;
            insertDeck.Parameters.AddWithValue("$id", deck.SourceId);
            insertDeck.Parameters.AddWithValue("$format", deck.FormatKey);
            insertDeck.Parameters.AddWithValue("$name", deck.Name);
            insertDeck.Parameters.AddWithValue("$url", deck.Url);
            insertDeck.Parameters.AddWithValue("$popularity", deck.Popularity);
            insertDeck.Parameters.AddWithValue("$fetchedAt", deck.FetchedAt.ToString("O"));
            await insertDeck.ExecuteNonQueryAsync(ct);
        }

        await InsertCardsAsync(connection, deck, ct);
    }

    private static async Task InsertCardsAsync(SqliteConnection connection, CandidateDeck deck, CancellationToken ct)
    {
        // Deck sources list cards per printing, so the same card name can appear more
        // than once on a board ("7 Island" as two entries) - collapse those first.
        var merged = deck.Cards
            .GroupBy(c => (c.Name, c.Board))
            .Select(g => new DeckCardRef(g.Key.Name, g.Sum(c => c.Quantity), g.Key.Board));

        await using var insertCard = connection.CreateCommand();
        insertCard.CommandText = """
            INSERT INTO deck_cards (source_id, card_name, board, quantity)
            VALUES ($id, $name, $board, $qty)
            """;
        insertCard.Parameters.AddWithValue("$id", deck.SourceId);
        var nameParameter = insertCard.Parameters.Add("$name", SqliteType.Text);
        var boardParameter = insertCard.Parameters.Add("$board", SqliteType.Text);
        var quantityParameter = insertCard.Parameters.Add("$qty", SqliteType.Integer);

        foreach (var card in merged)
        {
            nameParameter.Value = card.Name;
            boardParameter.Value = card.Board.ToString();
            quantityParameter.Value = card.Quantity;
            await insertCard.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Replaces a user deck's name, format and cards while keeping its source id, so the
    /// pin on it survives - re-importing mints a new id and takes the pin's baseline with
    /// it, which is the whole reason this exists.
    ///
    /// Refuses anything but a manual deck: a fetched one is overwritten when its source
    /// changes it, so the edit would vanish without a word.
    /// </summary>
    public async Task UpdateUserDeckAsync(CandidateDeck deck, CancellationToken ct = default)
    {
        if (!deck.IsUserDeck)
        {
            throw new InvalidOperationException(
                $"Only user decks can be edited; '{deck.SourceId}' is fetched and would be overwritten on the next refresh.");
        }

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE decks SET format_key = $format, name = $name WHERE source_id = $id
                """;
            update.Parameters.AddWithValue("$id", deck.SourceId);
            update.Parameters.AddWithValue("$format", deck.FormatKey);
            update.Parameters.AddWithValue("$name", deck.Name);

            // Nothing updated means the deck is gone - saying nothing here would report
            // success for a write that never happened.
            if (await update.ExecuteNonQueryAsync(ct) == 0)
            {
                throw new InvalidOperationException($"Deck '{deck.SourceId}' no longer exists.");
            }
        }

        await using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM deck_cards WHERE source_id = $id";
            clear.Parameters.AddWithValue("$id", deck.SourceId);
            await clear.ExecuteNonQueryAsync(ct);
        }

        await InsertCardsAsync(connection, deck, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteDeckAsync(string sourceId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM decks WHERE source_id = $id";
        command.Parameters.AddWithValue("$id", sourceId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>How many decks of their own the user has in each format, by format key.</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountUserDecksAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT format_key, count(*) FROM decks WHERE source_id >= $from AND source_id < $to GROUP BY format_key";
        command.Parameters.AddWithValue("$from", CandidateDeck.ManualSourcePrefix);
        command.Parameters.AddWithValue("$to", CandidateDeck.ManualSourcePrefix + "\U0010FFFF");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    public async Task<IReadOnlyList<CandidateDeck>> LoadAsync(FormatDefinition format, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);

        var decksById = new Dictionary<string, (string Name, string Url, int Popularity, DateTimeOffset FetchedAt, List<DeckCardRef> Cards)>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_id, name, url, popularity, fetched_at FROM decks WHERE format_key = $format";
            command.Parameters.AddWithValue("$format", format.Key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                DateTimeOffset.TryParse(reader.GetString(4), out var fetchedAt);
                decksById[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2), reader.GetInt32(3), fetchedAt, []);
            }
        }

        if (decksById.Count == 0) return [];

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT dc.source_id, dc.card_name, dc.board, dc.quantity
                FROM deck_cards dc
                JOIN decks d ON d.source_id = dc.source_id
                WHERE d.format_key = $format
                """;
            command.Parameters.AddWithValue("$format", format.Key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!decksById.TryGetValue(reader.GetString(0), out var deck)) continue;
                var board = Enum.Parse<DeckBoard>(reader.GetString(2));
                deck.Cards.Add(new DeckCardRef(reader.GetString(1), reader.GetInt32(3), board));
            }
        }

        return decksById
            .Select(kv => new CandidateDeck(kv.Key, kv.Value.Name, kv.Value.Url, format.Key, kv.Value.Popularity, kv.Value.Cards, kv.Value.FetchedAt))
            .ToList();
    }
}

/// <summary>
/// A deck a fetch read in detail, with the source's own last-update time.
/// <see cref="Deck"/> is null when it was read and rejected.
/// </summary>
public sealed record FetchedDeck(string SourceId, DateTimeOffset SourceUpdatedAt, CandidateDeck? Deck);

public sealed record DeckMergeResult(int Added, int Updated);

/// <summary>When a format was last fetched, and when a fetch last walked the whole window.</summary>
public sealed record DeckSyncState(DateTimeOffset? LastSyncAt, DateTimeOffset? FullWalkAt);
