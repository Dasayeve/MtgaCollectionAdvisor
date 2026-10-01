using System.IO.Compression;
using System.Net;
using System.Text;
using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #84 over a throwaway database: the set name and date stored per printing, read from Scryfall's
/// file, and the printings a deck's suggestion considers.
/// </summary>
public sealed class PackSuggestionServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"advisor-packs-{Guid.NewGuid():N}.db");
    private CardDatabaseStore _cards = null!;

    public async Task InitializeAsync()
    {
        var database = new Database(_databasePath);
        await SchemaMigrator.MigrateAsync(database);
        _cards = new CardDatabaseStore(database);
    }

    public Task DisposeAsync()
    {
        TestDatabaseFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task The_set_name_and_date_survive_the_store()
    {
        await _cards.ReplaceAllAsync(new[]
        {
            new CardInfo(1, "Emrakul, the Exigent Doom", "fra", "{10}", "", CardRarity.Mythic, true, true,
                SetName: "Reality Fracture", SetReleasedAt: new DateOnly(2026, 10, 2)),
        }.ToAsyncEnumerable());

        var card = Assert.Single(await _cards.FindByNameAsync("Emrakul, the Exigent Doom"));

        Assert.Equal("Reality Fracture", card.SetName);
        Assert.Equal(new DateOnly(2026, 10, 2), card.SetReleasedAt);
        Assert.Equal(1, (await _cards.CountCardDataAsync()).WithSetName);
    }

    [Fact]
    public async Task Service_uses_the_printings_the_deck_can_use()
    {
        // Legal in Standard only from Reality Fracture; the Foundations printing is not.
        await _cards.ReplaceAllAsync(new[]
        {
            new CardInfo(1, "Shifting", "fra", "{1}", "R", CardRarity.Rare, StandardLegal: true, PioneerLegal: true,
                SetName: "Reality Fracture", SetReleasedAt: new DateOnly(2026, 10, 2)),
            new CardInfo(2, "Shifting", "fdn", "{1}", "R", CardRarity.Uncommon, StandardLegal: false, PioneerLegal: true,
                SetName: "Foundations", SetReleasedAt: new DateOnly(2024, 11, 15)),
            new CardInfo(3, "Plains", "fra", "", "", CardRarity.Basic, true, true, SetName: "Reality Fracture"),
        }.ToAsyncEnumerable());

        var deck = new DeckAnalysisResult(
            new CandidateDeck("manual:test", "Test", "", Formats.Standard.Key, 0, [], DateTimeOffset.UnixEpoch),
            WildcardNeed.Zero, 0, 6,
            [
                new CardGap("Shifting", DeckBoard.Main, 4, 1, 1, CardRarity.Rare),
                new CardGap("Shifting", DeckBoard.Sideboard, 2, 1, 1, CardRarity.Rare),
                new CardGap("Plains", DeckBoard.Main, 8, 0, 3, CardRarity.Basic),
                new CardGap("Not On Arena", DeckBoard.Main, 2, 0, null, CardRarity.Unknown),
            ],
            ["Not On Arena"], [], "W");

        var suggestion = await new PackSuggestionService(_cards).SuggestAsync(deck, Formats.Standard);

        var pack = Assert.Single(suggestion.Packs);
        Assert.Equal("FRA", pack.SetCode);
        var card = Assert.Single(pack.Cards);
        Assert.Equal(4, card.Missing); // 3 in the main deck and 1 in the sideboard
        Assert.Empty(card.AlsoIn);     // the Foundations printing isn't legal here
        Assert.Empty(suggestion.WildcardsOnly);
    }

    [Fact]
    public async Task Import_reads_the_set_name_and_date()
    {
        var line = """{"arena_id":5,"name":"Emrakul, the Exigent Doom","set":"fra","set_name":"Reality Fracture","released_at":"2026-10-02","rarity":"mythic","legalities":{"standard":"legal"}}""";
        var importer = new ScryfallBulkImporter(new HttpClient(new GzipFile(line)));

        var import = await importer.ImportWithArenaPrintsAsync(new ScryfallBulkFile("https://data.scryfall.io/x.jsonl.gz", null));

        var card = Assert.Single(import.WithId);
        Assert.Equal("Reality Fracture", card.SetName);
        Assert.Equal(new DateOnly(2026, 10, 2), card.SetReleasedAt);
    }

    /// <summary>Scryfall's bulk file: gzip-compressed JSON lines.</summary>
    private sealed class GzipFile(string lines) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(lines + "\n"));
            }
            buffer.Position = 0;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(buffer) });
        }
    }
}
