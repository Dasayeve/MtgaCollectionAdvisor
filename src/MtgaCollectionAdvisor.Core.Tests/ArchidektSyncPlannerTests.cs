using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// What a deck fetch asks Archidekt for. Every rule here either keeps the pool current or
/// keeps the request count down - the source must never see a burst from this app.
/// </summary>
public class ArchidektSyncPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = ArchidektSyncPlanner.CutoffFor(Now);
    private static readonly HashSet<string> NoPins = [];
    private static readonly Dictionary<string, DateTimeOffset> NothingKnown = [];

    [Fact]
    public void Decide_Should_Fetch_NewListing()
    {
        Assert.Equal(ListingDecision.Fetch, ArchidektSyncPlanner.Decide(Listing(1), NothingKnown, NoPins, Cutoff));
    }

    [Fact]
    public void Decide_Should_Skip_UnchangedListing()
    {
        var listing = Listing(1);
        var known = new Dictionary<string, DateTimeOffset> { [listing.SourceId] = listing.UpdatedAt };

        Assert.Equal(ListingDecision.Unchanged, ArchidektSyncPlanner.Decide(listing, known, NoPins, Cutoff));
    }

    [Fact]
    public void Decide_Should_Fetch_ChangedListing()
    {
        var listing = Listing(1);
        var known = new Dictionary<string, DateTimeOffset> { [listing.SourceId] = listing.UpdatedAt.AddHours(-1) };

        Assert.Equal(ListingDecision.Fetch, ArchidektSyncPlanner.Decide(listing, known, NoPins, Cutoff));
    }

    [Fact]
    public void Decide_Should_NeverFetch_PinnedDeck_EvenWhenChanged()
    {
        var listing = Listing(1);
        var known = new Dictionary<string, DateTimeOffset> { [listing.SourceId] = listing.UpdatedAt.AddDays(-3) };

        Assert.Equal(ListingDecision.Pinned,
            ArchidektSyncPlanner.Decide(listing, known, new HashSet<string> { listing.SourceId }, Cutoff));
    }

    [Fact]
    public void Decide_Should_Skip_ListingWithFewViews()
    {
        var listing = Listing(1, views: ArchidektSyncPlanner.MinimumViews - 1);

        Assert.Equal(ListingDecision.TooFewViews, ArchidektSyncPlanner.Decide(listing, NothingKnown, NoPins, Cutoff));
    }

    [Fact]
    public void Decide_Should_Fetch_ListingAtExactlyMinimumViews()
    {
        var listing = Listing(1, views: ArchidektSyncPlanner.MinimumViews);

        Assert.Equal(ListingDecision.Fetch, ArchidektSyncPlanner.Decide(listing, NothingKnown, NoPins, Cutoff));
    }

    [Fact]
    public void Decide_Should_StopAt_ListingOutsideWindow_EvenWhenPinned()
    {
        var listing = Listing(1, daysAgo: 91);

        Assert.Equal(ListingDecision.OutsideWindow,
            ArchidektSyncPlanner.Decide(listing, NothingKnown, new HashSet<string> { listing.SourceId }, Cutoff));
    }

    [Fact]
    public void MayStopEarly_Should_NeedARecentFullWalk()
    {
        Assert.False(ArchidektSyncPlanner.MayStopEarly(null, Now));
        Assert.True(ArchidektSyncPlanner.MayStopEarly(Now.AddDays(-1), Now));
        Assert.False(ArchidektSyncPlanner.MayStopEarly(Now.AddDays(-7), Now));
    }

    [Fact]
    public void CooldownRemaining_Should_BlockARepeatWithinFiveMinutes()
    {
        Assert.Null(ArchidektSyncPlanner.CooldownRemaining(null, Now));
        Assert.Equal(TimeSpan.FromMinutes(3), ArchidektSyncPlanner.CooldownRemaining(Now.AddMinutes(-2), Now));
        Assert.Null(ArchidektSyncPlanner.CooldownRemaining(Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void Describe_Should_SayNothingIsNew_When_ThePoolDidNotChange()
    {
        var report = new DeckSyncReport(0, 0, 0, PoolSize: 42, DetailRequests: 0, CutShort: false, null, null);

        Assert.Equal("No new decks on Archidekt since the last fetch. 42 decks in the pool.", report.Describe());
    }

    [Fact]
    public void Describe_Should_ReportTheCooldown_When_NothingWasSent()
    {
        var report = new DeckSyncReport(0, 0, 0, 0, 0, false, null, CooldownRemaining: TimeSpan.FromSeconds(130));

        Assert.Equal("Archidekt was checked moments ago - try again in 3 min.", report.Describe());
    }

    [Fact]
    public void Describe_Should_SayWhyAFetchStopped()
    {
        var report = new DeckSyncReport(4, 1, 0, 30, 5, false, StoppedBecause: "Could not read deck 7 from Archidekt.", null);

        Assert.Equal(
            "Archidekt: 4 new, 1 updated, 0 removed. 30 decks in the pool. Stopped early, kept what was read: Could not read deck 7 from Archidekt.",
            report.Describe());
    }

    // ---------- One click, every format (#124) ----------

    private static DeckSyncReport Fetched(int added = 0, int pool = 10, bool capped = false) =>
        new(added, 0, 0, pool, 0, capped, StoppedBecause: null, CooldownRemaining: null);

    private static DeckSyncReport OnCooldown(int minutes) =>
        new(0, 0, 0, 0, 0, false, null, CooldownRemaining: TimeSpan.FromMinutes(minutes));

    private static DeckSyncReport Failed(string reason) =>
        new(1, 0, 0, 5, 1, false, StoppedBecause: reason, CooldownRemaining: null);

    private static (FormatDefinition, DateTimeOffset?)[] NeverFetched() =>
        [.. Formats.All.Select(f => (f, (DateTimeOffset?)null))];

    private static async Task<(MultiFormatSyncReport Report, List<string> Called)> Run(
        IReadOnlyList<(FormatDefinition, DateTimeOffset?)> formats, Func<FormatDefinition, DeckSyncReport> result)
    {
        var called = new List<string>();
        var report = await ArchidektSyncPlanner.SyncFormatsAsync(formats, format =>
        {
            called.Add(format.Key);
            return Task.FromResult(result(format));
        });
        return (report, called);
    }

    [Fact]
    public void FormatOrder_Should_PutNeverFetchedFirst_ThenOldest()
    {
        var order = ArchidektSyncPlanner.FormatOrder(
        [
            (Formats.Standard, Now.AddMinutes(-10)),
            (Formats.Pioneer, Now.AddDays(-3)),
            (Formats.Brawl, null),
            (Formats.StandardBrawl, Now.AddDays(-40)),
        ]);

        Assert.Equal([Formats.Brawl, Formats.StandardBrawl, Formats.Pioneer, Formats.Standard], order);
    }

    [Fact]
    public void FormatOrder_Should_KeepTheGivenOrder_ForTies()
    {
        Assert.Equal(Formats.All, ArchidektSyncPlanner.FormatOrder(NeverFetched()));
        Assert.Equal([Formats.Pioneer, Formats.Standard],
            ArchidektSyncPlanner.FormatOrder([(Formats.Pioneer, Now), (Formats.Standard, Now)]));
    }

    [Fact]
    public async Task SyncFormats_Should_RunEveryFormat_InOrder()
    {
        var (report, called) = await Run(NeverFetched(), _ => Fetched());

        Assert.Equal(Formats.All.Select(f => f.Key), called);
        Assert.Empty(report.NotReached);
        Assert.Null(report.Stopped);
    }

    [Fact]
    public async Task SyncFormats_Should_SkipCooldown_AndGoOn()
    {
        var (report, called) = await Run(NeverFetched(), f => f == Formats.Pioneer ? OnCooldown(3) : Fetched());

        Assert.Equal(4, called.Count);
        Assert.Empty(report.NotReached);
    }

    [Fact]
    public async Task SyncFormats_Should_StopAtTheFirstFailure()
    {
        var (report, called) = await Run(NeverFetched(), f => f == Formats.Pioneer ? Failed("HTTP 429") : Fetched());

        Assert.Equal([Formats.Standard.Key, Formats.Pioneer.Key], called);
        Assert.Equal([Formats.Brawl, Formats.StandardBrawl], report.NotReached);
        Assert.Equal(Formats.Pioneer, report.Stopped?.Format);
    }

    [Fact]
    public async Task SyncFormats_Should_GoOn_AfterACappedFormat()
    {
        var (report, called) = await Run(NeverFetched(), f => f == Formats.Standard ? Fetched(capped: true) : Fetched());

        Assert.Equal(4, called.Count);
        Assert.Null(report.Stopped);
    }

    [Fact]
    public async Task Describe_Should_SumChanges_AndNameSkippedAndNotReached()
    {
        var (report, _) = await Run(NeverFetched(), f =>
            f == Formats.Standard ? Fetched(added: 3, pool: 869, capped: true) :
            f == Formats.Pioneer ? OnCooldown(2) :
            f == Formats.Brawl ? Failed("Archidekt answered 429 (too many requests).") :
            Fetched());

        Assert.Equal(
            "Archidekt: 4 new, 0 updated, 0 removed. Pool: Standard 869, Brawl 5. Checked moments ago: Pioneer. "
            + "More remain for Standard - fetch again to continue. "
            + "Stopped early at Brawl, kept what was read: Archidekt answered 429 (too many requests). Not reached: Standard Brawl.",
            report.Describe());
    }

    [Fact]
    public async Task Describe_Should_SayTryAgain_WhenEveryFormatIsOnCooldown()
    {
        var (report, _) = await Run(NeverFetched(), f => OnCooldown(f == Formats.Brawl ? 1 : 4));

        Assert.Equal("Archidekt was checked moments ago - try again in 1 min.", report.Describe());
    }

    private static ArchidektListing Listing(int id, int views = 500, int daysAgo = 2) =>
        new(id, $"Deck {id}", views, Now.AddDays(-daysAgo));
}
