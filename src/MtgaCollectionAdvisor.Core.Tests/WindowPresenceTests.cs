using MtgaCollectionAdvisor.Core.Hosting;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// When the app stops for lack of a window. Stopping too eagerly kills it under a window the
/// browser froze (#99) or on a reload; never stopping leaves it watching MTG Arena with nobody
/// looking (#34).
/// </summary>
public class WindowPresenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Silence = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Sleep = TimeSpan.FromHours(12);

    private const string WindowA = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string WindowB = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private static WindowPresence NewPresence() => new(Grace, Silence, Sleep);

    [Fact]
    public void ShouldStop_Should_BeNull_BeforeAnyConnection()
    {
        var presence = NewPresence();
        presence.Reported(WindowA, WindowState.Closed, T0);

        Assert.Null(presence.ShouldStop(T0.AddDays(1)));
    }

    [Fact]
    public void ShouldStop_Should_BeNull_WhileConnected_WhateverTheBeaconsSaid()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Closed, T0);

        Assert.Null(presence.ShouldStop(T0.AddHours(1)));
    }

    [Fact]
    public void ShouldStop_Should_NotStop_When_AHiddenWindowLosesItsConnection()
    {
        // #99: behind MTG Arena the window is hidden, the browser freezes it, the connection drops.
        var presence = NewPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Visible, T0);
        presence.Reported(WindowA, WindowState.Hidden, T0.AddMinutes(1));
        presence.Disconnected(T0.AddMinutes(6));

        Assert.Null(presence.ShouldStop(T0.AddMinutes(6).Add(Sleep).AddMinutes(-1)));
    }

    [Fact]
    public void ShouldStop_Should_BeAsleepTooLong_AfterTheSleepLimit()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Hidden, T0);
        presence.Disconnected(T0);

        Assert.Equal(StopReason.AsleepTooLong, presence.ShouldStop(T0.Add(Sleep)));
    }

    [Fact]
    public void ShouldStop_Should_BeWindowClosed_GraceAfterTheLastClose()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Hidden, T0);
        presence.Reported(WindowA, WindowState.Closed, T0);
        presence.Disconnected(T0);

        Assert.Null(presence.ShouldStop(T0.AddSeconds(44)));
        Assert.Equal(StopReason.WindowClosed, presence.ShouldStop(T0.AddSeconds(45)));
    }

    [Fact]
    public void ShouldStop_Should_CountTheGraceFromTheLaterOfCloseAndDisconnect()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Closed, T0);
        presence.Disconnected(T0.AddSeconds(20));

        Assert.Null(presence.ShouldStop(T0.AddSeconds(64)));
        Assert.Equal(StopReason.WindowClosed, presence.ShouldStop(T0.AddSeconds(65)));

        var closedLater = NewPresence();
        closedLater.Connected();
        closedLater.Disconnected(T0);
        closedLater.Reported(WindowA, WindowState.Closed, T0.AddSeconds(20));

        Assert.Null(closedLater.ShouldStop(T0.AddSeconds(64)));
        Assert.Equal(StopReason.WindowClosed, closedLater.ShouldStop(T0.AddSeconds(65)));
    }

    [Fact]
    public void ShouldStop_Should_BeNull_When_AReloadConnectsAfterClose()
    {
        // A reload: the old page says closed and drops, the new page reports and connects.
        var presence = NewPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Closed, T0);
        presence.Disconnected(T0);
        presence.Reported(WindowB, WindowState.Visible, T0.AddSeconds(1));
        presence.Connected();

        Assert.Null(presence.ShouldStop(T0.AddHours(1)));
    }

    [Fact]
    public void ShouldStop_Should_WaitForTheSleepingWindow_When_AnotherWindowClosed()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Hidden, T0);
        presence.Reported(WindowB, WindowState.Closed, T0);
        presence.Disconnected(T0);
        presence.Disconnected(T0);

        Assert.Null(presence.ShouldStop(T0.AddHours(1)));
    }

    [Fact]
    public void ShouldStop_Should_BeNoWindow_AfterSilence_When_LastSeenVisible()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Visible, T0);
        presence.Disconnected(T0);

        Assert.Null(presence.ShouldStop(T0.AddMinutes(29)));
        Assert.Equal(StopReason.NoWindow, presence.ShouldStop(T0.AddMinutes(30)));
    }

    [Fact]
    public void ShouldStop_Should_BeNoWindow_AfterSilence_When_NoBeaconEverArrived()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Disconnected(T0);

        Assert.Null(presence.ShouldStop(T0.AddMinutes(29)));
        Assert.Equal(StopReason.NoWindow, presence.ShouldStop(T0.AddMinutes(30)));
    }

    [Fact]
    public void ShouldStop_Should_WaitForAllConnections()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Connected();
        presence.Disconnected(T0);

        Assert.Null(presence.ShouldStop(T0.AddDays(1)));
    }

    [Fact]
    public void Disconnected_Should_NotGoBelowZero()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Disconnected(T0);
        presence.Disconnected(T0);

        // Had the count gone to -1, this window would leave it at 0 and read as "no window".
        presence.Connected();

        Assert.Null(presence.ShouldStop(T0.AddDays(1)));
    }

    [Fact]
    public void Reported_Should_KeepAtMostMaxTrackedWindows()
    {
        var presence = NewPresence();
        presence.Connected();

        // The one hidden window is the least recently reported, so it is the one dropped.
        presence.Reported("sleeper", WindowState.Hidden, T0);
        for (var i = 0; i < WindowPresence.MaxTrackedWindows; i++)
        {
            presence.Reported($"closed-{i}", WindowState.Closed, T0.AddSeconds(1 + i));
        }
        presence.Disconnected(T0.AddMinutes(1));

        Assert.Equal(StopReason.WindowClosed, presence.ShouldStop(T0.AddMinutes(2)));
    }

    [Fact]
    public void ShouldStop_Should_PruneWindowsNotHeardFromForTheSleepLimit()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Closed, T0);
        presence.Reported(WindowB, WindowState.Visible, T0.Add(Sleep).AddMinutes(1));
        presence.Reported(WindowB, WindowState.Closed, T0.Add(Sleep).AddMinutes(2));
        presence.Disconnected(T0.Add(Sleep).AddMinutes(2));

        // WindowA is pruned; WindowB alone decides, and it closed.
        Assert.Equal(StopReason.WindowClosed, presence.ShouldStop(T0.Add(Sleep).AddMinutes(3)));

        var old = NewPresence();
        old.Connected();
        old.Reported(WindowA, WindowState.Closed, T0);
        old.Disconnected(T0.Add(Sleep).AddMinutes(1));

        // Its only window is pruned, so nothing is known: the silence limit applies, not the grace.
        Assert.Null(old.ShouldStop(T0.Add(Sleep).AddMinutes(2)));
    }

    [Theory]
    [InlineData(WindowA, true)]
    [InlineData("abc-123", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("é", false)]
    [InlineData(null, false)]
    public void IsValidWindowId_Should_AcceptAUuid_And_RejectOthers(string? id, bool valid)
    {
        Assert.Equal(valid, WindowPresence.IsValidWindowId(id));
    }

    [Fact]
    public void IsValidWindowId_Should_RejectMoreThan64Characters()
    {
        Assert.True(WindowPresence.IsValidWindowId(new string('a', 64)));
        Assert.False(WindowPresence.IsValidWindowId(new string('a', 65)));
    }

    [Fact]
    public void Reported_Should_IgnoreAnInvalidId()
    {
        var presence = NewPresence();
        presence.Connected();
        presence.Reported("a/b", WindowState.Closed, T0);
        presence.Disconnected(T0);

        // Nothing was recorded, so the grace doesn't apply: only the silence limit does.
        Assert.Null(presence.ShouldStop(T0.AddMinutes(1)));
    }

    [Fact]
    public void Defaults_Should_Be45Seconds30MinutesAnd12Hours()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), WindowPresence.DefaultCloseGrace);
        Assert.Equal(TimeSpan.FromMinutes(30), WindowPresence.DefaultSilenceLimit);
        Assert.Equal(TimeSpan.FromHours(12), WindowPresence.DefaultSleepLimit);

        var presence = new WindowPresence();
        presence.Connected();
        presence.Reported(WindowA, WindowState.Closed, T0);
        presence.Disconnected(T0);

        Assert.Null(presence.ShouldStop(T0.AddSeconds(44)));
        Assert.Equal(StopReason.WindowClosed, presence.ShouldStop(T0.AddSeconds(45)));
    }

    [Fact]
    public void Constructor_Should_RejectLimitsOutOfOrder()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowPresence(TimeSpan.Zero, Silence, Sleep));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowPresence(Grace, TimeSpan.FromSeconds(10), Sleep));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowPresence(Grace, Silence, TimeSpan.FromMinutes(10)));
    }
}
