using MtgaCollectionAdvisor.Core.Hosting;

namespace MtgaCollectionAdvisor.Core.Notices;

/// <summary>The notices to choose from, and why a read that was due gave nothing (#74); null when it worked or none was due.</summary>
public sealed record NoticeLoad(IReadOnlyList<Notice> Notices, string? ReadFailure);

/// <summary>
/// Reads the maintainer's notices.json within <see cref="NoticeSchedule"/> (#96) and answers with
/// the notices to choose from. Never throws for a network or file problem: a failed read is "no
/// news", and the stored copy stays, so notices survive an offline start.
/// </summary>
public sealed class NoticeService(HttpClient httpClient, NoticeStore store, string url = NoticeFile.RemoteUrl)
{
    public async Task<NoticeLoad> LoadAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var stored = await store.LoadFeedAsync(ct);
        var storedNotices = NoticeFile.Parse(stored.Json) ?? [];
        if (!NoticeSchedule.IsDue(stored.LastAttemptAt, now)) return new NoticeLoad(storedNotices, null);

        string? json = null;
        string? failure = null;
        try
        {
            json = await httpClient.GetStringAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            failure = $"notices.json: {FailureText.Describe(ex)}";
        }

        var fetched = json is null ? null : NoticeFile.Parse(json);
        if (json is not null && fetched is null) failure = "notices.json is not a notice list";

        await store.RecordAttemptAsync(now, fetched is null ? null : json, ct);
        return new NoticeLoad(fetched ?? storedNotices, failure);
    }
}
