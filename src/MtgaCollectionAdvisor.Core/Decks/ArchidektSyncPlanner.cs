using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Decks;

public enum ListingDecision
{
    /// <summary>New, or changed since it was last read: worth a detail request.</summary>
    Fetch,

    /// <summary>Read before, and not changed since.</summary>
    Unchanged,

    /// <summary>The user is tracking it: a fetch never touches a pinned deck.</summary>
    Pinned,

    /// <summary>Too few views to spend a request on. Not recorded, so a later walk looks again.</summary>
    TooFewViews,

    /// <summary>Last updated before the window; everything after it on the search is older still.</summary>
    OutsideWindow,
}

/// <summary>
/// What a deck fetch asks Archidekt for, and when it stops asking. Kept free of HTTP and
/// storage so the rules that protect the source can be tested on their own.
///
/// The search is walked newest first, so the first listing outside the window ends the
/// walk. Only new or changed decks cost a detail request; the rest of the pool is already
/// stored.
/// </summary>
public static class ArchidektSyncPlanner
{
    /// <summary>How far back a deck's last update may be for it to be considered at all.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(90);

    /// <summary>Below this, a listing is a scratch list or a deck nobody has looked at yet.</summary>
    public const int MinimumViews = 25;

    /// <summary>
    /// Detail requests per click on Fetch decks, shared by every format it reaches (#124), and per
    /// setup fetch. A first fetch on an empty pool is bounded by this. Search pages add at most 17
    /// per format (Archidekt lists 1000 results, 60 a page).
    /// </summary>
    public const int MaxDetailRequests = 150;

    /// <summary>
    /// The pause before every request after the first, so a fetch is never a burst. The same
    /// pace the Creators tab reads Archidekt at; a failed read (a 429 included) stops the
    /// fetch and keeps what was read. Archidekt publishes no limit, and a block would hit
    /// every player, so faster is a bet this does not take.
    /// </summary>
    public static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(300);

    /// <summary>A fetch this soon after the last one for the same format sends nothing.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a full walk vouches for the pool. Within it, a fetch may stop at the first
    /// page with nothing new; past it, the walk goes on to the window to pick up decks that
    /// were skipped for their views, or that an earlier fetch never reached.
    /// </summary>
    public static readonly TimeSpan FullWalkValidity = TimeSpan.FromDays(7);

    public static DateTimeOffset CutoffFor(DateTimeOffset now) => now - Window;

    public static ListingDecision Decide(
        ArchidektListing listing,
        IReadOnlyDictionary<string, DateTimeOffset> knownVersions,
        IReadOnlySet<string> pinned,
        DateTimeOffset cutoff)
    {
        if (listing.UpdatedAt < cutoff) return ListingDecision.OutsideWindow;
        if (pinned.Contains(listing.SourceId)) return ListingDecision.Pinned;

        if (knownVersions.TryGetValue(listing.SourceId, out var known) && listing.UpdatedAt <= known)
        {
            return ListingDecision.Unchanged;
        }

        return listing.ViewCount < MinimumViews ? ListingDecision.TooFewViews : ListingDecision.Fetch;
    }

    public static bool MayStopEarly(DateTimeOffset? fullWalkAt, DateTimeOffset now) =>
        fullWalkAt is { } walked && now - walked < FullWalkValidity;

    /// <summary>Time left before another fetch may go out, or null when it may go out now.</summary>
    public static TimeSpan? CooldownRemaining(DateTimeOffset? lastSyncAt, DateTimeOffset now)
    {
        if (lastSyncAt is not { } last) return null;
        var remaining = Cooldown - (now - last);
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <summary>
    /// The order one click walks the formats in (#124): never fetched first, then the least
    /// recently fetched; ties in the order given. A failed read ends the run, so a fixed order
    /// would keep filling the same format and starve the rest; this way repeated clicks reach
    /// every format in turn.
    /// </summary>
    public static IReadOnlyList<FormatDefinition> FormatOrder(
        IReadOnlyList<(FormatDefinition Format, DateTimeOffset? LastSyncAt)> formats) =>
        [.. formats
            .Select((f, index) => (f.Format, f.LastSyncAt, index))
            .OrderBy(f => f.LastSyncAt is null ? 0 : 1)
            .ThenBy(f => f.LastSyncAt ?? DateTimeOffset.MinValue)
            .ThenBy(f => f.index)
            .Select(f => f.Format)];

    /// <summary>
    /// Fetches each format in <see cref="FormatOrder"/>, as one fetch with one cooldown and one cap:
    /// <list type="bullet">
    /// <item>Nothing is sent within <see cref="Cooldown"/> of the last format fetched. A run a failed
    /// read (a 429 included) ended would otherwise go on at the next click with the formats it never
    /// reached: a retry loop under another name.</item>
    /// <item>The formats share <see cref="MaxDetailRequests"/>; once it is spent, the rest wait for
    /// the next click, which reaches them first.</item>
    /// <item>A failed read ends the run, keeping what was read.</item>
    /// </list>
    /// <paramref name="sync"/> is given the detail requests the format may still send.
    /// </summary>
    public static async Task<MultiFormatSyncReport> SyncFormatsAsync(
        IReadOnlyList<(FormatDefinition Format, DateTimeOffset? LastSyncAt)> formats,
        DateTimeOffset now,
        Func<FormatDefinition, int, Task<DeckSyncReport>> sync)
    {
        var order = FormatOrder(formats);
        if (CooldownRemaining(formats.Max(f => f.LastSyncAt), now) is { } wait)
        {
            return new MultiFormatSyncReport([], order, wait);
        }

        var ran = new List<FormatSyncReport>();
        var left = MaxDetailRequests;
        foreach (var format in order)
        {
            if (left <= 0) break;
            var report = await sync(format, left);
            ran.Add(new FormatSyncReport(format, report));
            left -= report.DetailRequests;
            if (report.StoppedBecause is not null) break;
        }
        return new MultiFormatSyncReport(ran, [.. order.Skip(ran.Count)]);
    }
}
