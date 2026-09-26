using System.Net;
using MtgaCollectionAdvisor.Core.Creators;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

public sealed class CreatorRosterTests
{
    private const string GoodId = "UCKivtYJCyZn-uaTr5uAozAg";
    private const string OtherId = "UCn7WPnkOG5946nO7DphBPvg";

    [Fact]
    public void Parse_Should_ReadEveryField()
    {
        var channels = CreatorRoster.Parse($$"""
            { "channels": [
              { "name": "A", "channelId": "{{GoodId}}" },
              { "name": "B", "channelId": "{{OtherId}}", "language": "PT", "postsOtherContent": true, "unknown": 1 }
            ] }
            """);

        Assert.Equal(
            [new CreatorChannel("A", GoodId), new CreatorChannel("B", OtherId, PostsOtherContent: true, Language: "pt")],
            channels);
    }

    [Theory]
    [InlineData("UCKivtYJCyZn-uaTr5uAozA")]          // one character short
    [InlineData("UCKivtYJCyZn-uaTr5uAozAgX")]        // one too many
    [InlineData("XXKivtYJCyZn-uaTr5uAozAg")]         // not a channel id
    [InlineData("UCKivtYJCyZn-uaTr5u/ozAg")]         // a character an id never has
    [InlineData("")]
    public void Parse_Should_DropAnEntryWithABadId_AndKeepTheRest(string badId)
    {
        var channels = CreatorRoster.Parse($$"""
            { "channels": [ { "name": "Bad", "channelId": "{{badId}}" }, { "name": "Good", "channelId": "{{GoodId}}" } ] }
            """);

        Assert.Equal(["Good"], channels!.Select(c => c.Name));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("\"   \"")]
    public void Parse_Should_DropAnEntryWithNoName(string name)
    {
        var channels = CreatorRoster.Parse($$"""
            { "channels": [ { "name": {{name}}, "channelId": "{{OtherId}}" }, { "name": "Good", "channelId": "{{GoodId}}" } ] }
            """);

        Assert.Equal(["Good"], channels!.Select(c => c.Name));
    }

    [Fact]
    public void Parse_Should_DropAnEntryWithATooLongName()
    {
        var channels = CreatorRoster.Parse($$"""
            { "channels": [ { "name": "{{new string('x', CreatorRoster.MaxNameLength + 1)}}", "channelId": "{{OtherId}}" },
                            { "name": "Good", "channelId": "{{GoodId}}" } ] }
            """);

        Assert.Equal(["Good"], channels!.Select(c => c.Name));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("zz")]
    [InlineData("p1")]
    public void Parse_Should_DropAnEntryWithAnUnknownLanguage(string language)
    {
        var channels = CreatorRoster.Parse($$"""
            { "channels": [ { "name": "Bad", "channelId": "{{OtherId}}", "language": "{{language}}" },
                            { "name": "Good", "channelId": "{{GoodId}}" } ] }
            """);

        Assert.Equal(["Good"], channels!.Select(c => c.Name));
    }

    [Fact]
    public void Parse_Should_KeepTheFirst_When_ANameOrIdRepeats()
    {
        var channels = CreatorRoster.Parse($$"""
            { "channels": [
              { "name": "A", "channelId": "{{GoodId}}" },
              { "name": "a", "channelId": "{{OtherId}}" },
              { "name": "C", "channelId": "{{GoodId}}" }
            ] }
            """);

        Assert.Equal(["A"], channels!.Select(c => c.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"channels\": [ { \"name\": \"A\" ")]
    [InlineData("{ \"channels\": null }")]
    [InlineData("{ \"channels\": [] }")]
    [InlineData("{ \"channels\": [ null ] }")]
    [InlineData("[]")]
    [InlineData("<html>404</html>")]
    public void Parse_Should_ReturnNothing_When_TheFileGivesNoChannel(string json)
    {
        Assert.Null(CreatorRoster.Parse(json));
    }

    [Fact]
    public void Choose_Should_PreferFetchedThenStoredThenCompiled()
    {
        IReadOnlyList<CreatorChannel> fetched = [new("F", GoodId)];
        IReadOnlyList<CreatorChannel> stored = [new("S", GoodId)];

        Assert.Same(fetched, CreatorRoster.Choose(fetched, stored));
        Assert.Same(stored, CreatorRoster.Choose(null, stored));
        Assert.Same(CreatorChannels.All, CreatorRoster.Choose(null, null));
    }

    [Fact]
    public void IsDue_Should_WaitADayAfterTheLastAttempt()
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        Assert.True(CreatorRoster.IsDue(null, now));
        Assert.False(CreatorRoster.IsDue(now.AddHours(-23), now));
        Assert.True(CreatorRoster.IsDue(now.AddHours(-24), now));
        // A clock moved back must not stop the checks for good.
        Assert.True(CreatorRoster.IsDue(now.AddDays(3), now));
    }

    [Fact]
    public void TheRepositorysFile_Should_BeValidInEveryEntry()
    {
        // The file players read. An entry the app would drop fails here, in CI, before anyone
        // loses a creator.
        var json = File.ReadAllText(Path.Combine(RepositoryRoot(), "creators.json"));
        var channels = CreatorRoster.Parse(json);

        Assert.NotNull(channels);
        Assert.Equal(System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("channels").GetArrayLength(), channels.Count);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MtgaCollectionAdvisor.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}

/// <summary>The daily read and its fallbacks, against a throwaway database and a canned response.</summary>
public sealed class CreatorRosterServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"advisor-roster-{Guid.NewGuid():N}.db");
    private CreatorRosterStore _store = null!;

    private const string Valid = """{ "channels": [ { "name": "Remote", "channelId": "UCKivtYJCyZn-uaTr5uAozAg" } ] }""";

    public async Task InitializeAsync()
    {
        var database = new Database(_databasePath);
        await SchemaMigrator.MigrateAsync(database);
        _store = new CreatorRosterStore(database);
    }

    public Task DisposeAsync()
    {
        TestDatabaseFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoadAsync_Should_UseAndStoreAValidFile()
    {
        var (service, handler) = Service(HttpStatusCode.OK, Valid);

        var load = await service.LoadAsync();
        Assert.Equal(["Remote"], load.Channels.Select(c => c.Name));
        Assert.Null(load.ReadFailure);
        Assert.Equal(Valid, (await _store.LoadAsync()).Json);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task LoadAsync_Should_UseTheCompiledList_When_NothingWasEverFetched()
    {
        var (service, _) = Service(HttpStatusCode.NotFound, "404: Not Found");

        var load = await service.LoadAsync();
        Assert.Same(CreatorChannels.All, load.Channels);
        Assert.Equal("HTTP 404 NotFound", load.ReadFailure);
        Assert.NotNull((await _store.LoadAsync()).LastAttemptAt);
    }

    [Fact]
    public async Task LoadAsync_Should_KeepTheStoredCopy_When_TheNextFileIsInvalid()
    {
        await _store.RecordAttemptAsync(DateTimeOffset.UtcNow.AddDays(-2), Valid);
        var (service, _) = Service(HttpStatusCode.OK, """{ "channels": [ { "name": "X", "channelId": "nope" } ] }""");

        var load = await service.LoadAsync();
        Assert.Equal(["Remote"], load.Channels.Select(c => c.Name));
        Assert.Equal("no valid channel in the file", load.ReadFailure);
        Assert.Equal(Valid, (await _store.LoadAsync()).Json);
    }

    [Fact]
    public async Task LoadAsync_Should_NotAskAgain_WithinADay()
    {
        await _store.RecordAttemptAsync(DateTimeOffset.UtcNow.AddHours(-1), Valid);
        var (service, handler) = Service(HttpStatusCode.OK, Valid);

        var load = await service.LoadAsync();
        Assert.Equal(["Remote"], load.Channels.Select(c => c.Name));
        Assert.Null(load.ReadFailure);   // nothing was asked, so nothing failed
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task LoadAsync_Should_NotAskAgain_WithinADay_AfterAFailure()
    {
        var (service, handler) = Service(HttpStatusCode.InternalServerError, "");

        await service.LoadAsync();
        await service.LoadAsync();

        Assert.Equal(1, handler.Requests);
    }

    private (CreatorRosterService, CannedHandler) Service(HttpStatusCode status, string body)
    {
        var handler = new CannedHandler(status, body);
        return (new CreatorRosterService(new HttpClient(handler), _store, "https://example.test/creators.json"), handler);
    }

    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
