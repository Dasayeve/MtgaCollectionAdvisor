using MtgaCollectionAdvisor.Core.Cards;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #89: the card database refreshes itself once per new set - never without a reason, never
/// twice for one flag, and never more often than the schedule allows.
/// </summary>
public sealed class CardRefreshScheduleTests
{
    private static readonly DateTimeOffset Flag = new(2026, 9, 30, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Flag.AddHours(10);

    [Theory]
    [InlineData("""{ "refreshCardsAfter": "2026-09-30T15:00:00Z" }""", true)]
    [InlineData("""{ "refreshCardsAfter": null }""", false)]
    [InlineData("""{ }""", false)]
    [InlineData("""{ "refreshCardsAfter": "next tuesday" }""", false)]
    [InlineData("""[1, 2]""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Flag_parses_a_date_and_nothing_else(string? json, bool hasDate)
    {
        var parsed = CardDataFlag.Parse(json);

        Assert.Equal(hasDate, parsed is not null);
        if (hasDate) Assert.Equal(Flag, parsed);
    }

    [Fact]
    public void IsDue_after_six_hours_only()
    {
        Assert.True(CardRefreshSchedule.IsDue(null, Now));
        Assert.False(CardRefreshSchedule.IsDue(Now.AddHours(-6).AddMinutes(1), Now));
        Assert.True(CardRefreshSchedule.IsDue(Now.AddHours(-6), Now));
    }

    [Fact]
    public void No_flag_never_imports()
    {
        Assert.False(CardRefreshSchedule.ShouldImport(null, importedSource: null, scryfallFileAt: Now, lastAutoAttempt: null, Now));
    }

    [Fact]
    public void A_future_flag_waits()
    {
        var early = Flag.AddHours(-1);

        Assert.False(CardRefreshSchedule.IsPending(Flag, importedSource: null, early));
        Assert.False(CardRefreshSchedule.ShouldImport(Flag, importedSource: null, scryfallFileAt: early, lastAutoAttempt: null, early));
    }

    [Fact]
    public void Data_from_a_file_after_the_flag_is_not_imported_again()
    {
        // A fresh install, or a manual Update cards, after Scryfall had the set.
        var source = Flag.AddHours(2);

        Assert.False(CardRefreshSchedule.IsPending(Flag, source, Now));
        Assert.False(CardRefreshSchedule.ShouldImport(Flag, source, scryfallFileAt: Now, lastAutoAttempt: null, Now));
    }

    [Fact]
    public void A_Scryfall_file_older_than_the_flag_is_not_imported()
    {
        // The flag is set, but Scryfall hasn't regenerated its file since: it wouldn't have the set.
        var olderFile = Flag.AddHours(-3);

        Assert.True(CardRefreshSchedule.IsPending(Flag, Flag.AddDays(-5), Now));
        Assert.False(CardRefreshSchedule.ShouldImport(Flag, Flag.AddDays(-5), olderFile, lastAutoAttempt: null, Now));
        Assert.False(CardRefreshSchedule.ShouldImport(Flag, Flag.AddDays(-5), scryfallFileAt: null, lastAutoAttempt: null, Now));
    }

    [Theory]
    [InlineData(true)]  // a database imported before #89: its source is unknown
    [InlineData(false)] // cards from a file a few days older than the flag
    public void Old_or_unknown_data_imports_once_Scryfall_has_a_newer_file(bool unknownSource)
    {
        DateTimeOffset? source = unknownSource ? null : Flag.AddDays(-3);

        Assert.True(CardRefreshSchedule.ShouldImport(Flag, source, scryfallFileAt: Flag.AddHours(1), lastAutoAttempt: null, Now));
    }

    [Fact]
    public void A_recent_automatic_attempt_blocks_another_for_six_hours()
    {
        // The last try failed (the source is still old): one try per window, never a loop.
        var source = Flag.AddDays(-3);
        var file = Flag.AddHours(1);

        Assert.False(CardRefreshSchedule.ShouldImport(Flag, source, file, lastAutoAttempt: Now.AddHours(-2), Now));
        Assert.True(CardRefreshSchedule.ShouldImport(Flag, source, file, lastAutoAttempt: Now.AddHours(-6), Now));
    }

    [Fact]
    public void After_an_import_the_same_flag_never_imports_again()
    {
        // The import recorded the file it used - from after the flag - so however much later,
        // and however many new Scryfall files appear, this flag is done.
        var importedFrom = Flag.AddHours(1);

        foreach (var later in new[] { Now, Now.AddDays(1), Now.AddDays(30) })
        {
            Assert.False(CardRefreshSchedule.ShouldImport(Flag, importedFrom, scryfallFileAt: later, lastAutoAttempt: Now, later));
        }
    }
}
