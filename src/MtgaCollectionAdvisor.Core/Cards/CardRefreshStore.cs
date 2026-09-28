using System.Globalization;
using Microsoft.Data.Sqlite;
using MtgaCollectionAdvisor.Core.Storage;

namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>
/// What the card refresh (#89) last saw and when, so its ceilings hold across restarts: the last
/// good card-data.json, when it was read, when Scryfall's listing was looked at and the file date
/// it gave, and when an automatic import was last tried.
/// </summary>
public sealed record CardRefreshState(
    string? FlagJson,
    DateTimeOffset? FlagReadAt,
    DateTimeOffset? ScryfallCheckedAt,
    DateTimeOffset? ScryfallFileAt,
    DateTimeOffset? AutoImportAt);

public sealed class CardRefreshStore(Database database)
{
    public async Task<CardRefreshState> LoadAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT flag_json, flag_read_at, scryfall_checked_at, scryfall_file_at, auto_import_at
            FROM card_refresh WHERE id = 1
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new CardRefreshState(null, null, null, null, null);

        return new CardRefreshState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            ReadTime(reader, 1),
            ReadTime(reader, 2),
            ReadTime(reader, 3),
            ReadTime(reader, 4));
    }

    /// <summary>A read of card-data.json; a <paramref name="json"/> of null keeps the copy already stored.</summary>
    public Task RecordFlagReadAsync(DateTimeOffset at, string? json, CancellationToken ct = default) =>
        UpsertAsync("""
            INSERT INTO card_refresh (id, flag_json, flag_read_at) VALUES (1, $json, $at)
            ON CONFLICT (id) DO UPDATE SET
                flag_json    = COALESCE(excluded.flag_json, card_refresh.flag_json),
                flag_read_at = excluded.flag_read_at
            """, at, ("$json", json), ct);

    /// <summary>A look at Scryfall's listing; a <paramref name="fileAt"/> of null keeps the date already stored.</summary>
    public Task RecordScryfallCheckAsync(DateTimeOffset at, DateTimeOffset? fileAt, CancellationToken ct = default) =>
        UpsertAsync("""
            INSERT INTO card_refresh (id, scryfall_checked_at, scryfall_file_at) VALUES (1, $at, $file)
            ON CONFLICT (id) DO UPDATE SET
                scryfall_checked_at = excluded.scryfall_checked_at,
                scryfall_file_at    = COALESCE(excluded.scryfall_file_at, card_refresh.scryfall_file_at)
            """, at, ("$file", fileAt is { } file ? Format(file) : null), ct);

    public Task RecordAutoImportAsync(DateTimeOffset at, CancellationToken ct = default) =>
        UpsertAsync("""
            INSERT INTO card_refresh (id, auto_import_at) VALUES (1, $at)
            ON CONFLICT (id) DO UPDATE SET auto_import_at = excluded.auto_import_at
            """, at, null, ct);

    private async Task UpsertAsync(string sql, DateTimeOffset at, (string Name, string? Value)? extra, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$at", Format(at));
        if (extra is { } parameter) command.Parameters.AddWithValue(parameter.Name, (object?)parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal)
        && DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;
}
