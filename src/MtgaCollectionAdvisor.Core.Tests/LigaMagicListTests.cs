using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

public sealed class LigaMagicListTests
{
    [Theory]
    [InlineData("BR", null, true)]
    [InlineData(null, "BR", true)]
    [InlineData("US", "br", true)]
    [InlineData("US", "US", false)]
    [InlineData("PT", "PT", false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    public void IsAvailable_Should_BeTrueOnlyForBrazil(string? homeLocation, string? regionalFormat, bool expected)
    {
        Assert.Equal(expected, LigaMagicList.IsAvailable(homeLocation, regionalFormat));
    }

    [Fact]
    public void Write_Should_ListQuantityAndNameWithMainboardFirst()
    {
        var deck = Analysis(
            Gap("Lightning Strike", 4, CardRarity.Common, DeckBoard.Sideboard),
            Gap("Sheoldred, the Apocalypse", 2, CardRarity.Mythic));

        Assert.Equal(Lines("2 Sheoldred, the Apocalypse", "4 Lightning Strike"), LigaMagicList.Write(deck));
    }

    [Fact]
    public void Write_Should_LeaveOutBasicLands()
    {
        var deck = Analysis(
            Gap("Mountain", 8, CardRarity.Basic),
            Gap("Snow-Covered Mountain", 4, CardRarity.Basic),
            Gap("Stomping Ground", 4, CardRarity.Rare, land: true));

        Assert.Equal(Lines("4 Stomping Ground"), LigaMagicList.Write(deck));
    }

    [Fact]
    public void Write_Should_LeaveOutNonBasicLandsWhenTheDeckShownExcludesThem()
    {
        var deck = Analysis(
            Gap("Stomping Ground", 4, CardRarity.Rare, land: true),
            Gap("Lightning Strike", 4, CardRarity.Common));

        Assert.Equal(Lines("4 Lightning Strike"), LigaMagicList.Write(deck.WithoutNonBasicLands()));
    }

    [Fact]
    public void Write_Should_UseTheFrontFaceName()
    {
        var deck = Analysis(Gap("Fable of the Mirror-Breaker // Reflection of Kiki-Jiki", 3, CardRarity.Rare));

        Assert.Equal(Lines("3 Fable of the Mirror-Breaker"), LigaMagicList.Write(deck));
    }

    [Fact]
    public void Write_Should_AddUpACardInBothBoards()
    {
        var deck = Analysis(
            Gap("Duress", 1, CardRarity.Common),
            Gap("Duress", 2, CardRarity.Common, DeckBoard.Sideboard));

        Assert.Equal(Lines("3 Duress"), LigaMagicList.Write(deck));
    }

    [Fact]
    public void Write_Should_BeEmptyForADeckOfBasics()
    {
        Assert.Equal("", LigaMagicList.Write(Analysis(Gap("Island", 20, CardRarity.Basic))));
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + Environment.NewLine));

    private static CardGap Gap(string name, int needed, CardRarity rarity, DeckBoard board = DeckBoard.Main, bool land = false) =>
        new(name, board, needed, 0, 1, rarity, IsNonBasicLand: land);

    private static DeckAnalysisResult Analysis(params CardGap[] gaps)
    {
        var deck = new CandidateDeck("manual:test", "Test", "", "standard", 0,
            [.. gaps.Select(g => new DeckCardRef(g.CardName, g.Needed, g.Board))], DateTimeOffset.UtcNow);
        return new DeckAnalysisResult(deck, WildcardNeed.Zero, 0, gaps.Sum(g => g.Needed), gaps, [], [], "", FitsFormatShape: true);
    }
}
