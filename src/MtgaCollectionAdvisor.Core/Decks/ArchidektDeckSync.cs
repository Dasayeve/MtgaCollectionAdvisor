using MtgaCollectionAdvisor.Core.Hosting;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Decks;

/// <summary>
/// What one deck fetch did. <see cref="CooldownRemaining"/> set means nothing was sent.
/// <see cref="StopCause"/> is why a stopped fetch stopped (an HTTP status, a timeout), for the log.
/// </summary>
public sealed record DeckSyncReport(
    int Added,
    int Updated,
    int Removed,
    int PoolSize,
    int DetailRequests,
    bool CutShort,
    string? StoppedBecause,
    TimeSpan? CooldownRemaining,
    string? StopCause = null)
{
    /// <summary>
    /// Why a fetch left the pool empty, for the first-run setup, where "no new decks since the
    /// last fetch" would read as success. There was no last fetch.
    /// </summary>
    public string DescribeEmptyPool()
    {
        if (CooldownRemaining is not null) return Describe();
        if (StoppedBecause is not null) return $"Could not reach Archidekt: {StoppedBecause}";
        return "Archidekt returned no recent Standard decks.";
    }

    /// <summary>The line the status bar shows once the fetch is over.</summary>
    public string Describe()
    {
        if (CooldownRemaining is { } wait) return DescribeCooldown(wait);

        var summary = $"{DescribeChanges(Added, Updated, Removed)} {PoolSize} decks in the pool.";
        if (StoppedBecause is not null) return $"{summary} Stopped early, kept what was read: {StoppedBecause}";
        if (CutShort) return $"{summary} More remain - fetch again to continue.";
        return summary;
    }

    internal static string DescribeCooldown(TimeSpan wait)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return $"Archidekt was checked moments ago - try again in {minutes} min.";
    }

    internal static string DescribeChanges(int added, int updated, int removed) =>
        added + updated + removed == 0
            ? "No new decks on Archidekt since the last fetch."
            : $"Archidekt: {added} new, {updated} updated, {removed} removed.";
}

/// <summary>What one fetch did for one format, within a run over all of them (#124).</summary>
public sealed record FormatSyncReport(FormatDefinition Format, DeckSyncReport Report);

/// <summary>
/// What one click on Fetch decks did across the formats (#124): those it ran, in order, and those
/// it didn't reach, because a read failed or the click's detail requests ran out.
/// <see cref="CooldownRemaining"/> set means nothing was sent.
/// </summary>
public sealed record MultiFormatSyncReport(
    IReadOnlyList<FormatSyncReport> Ran,
    IReadOnlyList<FormatDefinition> NotReached,
    TimeSpan? CooldownRemaining = null)
{
    /// <summary>The format whose read failed and ended the run; null when none did.</summary>
    public FormatSyncReport? Stopped => Ran.LastOrDefault(r => r.Report.StoppedBecause is not null);

    /// <summary>The line the status bar shows once the run is over.</summary>
    public string Describe()
    {
        if (CooldownRemaining is { } wait) return DeckSyncReport.DescribeCooldown(wait);

        var parts = new List<string>
        {
            DeckSyncReport.DescribeChanges(
                Ran.Sum(r => r.Report.Added), Ran.Sum(r => r.Report.Updated), Ran.Sum(r => r.Report.Removed)),
            $"Pool: {string.Join(", ", Ran.Select(r => $"{r.Format.DisplayName} {r.Report.PoolSize}"))}.",
        };

        if (Stopped is { } stopped)
        {
            var stop = $"Stopped early at {stopped.Format.DisplayName}, kept what was read: {stopped.Report.StoppedBecause}";
            parts.Add(NotReached.Count > 0 ? $"{stop.TrimEnd('.')}. Not reached: {Names(NotReached)}." : stop);
        }
        else
        {
            var remain = Ran.Where(r => r.Report.CutShort).Select(r => r.Format).Concat(NotReached).ToList();
            if (remain.Count > 0) parts.Add($"More remain for {Names(remain)} - fetch again to continue.");
        }
        return string.Join(" ", parts);

        static string Names(IEnumerable<FormatDefinition> formats) => string.Join(", ", formats.Select(f => f.DisplayName));
    }
}

/// <summary>
/// Brings the stored Archidekt decks of a format up to date: walks the search newest first,
/// reads only the decks that are new or changed (<see cref="ArchidektSyncPlanner"/>), merges
/// them into the pool page by page, then drops what fell out of the window and duplicate
/// lists.
///
/// Archidekt is asked politely: one request at a time with a pause between them, a cap on
/// detail requests, a cooldown between fetches, and the first request that fails ends the
/// fetch - whatever was already merged stays.
/// </summary>
public sealed class ArchidektDeckSync(ArchidektClient client, CuratedDeckStore decks, PinnedDeckStore pins)
{
    /// <summary>
    /// One click on Fetch decks (#124): every format, least recently fetched first, as one paced
    /// fetch with one cooldown and one cap (<see cref="ArchidektSyncPlanner.SyncFormatsAsync"/>).
    /// <paramref name="formatDone"/> runs after each format, so the one on screen shows its decks
    /// without waiting for the rest.
    /// </summary>
    public async Task<MultiFormatSyncReport> SyncAllAsync(
        IReadOnlyList<FormatDefinition> formats, IProgress<string> progress, Func<FormatDefinition, Task> formatDone,
        CancellationToken ct = default)
    {
        var states = new Dictionary<string, DeckSyncState>();
        foreach (var format in formats)
        {
            states[format.Key] = await decks.LoadSyncStateAsync(format, ct);
        }

        var pacer = new RequestPacer();
        return await ArchidektSyncPlanner.SyncFormatsAsync(
            [.. formats.Select(f => (f, states[f.Key].LastSyncAt))],
            DateTimeOffset.UtcNow,
            async (format, detailBudget) =>
            {
                var report = await SyncAsync(format, states[format.Key], pacer, detailBudget, progress, ct);
                await formatDone(format);
                return report;
            });
    }

    /// <summary>One format on its own: the first-run setup's fetch.</summary>
    public async Task<DeckSyncReport> SyncAsync(
        FormatDefinition format, IProgress<string> progress, CancellationToken ct = default) =>
        await SyncAsync(format, await decks.LoadSyncStateAsync(format, ct), new RequestPacer(),
            ArchidektSyncPlanner.MaxDetailRequests, progress, ct);

    private async Task<DeckSyncReport> SyncAsync(
        FormatDefinition format, DeckSyncState state, RequestPacer pacer, int detailBudget,
        IProgress<string> progress, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (ArchidektSyncPlanner.CooldownRemaining(state.LastSyncAt, now) is { } wait)
        {
            return new DeckSyncReport(0, 0, 0, 0, 0, CutShort: false, StoppedBecause: null, CooldownRemaining: wait);
        }

        // Recorded before the first request, so a fetch that dies midway still counts
        // towards the cooldown.
        await decks.SaveSyncStateAsync(format, state with { LastSyncAt = now }, ct);

        var cutoff = ArchidektSyncPlanner.CutoffFor(now);
        var known = await decks.LoadSourceVersionsAsync(format, ct);
        var pinned = (await pins.LoadAsync(ct)).Keys.ToHashSet(StringComparer.Ordinal);
        var mayStopEarly = ArchidektSyncPlanner.MayStopEarly(state.FullWalkAt, now);

        int added = 0, updated = 0, detailRequests = 0;
        var fullWalk = false;
        var capped = false;
        string? stoppedBecause = null;
        string? stopCause = null;

        for (var page = 1; ; page++)
        {
            await pacer.PauseAsync(ct);
            progress.Report($"Searching Archidekt ({format.DisplayName}), page {page}...");

            ArchidektSearchPage search;
            try
            {
                search = await client.SearchRecentAsync(format, page, ct);
            }
            catch (ArchidektUnavailableException ex)
            {
                stoppedBecause = ex.Message;
                stopCause = FailureText.Describe(ex);
                break;
            }

            var batch = new List<FetchedDeck>();
            var reachedWindow = false;
            var pageHadWork = false;

            foreach (var listing in search.Decks)
            {
                var decision = ArchidektSyncPlanner.Decide(listing, known, pinned, cutoff);
                if (decision == ListingDecision.OutsideWindow)
                {
                    reachedWindow = true;
                    break;
                }
                if (decision != ListingDecision.Fetch) continue;

                pageHadWork = true;
                if (detailRequests >= detailBudget)
                {
                    capped = true;
                    break;
                }

                await pacer.PauseAsync(ct);
                detailRequests++;
                progress.Report($"Reading deck {detailRequests} (of up to {detailBudget}) from Archidekt: {listing.Name}");

                try
                {
                    var deck = await client.ReadDeckAsync(listing, format, now, ct);
                    batch.Add(new FetchedDeck(listing.SourceId, listing.UpdatedAt, deck));
                }
                catch (ArchidektUnavailableException ex)
                {
                    stoppedBecause = ex.Message;
                    stopCause = FailureText.Describe(ex);
                    break;
                }
            }

            // Page by page, so a fetch that stops or is closed midway keeps what it read.
            var merged = await decks.MergeFetchedAsync(format, batch, ct);
            added += merged.Added;
            updated += merged.Updated;

            if (stoppedBecause is not null || capped) break;

            if (reachedWindow || !search.HasNext || search.Decks.Count == 0)
            {
                fullWalk = true;
                break;
            }

            // Newest first: past a page with nothing new, the rest is what the last full walk saw.
            if (mayStopEarly && !pageHadWork) break;
        }

        var removed = await decks.PruneFetchedAsync(format, ArchidektClient.SourcePrefix, cutoff, ct);
        removed += await RemoveDuplicatesAsync(format, ct);

        // The cooldown runs from the end: a first fetch takes minutes, and counting from its
        // start would let the next one follow almost at once.
        await decks.SaveSyncStateAsync(
            format, new DeckSyncState(DateTimeOffset.UtcNow, fullWalk ? now : state.FullWalkAt), ct);

        var poolSize = (await decks.LoadAsync(format, ct))
            .Count(d => d.SourceId.StartsWith(ArchidektClient.SourcePrefix, StringComparison.Ordinal));

        return new DeckSyncReport(added, updated, removed, poolSize, detailRequests,
            CutShort: capped, stoppedBecause, CooldownRemaining: null, stopCause);
    }

    /// <summary>
    /// Near-identical lists across the whole fetched pool, not just this fetch's batch, since
    /// the pool now grows across fetches. Pinned decks take part (a copy of one goes) but are
    /// never removed themselves; user decks are left out entirely.
    /// </summary>
    private async Task<int> RemoveDuplicatesAsync(FormatDefinition format, CancellationToken ct)
    {
        var fetched = (await decks.LoadAsync(format, ct))
            .Where(d => d.SourceId.StartsWith(ArchidektClient.SourcePrefix, StringComparison.Ordinal))
            .ToList();

        var kept = DeckDeduplicator.Deduplicate(fetched).Select(d => d.SourceId).ToHashSet(StringComparer.Ordinal);
        var duplicates = fetched.Where(d => !kept.Contains(d.SourceId)).Select(d => d.SourceId).ToList();

        return await decks.RemoveFetchedAsync(duplicates, ct);
    }

    /// <summary>
    /// The pause before every request after the first (<see cref="ArchidektSyncPlanner.RequestSpacing"/>),
    /// held across the formats of a click, so the next format's first search isn't a burst either.
    /// </summary>
    private sealed class RequestPacer
    {
        private bool _sent;

        public async Task PauseAsync(CancellationToken ct)
        {
            if (_sent) await Task.Delay(ArchidektSyncPlanner.RequestSpacing, ct);
            _sent = true;
        }
    }
}
