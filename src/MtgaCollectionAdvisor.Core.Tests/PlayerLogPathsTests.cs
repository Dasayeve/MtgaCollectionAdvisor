using MtgaCollectionAdvisor.Core.Logs;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>Where Player.log is looked for on each OS, whatever OS runs the tests.</summary>
public sealed class PlayerLogPathsTests
{
    private static readonly string LocalAppData = Path.Combine("users", "player", "AppData", "Local");
    private static readonly string Home = Path.Combine("users", "player");

    [Fact]
    public void For_Should_BeUnderLocalLow_OnWindows()
    {
        Assert.Equal(
            Path.Combine(LocalAppData, "..", "LocalLow", "Wizards Of The Coast", "MTGA", "Player.log"),
            PlayerLogPaths.For(isMacOS: false, LocalAppData, Home));
    }

    [Fact]
    public void For_Should_BeUnderLibraryLogs_OnMacOS()
    {
        Assert.Equal(
            Path.Combine(Home, "Library", "Logs", "Wizards Of The Coast", "MTGA", "Player.log"),
            PlayerLogPaths.For(isMacOS: true, LocalAppData, Home));
    }
}
