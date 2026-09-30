using System.Globalization;
using Microsoft.Data.Sqlite;
using MtgaCollectionAdvisor.Core.Storage;

namespace MtgaCollectionAdvisor.Core.Notices;

/// <summary>The last good notices.json and when it was last asked for (#96).</summary>
public sealed record NoticeFeed(string? Json, DateTimeOffset? FetchedAt, DateTimeOffset? LastAttemptAt);

/// <summary>The stored notices.json, and the notices the player dismissed, which never come back.</summary>
public sealed class NoticeStore(Database database)
{
    public async Task<NoticeFeed> LoadFeedAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json, fetched_at, last_attempt_at FROM notice_feed WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new NoticeFeed(null, null, null);

        return new NoticeFeed(reader.IsDBNull(0) ? null : reader.GetString(0), ReadTime(reader, 1), ReadTime(reader, 2));
    }

    /// <summary>Records an attempt; a <paramref name="json"/> of null keeps the copy already stored.</summary>
    public async Task RecordAttemptAsync(DateTimeOffset at, string? json, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notice_feed (id, json, fetched_at, last_attempt_at)
            VALUES (1, $json, CASE WHEN $json IS NULL THEN NULL ELSE $at END, $at)
            ON CONFLICT (id) DO UPDATE SET
                json            = COALESCE(excluded.json, notice_feed.json),
                fetched_at      = COALESCE(excluded.fetched_at, notice_feed.fetched_at),
                last_attempt_at = excluded.last_attempt_at
            """;
        command.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", Format(at));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlySet<string>> LoadDismissedAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM dismissed_notices";
        await using var reader = await command.ExecuteReaderAsync(ct);

        var ids = new HashSet<string>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>Dismisses a notice for good; dismissing it again keeps the first time.</summary>
    public async Task DismissAsync(string id, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO dismissed_notices (id, dismissed_at) VALUES ($id, $at)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", Format(at));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal)
        && DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;
}
