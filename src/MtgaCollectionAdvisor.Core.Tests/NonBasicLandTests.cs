using System.Text.Json;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #61: what counts as a non-basic land (the front face is a Land and not Basic), and a deck
/// priced and exported without them.
/// </summary>
public sealed class NonBasicLandTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static ScryfallCard Parse(string json) => JsonSerializer.Deserialize<ScryfallCard>(json, Options)!;

    [Theory]
    [InlineData("Land — Mountain Forest")]
    [InlineData("Legendary Land")]
    [InlineData("Land Creature — Forest Dryad")]
    [InlineData("Artifact Land")]
    [InlineData("Land")]
    public void IsNonBasicLandType_Should_BeTrue_ForNonBasicLands(string typeLine)
    {
        Assert.True(ScryfallCard.IsNonBasicLandType(typeLine));
    }

    [Theory]
    [InlineData("Basic Land — Island")]
    [InlineData("Basic Snow Land — Forest")]
    [InlineData("Creature — Elf Druid")]
    [InlineData("Sorcery")]
    [InlineData("Enchantment — Aura")]
    [InlineData("Creature — Landfall Beast")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNonBasicLandType_Should_BeFalse_ForBasicsAndNonLands(string? typeLine)
    {
        Assert.False(ScryfallCard.IsNonBasicLandType(typeLine));
    }

    // Snow-covered basics and Wastes are basic (maintainer's call): no wildcard, and never
    // counted among the non-basic lands.
    [Theory]
    [InlineData("Basic Land — Island")]
    [InlineData("Basic Snow Land — Island")]
    [InlineData("Basic Land")]
    public void IsBasicLandType_Should_BeTrue_ForEveryBasic(string typeLine)
    {
        Assert.True(ScryfallCard.IsBasicLandType(typeLine));
        Assert.False(ScryfallCard.IsNonBasicLandType(typeLine));
    }

    [Theory]
    [InlineData("Land — Mountain Forest")]
    [InlineData("Snow Land")]
    [InlineData("Creature — Elf")]
    [InlineData(null)]
    public void IsBasicLandType_Should_BeFalse_ForEverythingElse(string? typeLine)
    {
        Assert.False(ScryfallCard.IsBasicLandType(typeLine));
    }

    [Fact]
    public void IsNonBasicLand_Should_UseTheFrontFace()
    {
        // A spell with a land on its back: played, and crafted, as the spell.
        Assert.False(Parse("""
            {"name":"Spell // Land","type_line":"Sorcery // Land","card_faces":[{"type_line":"Sorcery"},{"type_line":"Land"}]}
            """).IsNonBasicLand());

        // A creature that transforms into a land.
        Assert.False(Parse("""
            {"name":"Ojer Taq","type_line":"Legendary Creature — God // Land","card_faces":[{"type_line":"Legendary Creature — God"},{"type_line":"Land"}]}
            """).IsNonBasicLand());

        // A land on the front, a spell on the back.
        Assert.True(Parse("""
            {"name":"Land // Spell","type_line":"Land // Instant","card_faces":[{"type_line":"Land"},{"type_line":"Instant"}]}
            """).IsNonBasicLand());

        // A single-faced shockland.
        Assert.True(Parse("""{"name":"Stomping Ground","type_line":"Land — Mountain Forest"}""").IsNonBasicLand());
    }

    // ---------- A deck without its non-basic lands ----------

    private static CardGap Gap(string name, int needed, int owned, CardRarity rarity, bool land = false, DeckBoard board = DeckBoard.Main) =>
        new(name, board, needed, owned, GrpId: 1, rarity, IsNonBasicLand: land);

    private static DeckAnalysisResult Analysis(params CardGap[] gaps)
    {
        var deck = new CandidateDeck("manual:test", "Test", "", "standard", 0,
            [.. gaps.Select(g => new DeckCardRef(g.CardName, g.Needed, g.Board))], DateTimeOffset.UtcNow);

        var needed = gaps.Where(g => g.Rarity != CardRarity.Basic)
            .Aggregate(WildcardNeed.Zero, (sum, g) => sum.Add(WildcardNeed.FromGap(g)));

        return new DeckAnalysisResult(deck, needed,
            gaps.Sum(g => Math.Min(g.Owned, g.Needed)), gaps.Sum(g => g.Needed),
            gaps, ["Stomping Ground"], [], "RG");
    }

    private static readonly DeckAnalysisResult Sample = Analysis(
        Gap("Stomping Ground", 4, 1, CardRarity.Rare, land: true),
        Gap("Lush Portico", 2, 2, CardRarity.Rare, land: true, board: DeckBoard.Sideboard),
        Gap("Mountain", 8, 8, CardRarity.Basic),
        Gap("Lightning Strike", 4, 1, CardRarity.Common),
        Gap("Sheoldred, the Apocalypse", 2, 0, CardRarity.Mythic));

    [Fact]
    public void WithoutNonBasicLands_Should_KeepTheShapeVerdict()
    {
        // A pricing view, not a new deck (#125): a wrong-shape deck stays one.
        Assert.False((Sample with { FitsFormatShape = false }).WithoutNonBasicLands().FitsFormatShape);
        Assert.True(Sample.WithoutNonBasicLands().FitsFormatShape);
    }

    [Fact]
    public void WithoutNonBasicLands_Should_RemoveThoseLinesFromGapsAndDeck()
    {
        var without = Sample.WithoutNonBasicLands();

        Assert.Equal(["Mountain", "Lightning Strike", "Sheoldred, the Apocalypse"], without.Gaps.Select(g => g.CardName));
        Assert.Equal(["Mountain", "Lightning Strike", "Sheoldred, the Apocalypse"], without.Deck.Cards.Select(c => c.Name));
        Assert.Empty(without.UnavailableOnArena);
        Assert.Equal(Sample.Colors, without.Colors);
    }

    [Fact]
    public void WithoutNonBasicLands_Should_RecomputeCostAndOwned()
    {
        var without = Sample.WithoutNonBasicLands();

        // Lightning Strike 3 commons + Sheoldred 2 mythics; the 3 missing rare lands are gone.
        Assert.Equal(new WildcardNeed(3, 0, 0, 2), without.Needed);
        Assert.Equal(8 + 1 + 0, without.OwnedCopies);
        Assert.Equal(8 + 4 + 2, without.TotalCopies);
    }

    [Fact]
    public void WithoutNonBasicLands_Should_KeepBasicLands()
    {
        Assert.Contains(Sample.WithoutNonBasicLands().Gaps, g => g.CardName == "Mountain");
    }

    [Fact]
    public void WithoutNonBasicLands_Should_ReturnSame_When_NoneInDeck()
    {
        var noLands = Analysis(Gap("Lightning Strike", 4, 1, CardRarity.Common));

        Assert.False(noLands.HasNonBasicLands);
        Assert.Same(noLands, noLands.WithoutNonBasicLands());
    }

    [Fact]
    public void NonBasicLandShare_Should_SumCopiesAndWildcards()
    {
        var (copies, cost) = Sample.NonBasicLandShare();

        Assert.Equal(6, copies);
        Assert.Equal(new WildcardNeed(0, 0, 3, 0), cost);
    }
}
