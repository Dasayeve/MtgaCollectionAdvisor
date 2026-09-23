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
                INSERT INTO cards (grp_id, name, set_code, mana_cost, colors, rarity, standard_legal, pioneer_legal, updated_at)
                VALUES ($grpId, $name, $setCode, $manaCost, $colors, $rarity, $standard, $pioneer, $updatedAt)
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

    public async Task<DateTimeOffset?> GetLastImportedAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_imported FROM card_import_state WHERE id = 1";
        var result = await command.ExecuteScalarAsync(ct);
        return result is string text && DateTimeOffset.TryParse(text, out var value) ? value : null;
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
        command.CommandText = """
            SELECT DISTINCT name FROM cards
            WHERE name LIKE $prefix ESCAPE '\' COLLATE NOCASE
            ORDER BY name
            LIMIT $limit
            """;
        // The term is user input, so neutralise LIKE's own wildcards before appending ours.
        command.Parameters.AddWithValue("$prefix", Escape(prefix) + "%");
        command.Parameters.AddWithValue("$limit", limit);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static string Escape(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
        command.CommandText = """
            SELECT grp_id, name, set_code, mana_cost, colors, rarity, standard_legal, pioneer_legal
            FROM cards
            WHERE name = $name COLLATE NOCASE
            UNION ALL
            SELECT grp_id, name, set_code, mana_cost, colors, rarity, standard_legal, pioneer_legal
            FROM cards
            WHERE name >= $frontFace COLLATE NOCASE AND name < $frontFaceEnd COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$name", name);
        // "Starts with 'name // '" as a range, not LIKE: an OR with a LIKE ... ESCAPE
        // keeps SQLite off ix_cards_name and scans all ~20k rows for every card of every
        // deck (~2 ms each, ~200x slower). '!' is the character after the space, so the
        // range holds exactly the names with that prefix. No wildcards, nothing to escape.
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
                PioneerLegal: reader.GetInt32(7) == 1));
        }
        return results;
    }
}
