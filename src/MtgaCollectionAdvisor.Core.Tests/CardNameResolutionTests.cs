using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// Resolving a decklist name to Arena printings. Runs against a throwaway SQLite file
/// because the matching rules live in SQL and nowhere else.
///
/// The case that matters: Arena's export format writes only the front face of a
/// double-faced card, while the card database stores the full "Front // Back" name. A
/// single unresolved name used to cost a deck its whole analysis.
/// </summary>
public sealed class CardNameResolutionTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"advisor-test-{Guid.NewGuid():N}.db");
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
    public async Task FindByName_Should_ResolveDoubleFacedCard_When_OnlyFrontFaceIsGiven()
    {
        var printings = await _store.FindByNameAsync("Mosswood Dreadknight");

        var printing = Assert.Single(printings);
        Assert.Equal(86952, printing.GrpId);
        Assert.Equal("Mosswood Dreadknight // Dread Whispers", printing.Name);
    }

    [Fact]
    public async Task FindByName_Should_ResolveCard_When_FullDoubleFacedNameIsGiven()
    {
        var printings = await _store.FindByNameAsync("Mosswood Dreadknight // Dread Whispers");

        var printing = Assert.Single(printings);
        Assert.Equal(86952, printing.GrpId);
    }

    [Fact]
    public async Task FindByName_Should_NotMatchBackFace_When_BackFaceNameIsGiven()
    {
        // Arena never exports the back face, so resolving it is not worth a second index.
        Assert.Empty(await _store.FindByNameAsync("Dread Whispers"));
    }

    [Fact]
    public async Task FindByName_Should_EscapeLikeWildcards_When_NameContainsPercent()
    {
        // Unescaped, "%" would turn the front-face branch into a full-table match and
        // attach an arbitrary printing to the card.
        var printings = await _store.FindByNameAsync("%");

        Assert.Empty(printings);
    }

    [Fact]
    public async Task FindByName_Should_BeCaseInsensitive_When_FrontFaceCasingDiffers()
    {
        var printings = await _store.FindByNameAsync("mosswood dreadknight");

        Assert.Single(printings);
    }

    [Fact]
    public async Task FindByName_Should_ReturnEveryPrinting_When_CardHasSeveral()
    {
        var printings = await _store.FindByNameAsync("Lightning Bolt");

        Assert.Equal(2, printings.Count);
    }

    [Fact]
    public async Task FindByName_Should_MatchOnlyTheFrontFace_When_OtherNamesShareItsStart()
    {
        // The front-face match is a range ending exactly at "Fire //": a card whose name
        // merely starts with the same letters must stay out of it.
        var printings = await _store.FindByNameAsync("Fire");

        var printing = Assert.Single(printings);
        Assert.Equal("Fire // Ice", printing.Name);
    }

    [Fact]
    public async Task FindByName_Should_MatchFrontFace_When_CasingDiffersInsideTheName()
    {
        var printings = await _store.FindByNameAsync("FIRE");

        Assert.Equal("Fire // Ice", Assert.Single(printings).Name);
    }

    [Fact]
    public async Task FindByName_Should_UseTheNameIndex_ForBothBranches()
    {
        var plan = await QueryPlanAsync(CardDatabaseStore.FindByNameSql,
            ("$name", "Fire"), ("$frontFace", "Fire // "), ("$frontFaceEnd", "Fire //!"));

        Assert.DoesNotContain("SCAN", plan);
        // Not a covering index here: the lookup returns every column, so each hit still
        // reads its row. What matters is SEARCH, once for each branch of the UNION.
        Assert.Equal(2, CountOf(plan, "SEARCH cards USING INDEX ix_cards_name"));
    }

    // #59: the hover preview's URLs survive the round trip, and a card without them stays
    // without (an old database before its next Update cards).
    [Fact]
    public async Task CardStore_Should_RoundTripImageUrls()
    {
        var ojer = Assert.Single(await _store.FindByNameAsync("Ojer Taq, Deepest Foundation"));
        Assert.Equal(OjerFront, ojer.ImageUrl);
        Assert.Equal(OjerBack, ojer.BackImageUrl);

        var shock = Assert.Single(await _store.FindByNameAsync("Shock"));
        Assert.Null(shock.ImageUrl);
        Assert.Null(shock.BackImageUrl);
    }

    [Fact]
    public async Task WildcardCalculator_Should_CarryImageUrlsOfMatchedPrinting()
    {
        var deck = new CandidateDeck("manual:test", "Test", "", Formats.Standard.Key, 0,
            [new DeckCardRef("Ojer Taq, Deepest Foundation", 2, DeckBoard.Main)], DateTimeOffset.UtcNow);

        var result = await new WildcardCalculator(_store).AnalyzeAsync(deck, CollectionSnapshot.Empty, Formats.Standard, []);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(7, gap.GrpId);
        Assert.Equal(OjerFront, gap.ImageUrl);
        Assert.Equal(OjerBack, gap.BackImageUrl);
    }

    // A database imported before a card column existed (#59 images, #61 land flag, #76 Brawl
    // legality) has cards and that column empty everywhere: the app re-imports once on its own.
    // Only then; a partly filled or empty database is left alone.
    [Theory]
    [InlineData(19977, 0, 19977, 19977, 19977, true)]
    [InlineData(19977, 19977, 0, 19977, 19977, true)]
    [InlineData(19977, 19977, 19977, 0, 19977, true)]
    [InlineData(19977, 19977, 19977, 19977, 0, true)]
    [InlineData(19977, 19977, 19977, 19977, 19977, false)]
    [InlineData(19977, 3, 5, 7, 9, false)]
    [InlineData(0, 0, 0, 0, 0, false)]
    public void NeedsCardDataBackfill_Should_AskWhenAnyColumnIsEmptyEverywhere(
        int cards, int withImage, int withLandFlag, int withBrawl, int withStandardBrawl, bool expected)
    {
        Assert.Equal(expected, CardDatabaseStore.NeedsCardDataBackfill(
            new CardDataCounts(cards, withImage, withLandFlag, withBrawl, withStandardBrawl)));
    }

    [Fact]
    public async Task CountCardDataAsync_Should_CountEachColumn()
    {
        // Brawl legalities are always written by an import (0 or 1), so every card has them.
        Assert.Equal(new CardDataCounts(9, 1, 2, 9, 9), await _store.CountCardDataAsync());
    }

    // #61: the flag survives the round trip; a card from before migration 4 reads as unknown.
    [Fact]
    public async Task CardStore_Should_RoundTripNonBasicLandFlag()
    {
        Assert.True(Assert.Single(await _store.FindByNameAsync("Stomping Ground")).IsNonBasicLand);
        Assert.False(Assert.Single(await _store.FindByNameAsync("Ojer Taq, Deepest Foundation")).IsNonBasicLand);
        Assert.Null(Assert.Single(await _store.FindByNameAsync("Shock")).IsNonBasicLand);
    }

    [Fact]
    public async Task WildcardCalculator_Should_FlagNonBasicLandGaps()
    {
        var deck = new CandidateDeck("manual:test", "Test", "", Formats.Standard.Key, 0,
            [new DeckCardRef("Stomping Ground", 4, DeckBoard.Main), new DeckCardRef("Shock", 4, DeckBoard.Main)], DateTimeOffset.UtcNow);

        var result = await new WildcardCalculator(_store).AnalyzeAsync(deck, CollectionSnapshot.Empty, Formats.Standard, []);

        Assert.True(result.Gaps.Single(g => g.CardName == "Stomping Ground").IsNonBasicLand);
        Assert.False(result.Gaps.Single(g => g.CardName == "Shock").IsNonBasicLand);
    }

    private const string OjerFront = "https://cards.scryfall.io/normal/front/1/2/ojer.jpg";
    private const string OjerBack = "https://cards.scryfall.io/normal/back/1/2/ojer.jpg";

    private static int CountOf(string text, string part) =>
        (text.Length - text.Replace(part, "").Length) / part.Length;

    /// <summary>
    /// The plan SQLite picks for the store's own SQL. A lookup that scans the card table
    /// still returns the right rows - only ~200x slower - so nothing but the plan shows it.
    /// </summary>
    private async Task<string> QueryPlanAsync(string sql, params (string Name, string Value)[] parameters)
    {
        await using var connection = await new Database(_databasePath).OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) steps.Add(reader.GetString(3));
        return string.Join(" | ", steps);
    }

    private static async IAsyncEnumerable<CardInfo> Cards()
    {
        yield return Card(86952, "Mosswood Dreadknight // Dread Whispers");
        yield return Card(1, "Lightning Bolt");
        yield return Card(2, "Lightning Bolt");
        yield return Card(3, "Shock");
        yield return Card(4, "Fire // Ice");
        yield return Card(5, "Fireball");
        yield return Card(6, "Fire Ants");
        yield return Card(7, "Ojer Taq, Deepest Foundation // Temple of Civilization") with
        {
            ImageUrl = OjerFront,
            BackImageUrl = OjerBack,
            IsNonBasicLand = false,
        };
        yield return Card(8, "Stomping Ground") with { IsNonBasicLand = true };
        await Task.CompletedTask;
    }

    private static CardInfo Card(int grpId, string name) =>
        new(grpId, name, "TST", "{R}", "R", CardRarity.Common, StandardLegal: true, PioneerLegal: true);
}
