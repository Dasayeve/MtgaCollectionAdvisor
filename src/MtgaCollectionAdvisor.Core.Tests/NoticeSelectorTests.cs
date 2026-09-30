using MtgaCollectionAdvisor.Core.Notices;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #96: a notice lasts 30 days, or until a newer one starts on this copy; an explicit showUntil
/// replaces both. The active ones show one at a time, oldest first, until dismissed.
/// </summary>
public sealed class NoticeSelectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static Notice At(string id, int day, int? untilDay = null, string? set = null) =>
        new(id, $"Title {id}", "Text", Start.AddDays(day), untilDay is { } until ? Start.AddDays(until) : null, set);

    private static NoticeContext Context(int day, IEnumerable<string>? sets = null, IEnumerable<string>? dismissed = null)
    {
        var owned = (sets ?? ["fra"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new NoticeContext(Start.AddDays(day), owned.Contains, (dismissed ?? []).ToHashSet());
    }

    private static string? Next(IReadOnlyList<Notice> notices, NoticeContext context) =>
        NoticeSelector.Next(notices, context)?.Id;

    [Fact]
    public void A_notice_shows_from_showFrom_for_thirty_days()
    {
        Notice[] notices = [At("a", day: 0)];

        Assert.Null(Next(notices, Context(-1)));
        Assert.Equal("a", Next(notices, Context(0)));
        Assert.Equal("a", Next(notices, Context(29)));
        Assert.Null(Next(notices, Context(30)));
    }

    [Fact]
    public void An_explicit_showUntil_replaces_the_thirty_days()
    {
        Assert.Null(Next([At("short", day: 0, untilDay: 5)], Context(5)));
        Assert.Equal("long", Next([At("long", day: 0, untilDay: 60)], Context(45)));
    }

    [Fact]
    public void An_ended_notice_never_shows_even_if_never_dismissed()
    {
        Notice[] notices = [At("a", day: 0, untilDay: 3)];

        Assert.Null(Next(notices, Context(10, dismissed: [])));
    }

    [Fact]
    public void A_newer_notice_ends_the_older_ones_once_it_starts()
    {
        Notice[] notices = [At("old", day: 0), At("new", day: 5)];

        Assert.Equal("old", Next(notices, Context(4)));
        Assert.Equal("new", Next(notices, Context(5)));
        // Dismissing the newer one doesn't bring the older one back.
        Assert.Null(Next(notices, Context(6, dismissed: ["new"])));
    }

    [Fact]
    public void A_newer_notice_waiting_on_a_set_ends_nothing()
    {
        Notice[] notices = [At("old", day: 0), At("new-set", day: 5, set: "abc")];

        // This copy's Arena hasn't brought "abc" yet: the older notice stays.
        Assert.Equal("old", Next(notices, Context(6, sets: ["fra"])));
        Assert.Equal("new-set", Next(notices, Context(6, sets: ["fra", "abc"])));
    }

    [Fact]
    public void A_newer_notice_does_not_end_one_with_showUntil()
    {
        Notice[] notices = [At("kept", day: 0, untilDay: 20), At("new", day: 5)];

        Assert.Equal("kept", Next(notices, Context(6)));
        Assert.Equal("new", Next(notices, Context(6, dismissed: ["kept"])));
    }

    [Fact]
    public void RequiresSet_waits_for_the_sets_cards()
    {
        Notice[] notices = [At("fra-cards", day: 0, set: "fra")];

        Assert.Null(Next(notices, Context(1, sets: [])));
        Assert.Equal("fra-cards", Next(notices, Context(1, sets: ["FRA"])));
    }

    [Fact]
    public void A_dismissed_notice_never_shows_again()
    {
        Notice[] notices = [At("a", day: 0)];

        Assert.Null(Next(notices, Context(1, dismissed: ["a"])));
    }

    [Fact]
    public void Next_is_the_oldest_active_notice()
    {
        // Out of order in the file, and two starting together: by showFrom, then file order.
        Notice[] notices = [At("third", day: 2, untilDay: 30), At("first", day: 0, untilDay: 30), At("second", day: 0, untilDay: 30)];

        Assert.Equal("first", Next(notices, Context(3)));
        Assert.Equal("second", Next(notices, Context(3, dismissed: ["first"])));
        Assert.Equal("third", Next(notices, Context(3, dismissed: ["first", "second"])));
        Assert.Null(Next(notices, Context(3, dismissed: ["first", "second", "third"])));
    }

    [Fact]
    public void IsDue_after_six_hours_only()
    {
        Assert.True(NoticeSchedule.IsDue(null, Start));
        Assert.False(NoticeSchedule.IsDue(Start.AddHours(-6).AddMinutes(1), Start));
        Assert.True(NoticeSchedule.IsDue(Start.AddHours(-6), Start));
    }
}
