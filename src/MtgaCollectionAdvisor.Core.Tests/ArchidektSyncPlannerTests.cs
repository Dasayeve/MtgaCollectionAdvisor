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

    [Fact]
    public void VersionsToKeep_Should_DropStoredDecksOfTheWrongShape_SoTheyAreReadAgain()
    {
        // #125: stored before the shape check, e.g. counted with a "Maybe Draw" category.
        static CandidateDeck Stored(string id, int main) => new($"archidekt:{id}", id, "", Formats.StandardBrawl.Key, 0,
            [new DeckCardRef("Commander", 1, DeckBoard.Commander), new DeckCardRef("Island", main, DeckBoard.Main)],
            DateTimeOffset.UnixEpoch);
        var known = new Dictionary<string, DateTimeOffset> { ["archidekt:fits"] = Now, ["archidekt:63"] = Now, ["archidekt:gone"] = Now };

        var kept = ArchidektSyncPlanner.VersionsToKeep(known, [Stored("fits", 59), Stored("63", 62)], Formats.StandardBrawl);

        Assert.Equal(["archidekt:fits", "archidekt:gone"], kept.Keys.Order());
        Assert.Equal(ListingDecision.Fetch, ArchidektSyncPlanner.Decide(Listing(63), kept, NoPins, Cutoff));
        Assert.Equal(ListingDecision.Unchanged, ArchidektSyncPlanner.Decide(Listing(63), known, NoPins, Cutoff));
    }

    private static ArchidektListing Listing(int id, int views = 500, int daysAgo = 2) =>
        new(id, $"Deck {id}", views, Now.AddDays(-daysAgo));
}
