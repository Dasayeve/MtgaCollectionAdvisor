using System.Net;
using System.Text.Json;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Hosting;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>The few words the log keeps about a handled network failure (#74).</summary>
public sealed class FailureTextTests
{
    [Fact]
    public void Describe_Should_GiveTheHttpStatus()
    {
        Assert.Equal("HTTP 404 NotFound",
            FailureText.Describe(new HttpRequestException("x", null, HttpStatusCode.NotFound)));
    }

    [Fact]
    public void Describe_Should_LookInsideTheAppsOwnExceptions()
    {
        // Archidekt's client wraps the HTTP failure: a 429 has to read as a 429, not as its wrapper.
        var wrapped = new ArchidektUnavailableException("Could not read deck 7 from Archidekt.",
            new HttpRequestException("x", null, HttpStatusCode.TooManyRequests));

        Assert.Equal("HTTP 429 TooManyRequests", FailureText.Describe(wrapped));
    }

    [Fact]
    public void Describe_Should_NameATimeout()
    {
        Assert.Equal("timed out", FailureText.Describe(new TaskCanceledException()));
    }

    [Fact]
    public void Describe_Should_NameAConnectionFailure()
    {
        var ex = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.");

        Assert.Equal("no connection (NameResolutionError)", FailureText.Describe(ex));
    }

    [Fact]
    public void Describe_Should_NameAnUnreadableAnswer()
    {
        Assert.Equal("unreadable answer (not the JSON expected)", FailureText.Describe(new JsonException("bad")));
    }

    [Fact]
    public void Describe_Should_FallBackToTypeAndMessage()
    {
        Assert.Equal("InvalidOperationException: odd", FailureText.Describe(new InvalidOperationException("odd")));
    }
}
