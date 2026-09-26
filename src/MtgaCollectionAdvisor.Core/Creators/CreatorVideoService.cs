using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Hosting;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Creators;

public sealed record CreatorVideoRefreshResult(
    IReadOnlyList<CreatorVideo> Videos,
    bool FromCache,
    IReadOnlyList<FeedFailure> FeedFailures,
    int ArchidektFetches)
{
    public int FailedFeeds => FeedFailures.Count;
}

/// <summary>A channel whose feed failed this time, why, and how many times in a row (#74).</summary>
public sealed record FeedFailure(string Creator, string Reason, int ConsecutiveFailures);

/// <summary>
/// Brings creator videos in and prices their decks. The rules - what survives a refresh,
/// which format a deck is priced under - live in <see cref="CreatorVideoMerge"/> and
/// <see cref="CreatorVideoPricing"/>; this only does the I/O around them.
/// </summary>
public sealed class CreatorVideoService(
    YouTubeFeedClient feeds,
    ArchidektClient archidekt,
    CreatorVideoStore store,
    DeckRankingService ranking)
{
    /// <summary>Spacing between feed requests: one at a time, never a burst YouTube could read as abuse.</summary>
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Fetches only the channels whose feed is due (<see cref="CreatorFeedSchedule"/>), one
    /// at a time, and keeps the rest from the cache; <paramref name="force"/> - the user's
    /// Refresh - asks every channel. A feed or Archidekt failure never throws: it leaves
    /// what the cache already knew and pushes that channel's next attempt back.
    /// <paramref name="channels"/> is the curated list (<see cref="CreatorRoster"/>); videos of a
    /// creator no longer on it are not returned.
    /// </summary>
    public async Task<CreatorVideoRefreshResult> LoadAsync(
        IReadOnlyList<CreatorChannel> channels, bool force, CancellationToken ct = default)
    {
        var cached = await store.LoadAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var curated = channels.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        var due = channels
            .Where(c => force || CreatorFeedSchedule.IsDue(cached.FeedOf(c.Name), now))
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (due.Count == 0)
        {
            var kept = cached.Videos.Where(v => curated.Contains(v.Creator)).ToList();
            return new CreatorVideoRefreshResult(kept, FromCache: true, FeedFailures: [], ArchidektFetches: 0);
        }

        var results = new List<ChannelFeedResult>(channels.Count);
        var feedStates = new Dictionary<string, CreatorFeedState>(StringComparer.Ordinal);
        var asked = 0;
        var failures = new List<FeedFailure>();

        foreach (var channel in channels)
        {
            if (!due.Contains(channel.Name))
            {
                // Not asked this time: a null result makes Merge keep what the cache has.
                results.Add(new ChannelFeedResult(channel, null));
                if (cached.FeedOf(channel.Name) is { } kept) feedStates[channel.Name] = kept;
                continue;
            }

            if (asked++ > 0) await Task.Delay(RequestSpacing, ct);

            IReadOnlyList<FeedVideo>? videos;
            string? failure = null;
            try
            {
                videos = await feeds.FetchAsync(channel, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                videos = null;
                failure = FailureText.Describe(ex);
            }

            results.Add(new ChannelFeedResult(channel, videos));
            var state = CreatorFeedSchedule.Record(
                cached.FeedOf(channel.Name), channel.Name, succeeded: videos is not null, DateTimeOffset.UtcNow);
            feedStates[channel.Name] = state;
            if (failure is not null) failures.Add(new FeedFailure(channel.Name, failure, state.ConsecutiveFailures));
        }

        var merged = CreatorVideoMerge.Merge(cached.Videos, results, channels).ToList();

        var archidektFetches = 0;
        for (var i = 0; i < merged.Count; i++)
        {
            if (!merged[i].NeedsArchidektFetch) continue;

            archidektFetches++;
            // The format only decides legality flags on the fetched deck; the list itself is
            // re-priced under every format below, so Standard is as good as any.
            var deck = await archidekt.TryFetchDeckAsync(merged[i].ArchidektId!.Value, Formats.Standard, ct);
            if (deck is not null)
            {
                merged[i] = merged[i] with { Decklist = ArenaDeckListWriter.Write(deck) };
            }
        }

        await store.ReplaceAsync(new CreatorVideoSnapshot(merged, feedStates), ct);

        return new CreatorVideoRefreshResult(
            merged,
            FromCache: false,
            FeedFailures: failures,
            ArchidektFetches: archidektFetches);
    }

    /// <summary>
    /// One ranking pass per format over every video's deck, never one per video: a pass
    /// shares its card-name lookups, so each card is resolved once, not once per deck.
    /// </summary>
    public async Task<IReadOnlyList<CreatorVideoCard>> PriceAsync(
        IReadOnlyList<CreatorVideo> videos, CollectionSnapshot collection, CancellationToken ct = default)
    {
        var drafts = videos
            .Where(v => v.Decklist is not null)
            .Select(v => new CandidateDeck(
                SourceId: CreatorVideoPricing.DraftSourceId(v.VideoId),
                Name: v.Title,
                Url: v.Url,
                FormatKey: Formats.Standard.Key,
                Popularity: 0,
                Cards: ArenaDeckListParser.Parse(v.Decklist!),
                FetchedAt: v.Published))
            .Where(d => d.Cards.Count > 0)
            .ToList();

        var perVideo = new Dictionary<string, List<(FormatDefinition, DeckAnalysisResult)>>(StringComparer.Ordinal);
        if (drafts.Count > 0)
        {
            foreach (var format in Formats.All)
            {
                foreach (var result in await ranking.RankAsync(format, drafts, collection, ct))
                {
                    if (!perVideo.TryGetValue(result.Deck.SourceId, out var list))
                    {
                        perVideo[result.Deck.SourceId] = list = [];
                    }
                    list.Add((format, result));
                }
            }
        }

        return videos
            .Select(v =>
            {
                if (!perVideo.TryGetValue(CreatorVideoPricing.DraftSourceId(v.VideoId), out var priced))
                {
                    return new CreatorVideoCard(v, null, null);
                }

                var (format, analysis) = CreatorVideoPricing.PickBest(priced);
                return new CreatorVideoCard(v, format, analysis);
            })
            .ToList();
    }
}
