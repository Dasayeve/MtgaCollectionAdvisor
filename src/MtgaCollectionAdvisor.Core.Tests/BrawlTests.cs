using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Arena;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>The 100-card Brawl format (#76): its legality column, its commander, and Arena's names for it.</summary>
public sealed class BrawlTests
{
    [Fact]
    public void Brawl_Should_BeA100CardFormat()
    {
        Assert.Contains(Formats.Brawl, Formats.All);
        Assert.Equal("brawl", Formats.Brawl.ScryfallLegalityKey);
        Assert.Equal(100, Formats.Brawl.MinimumDeckSize);
        Assert.All([Formats.Standard, Formats.Pioneer], f => Assert.Equal(60, f.MinimumDeckSize));
    }

    [Fact]
    public void IsLegalIn_Should_ReadEachFormatsOwnColumn()
    {
        // Legal in Brawl only: before #76 every format that wasn't Standard read Pioneer's column.
        var card = new CardInfo(1, "Ajani, Nacatl Pariah", "MH3", "{1}{W}", "W", CardRarity.Mythic,
            StandardLegal: false, PioneerLegal: false, BrawlLegal: true);

        Assert.False(card.IsLegalIn(Formats.Standard));
        Assert.False(card.IsLegalIn(Formats.Pioneer));
        Assert.True(card.IsLegalIn(Formats.Brawl));
    }

    [Fact]
    public void IsLegalIn_Should_Throw_ForAFormatWithNoColumn()
    {
        var card = new CardInfo(1, "X", "S", "", "", CardRarity.Common, true, true);

        Assert.Throws<ArgumentOutOfRangeException>(() => card.IsLegalIn(new FormatDefinition("modern", "Modern", "modern", "modern")));
    }

    [Theory]
    [InlineData("HistoricBrawl", "brawl")]
    [InlineData("Brawl", null)]   // Arena's "Brawl" is Standard Brawl (60 cards), not ranked
    public void FormatFor_Should_MapArenasBrawlNames(string arenaFormat, string? expectedKey)
    {
        Assert.Equal(expectedKey, ArenaDeckImport.FormatFor(arenaFormat)?.Key);
    }

    [Fact]
    public void ToCandidateDeck_Should_KeepTheCommanderOnItsOwnBoard()
    {
        var names = new Dictionary<int, string> { [1] = "Tinybones, Bauble Burglar", [2] = "Swamp" };
        var deck = new ArenaDeck("d", "Tinybones", "HistoricBrawl", false,
            Commander: [new(1, 1)], Companion: [], Main: [new(2, 99)], Sideboard: []);

        var candidate = ArenaDeckImport.ToCandidateDeck(deck, names, DateTimeOffset.UnixEpoch)!;

        Assert.Equal("brawl", candidate.FormatKey);
        Assert.Equal(
            [new DeckCardRef("Tinybones, Bauble Burglar", 1, DeckBoard.Commander), new DeckCardRef("Swamp", 99, DeckBoard.Main)],
            candidate.Cards);
        Assert.Equal(100, ArenaDeckImport.Choices([deck], names, new HashSet<string>()).Single().CardCount);
    }

    [Fact]
    public void Parse_Should_ReadTheCommanderSection()
    {
        var cards = ArenaDeckListParser.Parse("Commander\n1 Tinybones, Bauble Burglar (SNC) 94\n\nDeck\n1 Duress (M21) 96\n98 Swamp\n");

        Assert.Equal(
            [
                new DeckCardRef("Tinybones, Bauble Burglar", 1, DeckBoard.Commander),
                new DeckCardRef("Duress", 1, DeckBoard.Main),
                new DeckCardRef("Swamp", 98, DeckBoard.Main),
            ],
            cards);
    }

    [Fact]
    public void Write_Should_PutTheCommanderInItsOwnSectionFirst()
    {
        var deck = Deck(("Duress", 1, DeckBoard.Main), ("Tinybones, Bauble Burglar", 1, DeckBoard.Commander));

        var text = ArenaDeckListWriter.Write(deck);

        Assert.Equal($"Commander{N}1 Tinybones, Bauble Burglar{N}{N}Deck{N}1 Duress{N}", text);
        Assert.Equal(deck.Cards.OrderBy(c => c.Board == DeckBoard.Commander ? 0 : 1), ArenaDeckListParser.Parse(text));
    }

    [Fact]
    public void Write_Should_HaveNoCommanderSection_ForADeckWithout()
    {
        Assert.StartsWith("Deck", ArenaDeckListWriter.Write(Deck(("Duress", 4, DeckBoard.Main))));
    }

    [Fact]
    public void Deduplicate_Should_KeepDecksThatDifferOnlyInTheirCommander()
    {
        var a = Deck(("Swamp", 99, DeckBoard.Main), ("Tinybones, Bauble Burglar", 1, DeckBoard.Commander)) with { SourceId = "a" };
        var b = Deck(("Swamp", 99, DeckBoard.Main), ("Sheoldred, the Apocalypse", 1, DeckBoard.Commander)) with { SourceId = "b" };

        Assert.Equal(2, DeckDeduplicator.Deduplicate([a, b]).Count);
    }

    [Fact]
    public void LigaMagicList_Should_ListTheCommanderFirst()
    {
        var gaps = new[]
        {
            new CardGap("Duress", DeckBoard.Main, 1, 0, 1, CardRarity.Common),
            new CardGap("Tinybones, Bauble Burglar", DeckBoard.Commander, 1, 0, 2, CardRarity.Rare),
        };
        var analysis = new DeckAnalysisResult(
            Deck(("Duress", 1, DeckBoard.Main), ("Tinybones, Bauble Burglar", 1, DeckBoard.Commander)),
            WildcardNeed.Zero, 0, 2, gaps, [], [], "B");

        Assert.Equal($"1 Tinybones, Bauble Burglar{N}1 Duress{N}", LigaMagicList.Write(analysis));
    }

    private static readonly string N = Environment.NewLine;

    private static CandidateDeck Deck(params (string Name, int Qty, DeckBoard Board)[] cards) =>
        new("manual:t", "T", "", "brawl", 0, [.. cards.Select(c => new DeckCardRef(c.Name, c.Qty, c.Board))], DateTimeOffset.UnixEpoch);
}

/// <summary>Brawl legality through the card store and the wildcard calculation (#76).</summary>
public sealed class BrawlLegalityStorageTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"advisor-brawl-{Guid.NewGuid():N}.db");
    private CardDatabaseStore _store = null!;

    public async Task InitializeAsync()
    {
        var database = new Database(_databasePath);
        await SchemaMigrator.MigrateAsync(database);
        _store = new CardDatabaseStore(database);
        await _store.ReplaceAllAsync(Cards());
    }

    public Task DisposeAsync()
    {
        TestDatabaseFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CardStore_Should_RoundTripBrawlLegality()
    {
        Assert.True(Assert.Single(await _store.FindByNameAsync("Ajani, Nacatl Pariah")).BrawlLegal);
        Assert.False(Assert.Single(await _store.FindByNameAsync("Banned Thing")).BrawlLegal);
    }

    [Fact]
    public async Task Analyze_Should_UseBrawlLegality_ForABrawlDeck()
    {
        var deck = new CandidateDeck("manual:b", "B", "", "brawl", 0,
            [new("Ajani, Nacatl Pariah", 1, DeckBoard.Commander), new("Banned Thing", 1, DeckBoard.Main)], DateTimeOffset.UnixEpoch);
        var calculator = new WildcardCalculator(_store);

        var asBrawl = await calculator.AnalyzeAsync(deck, CollectionSnapshot.Empty, Formats.Brawl, []);
        var asPioneer = await calculator.AnalyzeAsync(deck, CollectionSnapshot.Empty, Formats.Pioneer, []);

        Assert.Equal(["Banned Thing"], asBrawl.IllegalInFormat);
        Assert.Equal(["Ajani, Nacatl Pariah"], asPioneer.IllegalInFormat);
        // The commander counts towards cost and colours like any card of the deck.
        Assert.Equal(new WildcardNeed(1, 0, 0, 1), asBrawl.Needed);
        Assert.Equal("WR", asBrawl.Colors);
    }

    private static async IAsyncEnumerable<CardInfo> Cards()
    {
        yield return new CardInfo(1, "Ajani, Nacatl Pariah", "MH3", "{1}{W}", "W", CardRarity.Mythic,
            StandardLegal: false, PioneerLegal: false, BrawlLegal: true);
        yield return new CardInfo(2, "Banned Thing", "TST", "{R}", "R", CardRarity.Common,
            StandardLegal: true, PioneerLegal: true, BrawlLegal: false);
        await Task.CompletedTask;
    }
}
