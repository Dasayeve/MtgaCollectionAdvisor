using System.Globalization;
using MtgaCollectionAdvisor.Core.Storage;

namespace MtgaCollectionAdvisor.Core.Creators;

/// <summary>The last good creators.json and when it was last asked for (#64).</summary>
public sealed record StoredRoster(string? Json, DateTimeOffset? FetchedAt, DateTimeOffset? LastAttemptAt);

public sealed class CreatorRosterStore(Database database)
{
    public async Task<StoredRoster> LoadAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT json, fetched_at, last_attempt_at FROM creator_roster WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new StoredRoster(null, null, null);

        return new StoredRoster(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            ReadTime(reader, 1),
            ReadTime(reader, 2));
    }

    /// <summary>Records an attempt; a <paramref name="json"/> of null keeps the copy already stored.</summary>
    public async Task RecordAttemptAsync(DateTimeOffset at, string? json, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO creator_roster (id, json, fetched_at, last_attempt_at)
            VALUES (1, $json, CASE WHEN $json IS NULL THEN NULL ELSE $at END, $at)
            ON CONFLICT (id) DO UPDATE SET
                json            = COALESCE(excluded.json, creator_roster.json),
                fetched_at      = COALESCE(excluded.fetched_at, creator_roster.fetched_at),
                last_attempt_at = excluded.last_attempt_at
            """;
        command.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DateTimeOffset? ReadTime(Microsoft.Data.Sqlite.SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal)
        && DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;
}

/// <summary>
/// Reads the maintainer's creators.json at most once a day (#64) and answers with the list to
/// use. Never throws for a network or file problem: a failed read is "no news", and the list
/// stays whatever was stored, or the compiled one.
/// </summary>
public sealed class CreatorRosterService(HttpClient httpClient, CreatorRosterStore store, string url = CreatorRoster.RemoteUrl)
{
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MtgaCollectionAdvisor/1.0");
        return client;
    }

    public async Task<IReadOnlyList<CreatorChannel>> LoadAsync(CancellationToken ct = default)
    {
        var stored = await store.LoadAsync(ct);
        var storedList = CreatorRoster.Parse(stored.Json);

        var now = DateTimeOffset.UtcNow;
        if (!CreatorRoster.IsDue(stored.LastAttemptAt, now)) return CreatorRoster.Choose(null, storedList);

        string? json = null;
        try
        {
            json = await httpClient.GetStringAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 404 while the repository is private, or GitHub unreachable: keep what we have.
        }

        var fetched = CreatorRoster.Parse(json);
        await store.RecordAttemptAsync(now, fetched is null ? null : json, ct);
        return CreatorRoster.Choose(fetched, storedList);
    }
}
