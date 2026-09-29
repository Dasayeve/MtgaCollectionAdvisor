using MtgaCollectionAdvisor.Core.Hosting;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

public sealed class LogFilesTests
{
    [Fact]
    public void FolderFor_Should_BeLogsNextToTheDatabase()
    {
        // A folder that is absolute on the running OS: "C:\x" is only a relative file name on macOS.
        var folder = Path.Combine(Path.GetTempPath(), "x");

        Assert.Equal(Path.Combine(folder, "logs"), LogFiles.FolderFor(Path.Combine(folder, "advisor.db")));
    }

    [Fact]
    public void FileName_Should_UseTheDay()
    {
        Assert.Equal("advisor-2026-09-25.log", LogFiles.FileName(new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public void ExpiredFiles_Should_ReturnOnlyOldLogFiles()
    {
        var today = new DateOnly(2026, 9, 25);
        string[] files = ["advisor-2026-09-25.log", "advisor-2026-09-18.log", "advisor-2026-09-17.log", "advisor-2000-01-01.log"];

        Assert.Equal(["advisor-2026-09-17.log", "advisor-2000-01-01.log"], LogFiles.ExpiredFiles(files, today));
    }

    [Fact]
    public void ExpiredFiles_Should_IgnoreOtherFiles()
    {
        string[] files = ["notes.txt", "advisor-bad.log", "advisor.db", "advisor-2000-13-45.log", "other-2000-01-01.log"];

        Assert.Empty(LogFiles.ExpiredFiles(files, new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public void FormatEntry_Should_WriteTimeLevelCategoryMessage()
    {
        var at = new DateTimeOffset(2026, 9, 25, 18, 4, 12, 345, TimeSpan.Zero);

        Assert.Equal("2026-09-25 18:04:12.345 WARN  Some.Category: it broke" + Environment.NewLine,
            LogFiles.FormatEntry(at, "WARN ", "Some.Category", "it broke", null));
    }

    [Fact]
    public void FormatEntry_Should_AppendTheException()
    {
        var entry = LogFiles.FormatEntry(DateTimeOffset.UnixEpoch, "ERROR", "C", "failed",
            new InvalidOperationException("the reason"));

        var lines = entry.Split(Environment.NewLine);
        Assert.EndsWith("ERROR C: failed", lines[0]);
        Assert.Contains("System.InvalidOperationException: the reason", lines[1]);
    }

    [Theory]
    [InlineData(2, "INFO ")]
    [InlineData(3, "WARN ")]
    [InlineData(4, "ERROR")]
    [InlineData(5, "CRIT ")]
    public void LevelText_Should_MapEachLevel(int level, string expected)
    {
        Assert.Equal(expected, LogFiles.LevelText(level));
    }
}
