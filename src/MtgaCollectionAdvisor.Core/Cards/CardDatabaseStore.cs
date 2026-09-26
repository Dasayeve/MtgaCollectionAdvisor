using Microsoft.Data.Sqlite;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;

namespace MtgaCollectionAdvisor.Core.Cards;

public sealed class CardDatabaseStore(Database database)
{
    public async Task ReplaceAllAsync(IAsyncEnumerable<CardInfo> cards, CancellationToken ct = default)
    {
        var importedAt = DateTimeOffset.UtcNow.ToString("O");

        // Scryfall's bulk data occasionally lists the same arena_id twice (split/meld
        // halves sharing one Arena object); grp_id is our primary key, so dedupe first.
        var deduped = new Dictionary<int, CardInfo>();
        await foreach (var card in cards.WithCancellation(ct))
        {
            deduped[card.GrpId] = card;
        }

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM cards";
            await clear.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO cards (grp_id, name, set_code, mana_cost, colors, rarity, standard_legal, pioneer_legal, updated_at,
                                   image_url, back_image_url, is_nonbasic_land, brawl_legal, standard_brawl_legal)
                VALUES ($grpId, $name, $setCode, $manaCost, $colors, $rarity, $standard, $pioneer, $updatedAt,
                        $imageUrl, $backImageUrl, $nonBasicLand, $brawl, $standardBrawl)
                """;
            var grpId = insert.Parameters.Add("$grpId", SqliteType.Integer);
            var name = insert.Parameters.Add("$name", SqliteType.Text);
            var setCode = insert.Parameters.Add("$setCode", SqliteType.Text);
            var manaCost = insert.Parameters.Add("$manaCost", SqliteType.Text);
            var colors = insert.Parameters.Add("$colors", SqliteType.Text);
            var rarity = insert.Parameters.Add("$rarity", SqliteType.Text);
            var standard = insert.Parameters.Add("$standard", SqliteType.Integer);
            var pioneer = insert.Parameters.Add("$pioneer", SqliteType.Integer);
            insert.Parameters.AddWithValue("$updatedAt", importedAt);
            var imageUrl = insert.Parameters.Add("$imageUrl", SqliteType.Text);
            var backImageUrl = insert.Parameters.Add("$backImageUrl", SqliteType.Text);
            var nonBasicLand = insert.Parameters.Add("$nonBasicLand", SqliteType.Integer);
            var brawl = insert.Parameters.Add("$brawl", SqliteType.Integer);
            var standardBrawl = insert.Parameters.Add("$standardBrawl", SqliteType.Integer);

            foreach (var card in deduped.Values)
            {
                grpId.Value = card.GrpId;
                name.Value = card.Name;
                setCode.Value = card.SetCode;
                manaCost.Value = card.ManaCost;
                colors.Value = card.Colors;
                rarity.Value = card.Rarity.ToString();
                standard.Value = card.StandardLegal ? 1 : 0;
                pioneer.Value = card.PioneerLegal ? 1 : 0;
                imageUrl.Value = (object?)card.ImageUrl ?? DBNull.Value;
                backImageUrl.Value = (object?)card.BackImageUrl ?? DBNull.Value;
                nonBasicLand.Value = card.IsNonBasicLand is { } land ? (land ? 1 : 0) : DBNull.Value;
                brawl.Value = card.BrawlLegal ? 1 : 0;
                standardBrawl.Value = card.StandardBrawlLegal ? 1 : 0;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var state = connection.CreateCommand())
        {
            state.CommandText = """
                INSERT INTO card_import_state (id, last_imported) VALUES (1, $at)
                ON CONFLICT (id) DO UPDATE SET last_imported = excluded.last_imported
                """;
            state.Parameters.AddWithValue("$at", importedAt);
            await state.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// A card database imported before a later migration's columns existed (image URLs, #59;
    /// the non-basic land flag, #61; Brawl and Standard Brawl legality, #76) has cards and nothing in one of those
    /// columns: it needs one more import, which the app runs by itself. A migration adds
    /// columns, not data.
    /// </summary>
    public static bool NeedsCardDataBackfill(CardDataCounts counts) =>
        counts.Cards > 0 && (counts.WithImage == 0 || counts.WithLandFlag == 0 || counts.WithBrawlLegality == 0
                             || counts.WithStandardBrawlLegality == 0);

    public async Task<CardDataCounts> CountCardDataAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*), count(image_url), count(is_nonbasic_land), count(brawl_legal), count(standard_brawl_legal)
            FROM cards
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new CardDataCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    public async Task<DateTimeOffset?> GetLastImportedAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_imported FROM card_import_state WHERE id = 1";
        var result = await command.ExecuteScalarAsync(ct);
        return result is string text && DateTimeOffset.TryParse(text, out var value) ? value : null;
    }

    /// <summary>The name of every Arena id we know about - used to write the collection out.</summary>
    public async Task<IReadOnlyDictionary<int, string>> GetNamesAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT grp_id, name FROM cards";
        await using var reader = await command.ExecuteReaderAsync(ct);

        var names = new Dictionary<int, string>();
        while (await reader.ReadAsync(ct))
        {
            names[reader.GetInt32(0)] = reader.GetString(1);
        }
        return names;
    }

    /// <summary>Every Arena id we know about - used to score memory-scan candidate blocks.</summary>
    public async Task<IReadOnlySet<int>> GetAllGrpIdsAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT grp_id FROM cards";
        await using var reader = await command.ExecuteReaderAsync(ct);

        var ids = new HashSet<int>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }

    /// <summary>
    /// Distinct card names starting with <paramref name="term"/>, for autocomplete.
    /// Prefix rather than substring matching: it uses the name index and gives
    /// predictable results as the user types.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchNamesAsync(
        string term, int limit = 10, CancellationToken ct = default)
    {
        var prefix = term.Trim();
        if (prefix.Length < 2) return [];

        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = SearchNamesSql;
        command.Parameters.AddWithValue("$from", prefix);
        command.Parameters.AddWithValue("$to", prefix + PastEveryCharacter);
        command.Parameters.AddWithValue("$limit", limit);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    // Both name lookups are prefix matches written as ranges on ix_cards_name, not LIKE.
    // SQLite drops its LIKE-to-index optimisation whenever an ESCAPE clause is present,
    // and user input needs escaping, so a LIKE here scans all ~20k cards on every call:
    // ~2 ms against ~0.01 ms, per card, per deck. A range also has no wildcards, so
    // there is nothing in the input to escape.
    internal const string SearchNamesSql = """
        SELECT DISTINCT name FROM cards
        WHERE name >= $from COLLATE NOCASE AND name < $to COLLATE NOCASE
        ORDER BY name
        LIMIT $limit
        """;

    internal const string FindByNameSql = """
        SELECT grp_id, name, set_code, mana_cost, colors, rarity, standard_legal, pioneer_legal, image_url, back_image_url, is_nonbasic_land, brawl_legal, standard_brawl_legal
        FROM cards
        WHERE name = $name COLLATE NOCASE
        UNION ALL
        SELECT grp_id, name, set_code, mana_cost, colors, rarity, standard_legal, pioneer_legal, image_url, back_image_url, is_nonbasic_land, brawl_legal, standard_brawl_legal
        FROM cards
        WHERE name >= $frontFace COLLATE NOCASE AND name < $frontFaceEnd COLLATE NOCASE
        """;

    /// <summary>Sorts after any character a card name can contain, closing a prefix range.</summary>
    private const string PastEveryCharacter = "\U0010FFFF";

    /// <summary>
    /// Every Arena printing of a card, by name.
    ///
    /// Double-faced cards are stored under their full "Front // Back" name, but Arena's
    /// own export format - the format decklists are pasted in - writes only the front
    /// face. So a bare name also matches anything filed as "that name // something", or
    /// a deck loses the card entirely and, with it, its whole analysis.
    ///
    /// The front-face branch can in principle over-match (asking for "Fire" would also
    /// find "Fire // Ice"), which is accepted: Arena exports split cards under their full
    /// name, so it does not arise in practice, and matching too much beats dropping a
    /// deck. Back-face names are not matched - Arena never writes them.
    /// </summary>
    public async Task<IReadOnlyList<CardInfo>> FindByNameAsync(string name, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = FindByNameSql;
        command.Parameters.AddWithValue("$name", name);
        // Every name starting "name // ": '!' is the character right after the space,
        // so the range ends exactly where that prefix does.
        command.Parameters.AddWithValue("$frontFace", name + " // ");
        command.Parameters.AddWithValue("$frontFaceEnd", name + " //!");

        var results = new List<CardInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CardInfo(
                GrpId: reader.GetInt32(0),
                Name: reader.GetString(1),
                SetCode: reader.GetString(2),
                ManaCost: reader.GetString(3),
                Colors: reader.GetString(4),
                Rarity: Enum.Parse<CardRarity>(reader.GetString(5)),
                StandardLegal: reader.GetInt32(6) == 1,
                PioneerLegal: reader.GetInt32(7) == 1,
                ImageUrl: reader.IsDBNull(8) ? null : reader.GetString(8),
                BackImageUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
                IsNonBasicLand: reader.IsDBNull(10) ? null : reader.GetInt32(10) == 1,
                BrawlLegal: !reader.IsDBNull(11) && reader.GetInt32(11) == 1,
                StandardBrawlLegal: !reader.IsDBNull(12) && reader.GetInt32(12) == 1));
        }
        return results;
    }
}

/// <summary>How many cards the database has, and how many have each column a later migration added.</summary>
public sealed record CardDataCounts(int Cards, int WithImage, int WithLandFlag, int WithBrawlLegality, int WithStandardBrawlLegality);
