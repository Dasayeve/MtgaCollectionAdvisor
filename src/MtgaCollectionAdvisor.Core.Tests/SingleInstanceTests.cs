using MtgaCollectionAdvisor.Core.Hosting;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// One app per database (#119): the same file must always give the same name, or a second
/// launch starts a second copy instead of showing the first one's window.
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void NameFor_Should_BeTheSame_ForSlashAndBackslash()
    {
        Assert.Equal(SingleInstance.NameFor(@"C:\Users\p\advisor.db"), SingleInstance.NameFor("C:/Users/p/advisor.db"));
        Assert.Equal(SingleInstance.NameFor(@"C:\Users\p\advisor.db"), SingleInstance.NameFor(@"C:\Users/p\advisor.db"));
    }

    [Fact]
    public void NameFor_Should_IgnoreCase()
    {
        Assert.Equal(SingleInstance.NameFor(@"C:\Users\P\Advisor.DB"), SingleInstance.NameFor(@"c:\users\p\advisor.db"));
    }

    [Fact]
    public void NameFor_Should_Differ_ForAnotherDatabase()
    {
        // A test copy on its own database runs next to the player's.
        Assert.NotEqual(SingleInstance.NameFor(@"C:\Users\p\advisor.db"), SingleInstance.NameFor(@"C:\Temp\test\advisor.db"));
    }

    [Fact]
    public void NameFor_Should_BeAPlainName()
    {
        var name = SingleInstance.NameFor(@"C:\Users\p\advisor.db");

        Assert.Matches("^MtgaDeckAdvisor\\.[0-9A-F]{16}$", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NameFor_Should_Reject_EmptyPath(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => SingleInstance.NameFor(path!));
    }
}
