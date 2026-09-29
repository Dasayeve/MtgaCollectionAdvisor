using MtgaCollectionAdvisor.Core.Hosting;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.1.0+3f2a9c1", "0.1.0")]
    [InlineData("0.2.0-beta.1+abc", "0.2.0-beta.1")]
    [InlineData("1.4.2", "1.4.2")]
    public void Display_Should_StripBuildMetadata(string informationalVersion, string expected)
    {
        Assert.Equal(expected, AppVersion.Display(informationalVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+abc")]
    public void Display_Should_ReturnDev_When_VersionIsMissingOrDefault(string? informationalVersion)
    {
        Assert.Equal("dev", AppVersion.Display(informationalVersion));
    }

    [Fact]
    public void ReleaseNotesUrl_Should_PointAtTheVersionsRelease()
    {
        Assert.Equal("https://github.com/Dasayeve/MtgaCollectionAdvisor/releases/tag/v0.5.0", AppVersion.ReleaseNotesUrl("0.5.0"));
    }

    [Fact]
    public void ReleaseNotesUrl_Should_PointAtAllReleases_When_Dev()
    {
        Assert.Equal("https://github.com/Dasayeve/MtgaCollectionAdvisor/releases", AppVersion.ReleaseNotesUrl("dev"));
    }
}
