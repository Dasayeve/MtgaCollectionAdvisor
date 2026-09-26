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
        if (CooldownRemaining is { } wait)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
            return $"Archidekt was checked moments ago - try again in {minutes} min.";
        }

        var changes = Added + Updated + Removed == 0
            ? "No new decks on Archidekt since the last fetch."
            : $"Archidekt: {Added} new, {Updated} updated, {Removed} removed.";

        var summary = $"{changes} {PoolSize} decks in the pool.";
        if (StoppedBecause is not null) return $"{summary} Stopped early, kept what was read: {StoppedBecause}";
        if (CutShort) return $"{summary} More remain - fetch again to continue.";
        return summary;
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
    public async Task<DeckSyncReport> SyncAsync(
        FormatDefinition format, IProgress<string> progress, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var state = await decks.LoadSyncStateAsync(format, ct);

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

        int added = 0, updated = 0, detailRequests = 0, requests = 0;
        var fullWalk = false;
        var capped = false;
        string? stoppedBecause = null;
        string? stopCause = null;

        async Task PauseAsync()
        {
            if (requests++ > 0) await Task.Delay(ArchidektSyncPlanner.RequestSpacing, ct);
        }

        for (var page = 1; ; page++)
        {
            await PauseAsync();
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
                if (detailRequests >= ArchidektSyncPlanner.MaxDetailRequests)
                {
                    capped = true;
                    break;
                }

                await PauseAsync();
                detailRequests++;
                progress.Report($"Reading deck {detailRequests} (of up to {ArchidektSyncPlanner.MaxDetailRequests}) from Archidekt: {listing.Name}");

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
}
