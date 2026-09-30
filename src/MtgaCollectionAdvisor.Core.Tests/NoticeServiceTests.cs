using System.Net;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Notices;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #96 over a throwaway database and a fake network: how often notices.json is asked for, what
/// survives a failed read, and that dismissals stay.
/// </summary>
public sealed class NoticeServiceTests : IAsyncLifetime
{
    private const string Url = "https://example.test/notices.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private const string OneNotice = """
        { "notices": [ { "id": "a", "title": "Hello", "text": "World", "showFrom": "2026-10-02T00:00:00Z" } ] }
        """;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"advisor-notices-{Guid.NewGuid():N}.db");
    private Database _database = null!;

    public async Task InitializeAsync()
    {
        _database = new Database(_databasePath);
        await SchemaMigrator.MigrateAsync(_database);
    }

    public Task DisposeAsync()
    {
        TestDatabaseFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_read_is_made_at_most_once_per_window_across_restarts()
    {
        var network = new FakeNetwork(OneNotice);

        var first = await Service(network).LoadAsync(Now);
        var restarted = await Service(network).LoadAsync(Now.AddHours(5));

        Assert.Equal(1, network.Reads);
        Assert.Equal(["a"], first.Notices.Select(n => n.Id));
        Assert.Equal(["a"], restarted.Notices.Select(n => n.Id));

        await Service(network).LoadAsync(Now.AddHours(6));
        Assert.Equal(2, network.Reads);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_stored_notices_and_says_why()
    {
        await Service(new FakeNetwork(OneNotice)).LoadAsync(Now);

        var failing = new FakeNetwork(OneNotice) { Status = HttpStatusCode.NotFound };
        var load = await Service(failing).LoadAsync(Now.AddHours(7));

        Assert.Equal(["a"], load.Notices.Select(n => n.Id));
        Assert.Contains("404", load.ReadFailure);

        // No retry before the next window.
        await Service(failing).LoadAsync(Now.AddHours(8));
        Assert.Equal(1, failing.Reads);
    }

    [Fact]
    public async Task A_file_that_parses_to_nothing_keeps_the_stored_copy()
    {
        await Service(new FakeNetwork(OneNotice)).LoadAsync(Now);

        var load = await Service(new FakeNetwork("<html>not json</html>")).LoadAsync(Now.AddHours(7));

        Assert.Equal(["a"], load.Notices.Select(n => n.Id));
        Assert.NotNull(load.ReadFailure);
        Assert.Contains("\"a\"", (await new NoticeStore(_database).LoadFeedAsync()).Json);
    }

    [Fact]
    public async Task An_empty_list_replaces_the_stored_one()
    {
        await Service(new FakeNetwork(OneNotice)).LoadAsync(Now);

        var load = await Service(new FakeNetwork("""{ "notices": [] }""")).LoadAsync(Now.AddHours(7));

        Assert.Empty(load.Notices);
        Assert.Null(load.ReadFailure);
    }

    [Fact]
    public async Task Dismissals_survive_a_new_store_instance()
    {
        await new NoticeStore(_database).DismissAsync("a", Now);
        await new NoticeStore(_database).DismissAsync("a", Now.AddDays(1));

        Assert.Equal(["a"], await new NoticeStore(_database).LoadDismissedAsync());
    }

    [Fact]
    public async Task HasSetAsync_finds_a_set_case_insensitively()
    {
        var cards = new CardDatabaseStore(_database);
        await cards.ReplaceAllAsync(new[] { new CardInfo(1, "Card", "fra", "{R}", "R", CardRarity.Common, true, true) }.ToAsyncEnumerable());

        Assert.True(await cards.HasSetAsync("fra"));
        Assert.True(await cards.HasSetAsync("FRA"));
        Assert.False(await cards.HasSetAsync("dmu"));
    }

    private NoticeService Service(FakeNetwork network) => new(new HttpClient(network), new NoticeStore(_database), Url);

    private sealed class FakeNetwork(string body) : HttpMessageHandler
    {
        public int Reads { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body) });
        }
    }
}
