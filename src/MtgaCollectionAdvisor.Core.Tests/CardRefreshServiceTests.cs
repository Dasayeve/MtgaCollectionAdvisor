using System.Net;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #89 end to end over a throwaway database and a fake network: what is asked, how often, and
/// when an import is called for. Counting requests is the point - the player's app must never
/// meet a provider's rate limit.
/// </summary>
public sealed class CardRefreshServiceTests : IAsyncLifetime
{
    private const string FlagUrl = "https://example.test/card-data.json";
    private static readonly DateTimeOffset Flag = new(2026, 9, 30, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Flag.AddHours(10);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"advisor-refresh-{Guid.NewGuid():N}.db");
    private CardRefreshStore _store = null!;
    private CardDatabaseStore _cards = null!;

    public async Task InitializeAsync()
    {
        var database = new Database(_databasePath);
        await SchemaMigrator.MigrateAsync(database);
        _store = new CardRefreshStore(database);
        _cards = new CardDatabaseStore(database);
    }

    public Task DisposeAsync()
    {
        TestDatabaseFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Old_data_with_a_flag_and_a_newer_Scryfall_file_imports()
    {
        var (service, network) = Service(FlagJson(Flag), scryfallFileAt: Flag.AddHours(2));
        await ImportCardsFrom(Flag.AddDays(-4));

        var check = await service.CheckAsync(Now);

        Assert.True(check.ShouldImport);
        Assert.Null(check.ReadFailure);
        Assert.Equal((1, 1), (network.FlagReads, network.ScryfallReads));
    }

    [Fact]
    public async Task After_an_import_the_same_flag_never_imports_again()
    {
        var (service, _) = Service(FlagJson(Flag), scryfallFileAt: Flag.AddHours(2));
        await ImportCardsFrom(Flag.AddDays(-4));
        Assert.True((await service.CheckAsync(Now)).ShouldImport);

        await service.RecordAutoImportAsync(Now);
        await ImportCardsFrom(Flag.AddHours(2)); // the import records the file it used

        foreach (var later in new[] { Now.AddHours(7), Now.AddDays(2), Now.AddDays(20) })
        {
            Assert.False((await service.CheckAsync(later)).ShouldImport);
        }
    }

    [Fact]
    public async Task A_failed_import_is_not_retried_within_six_hours()
    {
        var (service, _) = Service(FlagJson(Flag), scryfallFileAt: Flag.AddHours(2));
        await ImportCardsFrom(Flag.AddDays(-4));

        Assert.True((await service.CheckAsync(Now)).ShouldImport);
        await service.RecordAutoImportAsync(Now); // ...and the import fails: the source stays old

        Assert.False((await service.CheckAsync(Now.AddHours(1))).ShouldImport);
        Assert.False((await service.CheckAsync(Now.AddHours(5))).ShouldImport);
        Assert.True((await service.CheckAsync(Now.AddHours(6))).ShouldImport);
    }

    [Fact]
    public async Task The_flag_is_read_at_most_once_every_six_hours_across_restarts()
    {
        var (first, network) = Service(FlagJson(null), scryfallFileAt: Now);
        await first.CheckAsync(Now);

        // A new service over the same database is what a restart of the app looks like.
        var restarted = new CardRefreshService(new HttpClient(network), Scryfall(network), _store, _cards, FlagUrl);
        await restarted.CheckAsync(Now.AddMinutes(1));
        await restarted.CheckAsync(Now.AddHours(5));
        Assert.Equal(1, network.FlagReads);

        await restarted.CheckAsync(Now.AddHours(6));
        Assert.Equal(2, network.FlagReads);
    }

    [Fact]
    public async Task Scryfall_is_not_asked_without_a_pending_flag()
    {
        // No flag; a future flag; and data already from after the flag: none needs Scryfall.
        var (noFlag, network1) = Service(FlagJson(null), scryfallFileAt: Now);
        Assert.False((await noFlag.CheckAsync(Now)).ShouldImport);
        Assert.Equal(0, network1.ScryfallReads);

        await ImportCardsFrom(Flag.AddHours(1));
        var (upToDate, network2) = Service(FlagJson(Flag), scryfallFileAt: Now);
        Assert.False((await upToDate.CheckAsync(Now.AddHours(6))).ShouldImport);
        Assert.Equal(0, network2.ScryfallReads);
    }

    [Fact]
    public async Task Scryfall_is_asked_at_most_once_every_six_hours_while_its_file_is_older()
    {
        var (service, network) = Service(FlagJson(Flag), scryfallFileAt: Flag.AddHours(-3));
        await ImportCardsFrom(Flag.AddDays(-4));

        Assert.False((await service.CheckAsync(Now)).ShouldImport);
        Assert.False((await service.CheckAsync(Now.AddHours(2))).ShouldImport);
        Assert.Equal(1, network.ScryfallReads);

        network.ScryfallFileAt = Flag.AddHours(9); // Scryfall regenerates its file
        Assert.True((await service.CheckAsync(Now.AddHours(6))).ShouldImport);
        Assert.Equal(2, network.ScryfallReads);
    }

    [Fact]
    public async Task A_failed_flag_read_keeps_the_last_good_flag()
    {
        var (service, network) = Service(FlagJson(Flag), scryfallFileAt: Flag.AddHours(2));
        await ImportCardsFrom(Flag.AddDays(-4));
        await service.CheckAsync(Now);
        await service.RecordAutoImportAsync(Now);

        network.FlagStatus = HttpStatusCode.InternalServerError;
        var check = await service.CheckAsync(Now.AddHours(6));

        Assert.NotNull(check.ReadFailure);
        Assert.True(check.ShouldImport); // the stored flag still stands
        Assert.Equal(Flag, CardDataFlag.Parse((await _store.LoadAsync()).FlagJson));
    }

    [Fact]
    public void The_repository_card_data_json_parses()
    {
        // The maintainer edits this file by hand (#89); a typo must fail CI, not every copy of the app.
        var json = File.ReadAllText(Path.Combine(RepositoryRoot(), "card-data.json"));

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("refreshCardsAfter", out var value));
        Assert.True(value.ValueKind == System.Text.Json.JsonValueKind.Null || CardDataFlag.Parse(json) is not null,
            "refreshCardsAfter must be null or an ISO 8601 date, e.g. \"2026-09-30T15:00:00Z\".");
    }

    private async Task ImportCardsFrom(DateTimeOffset scryfallFileAt) =>
        await _cards.ReplaceAllAsync(OneCard(), scryfallFileAt);

    private static async IAsyncEnumerable<CardInfo> OneCard()
    {
        await Task.Yield();
        yield return new CardInfo(1, "Card", "SET", "{R}", "R", CardRarity.Common, true, true);
    }

    private (CardRefreshService, FakeNetwork) Service(string flagJson, DateTimeOffset scryfallFileAt)
    {
        var network = new FakeNetwork(flagJson) { ScryfallFileAt = scryfallFileAt };
        return (new CardRefreshService(new HttpClient(network), Scryfall(network), _store, _cards, FlagUrl), network);
    }

    private static ScryfallBulkImporter Scryfall(FakeNetwork network) => new(new HttpClient(network));

    private static string FlagJson(DateTimeOffset? at) =>
        at is { } date ? $$"""{ "refreshCardsAfter": "{{date:O}}" }""" : """{ "refreshCardsAfter": null }""";

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MtgaCollectionAdvisor.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    /// <summary>GitHub's raw file and Scryfall's listing, each counted.</summary>
    private sealed class FakeNetwork(string flagJson) : HttpMessageHandler
    {
        public int FlagReads { get; private set; }
        public int ScryfallReads { get; private set; }
        public HttpStatusCode FlagStatus { get; set; } = HttpStatusCode.OK;
        public DateTimeOffset ScryfallFileAt { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.scryfall.com")
            {
                ScryfallReads++;
                var listing = $$"""
                    { "data": [ { "type": "default_cards", "jsonl_download_uri": "https://data.scryfall.io/x.jsonl.gz",
                                  "updated_at": "{{ScryfallFileAt:O}}" } ] }
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listing) });
            }

            FlagReads++;
            return Task.FromResult(new HttpResponseMessage(FlagStatus) { Content = new StringContent(flagJson) });
        }
    }
}
