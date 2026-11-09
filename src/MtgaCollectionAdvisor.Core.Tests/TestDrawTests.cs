using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>#126: sample opening hands from a deck, and the exact odds of lands in one.</summary>
public sealed class TestDrawTests
{
    private static CardGap Spell(string name, int copies, DeckBoard board = DeckBoard.Main) =>
        new(name, board, copies, 0, 1, CardRarity.Rare, ManaValue: 2);

    private static CardGap NonBasicLand(string name, int copies) =>
        new(name, DeckBoard.Main, copies, 0, 1, CardRarity.Rare, IsNonBasicLand: true, ManaValue: 0);

    private static CardGap Basic(string name, int copies) =>
        new(name, DeckBoard.Main, copies, copies, 1, CardRarity.Basic, ManaValue: 0);

    private static CardGap Unrecognised(string name, int copies) =>
        new(name, DeckBoard.Main, copies, 0, null, CardRarity.Basic);

    // 60 cards, 24 lands: the usual shape of a 60-card deck.
    private static IReadOnlyList<CardGap> SixtyCards() => TestDraw.Library(
    [
        Spell("A", 4), Spell("B", 4), Spell("C", 4), Spell("D", 4), Spell("E", 4), Spell("F", 4),
        Spell("G", 4), Spell("H", 4), Spell("I", 4), NonBasicLand("Dual", 4), Basic("Island", 20),
    ]);

    private static IReadOnlyList<CardGap> Library(int size, int lands) => TestDraw.Library(
        [.. new[] { Spell("Spell", size - lands), Basic("Island", lands) }.Where(g => g.Needed > 0)]);

    [Fact]
    public void Library_holds_one_entry_per_copy_of_the_main_board()
    {
        var library = TestDraw.Library([Spell("Opt", 4), Spell("Bolt", 2)]);

        Assert.Equal(6, library.Count);
        Assert.Equal(4, library.Count(g => g.CardName == "Opt"));
    }

    [Fact]
    public void Library_leaves_out_the_sideboard_and_the_commander()
    {
        var library = TestDraw.Library(
        [
            Spell("Commander", 1, DeckBoard.Commander),
            Spell("Spells", 60), Basic("Forest", 39),
            Spell("Sideboard card", 3, DeckBoard.Sideboard),
        ]);

        Assert.Equal(99, library.Count);
        Assert.DoesNotContain(library, g => g.Board != DeckBoard.Main);
    }

    [Fact]
    public void Deal_gives_seven_cards_from_the_library()
    {
        var library = SixtyCards();

        var hand = TestDraw.Deal(library, new Random(1));

        Assert.Equal(7, hand.Count);
        Assert.All(hand, card => Assert.Contains(card, library));
    }

    [Fact]
    public void Deal_never_gives_more_copies_than_the_deck_has()
    {
        var library = TestDraw.Library([Spell("Single", 1), Spell("Pair", 2), Basic("Island", 7)]);
        var random = new Random(2);

        for (var i = 0; i < 1000; i++)
        {
            var hand = TestDraw.Deal(library, random);
            Assert.All(hand.GroupBy(g => g.CardName), group => Assert.True(group.Count() <= group.First().Needed));
        }
    }

    [Fact]
    public void Deal_from_a_small_library_gives_all_of_it()
    {
        var library = Library(5, 2);

        Assert.Equal(5, TestDraw.Deal(library, new Random(3)).Count);
    }

    [Fact]
    public void Deal_does_not_change_the_library()
    {
        var library = SixtyCards();
        var before = library.ToList();

        TestDraw.Deal(library, new Random(4));

        Assert.Equal(before, library);
    }

    [Fact]
    public void Deal_rejects_a_negative_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestDraw.Deal(SixtyCards(), new Random(5), -1));
    }

    [Fact]
    public void Land_counts_dealt_match_the_odds()
    {
        var library = SixtyCards();
        var odds = TestDraw.Odds(library);
        var random = new Random(126);
        const int deals = 20_000;
        var seen = new int[8];

        for (var i = 0; i < deals; i++)
        {
            seen[TestDraw.Deal(library, random).Count(g => g.IsLand)]++;
        }

        for (var k = 0; k <= 7; k++)
        {
            Assert.InRange((double)seen[k] / deals, odds.ExactlyLands[k] - 0.01, odds.ExactlyLands[k] + 0.01);
        }
    }

    [Fact]
    public void Odds_of_sixty_cards_and_twenty_four_lands_match_the_known_values()
    {
        var odds = TestDraw.Odds(SixtyCards());

        Assert.Equal(60, odds.LibrarySize);
        Assert.Equal(24, odds.Lands);
        Assert.Equal(7, odds.HandSize);
        Assert.Equal(0.3087, odds.ExactlyLands[3], 4);
        Assert.Equal(0.7746, odds.TwoToFourLands, 4);
        Assert.Equal(2.8, odds.AverageLands, 9);
    }

    [Theory]
    [InlineData(40, 17)]
    [InlineData(60, 24)]
    [InlineData(99, 40)]
    [InlineData(59, 24)]
    public void Odds_sum_to_one(int size, int lands)
    {
        Assert.Equal(1.0, TestDraw.Odds(Library(size, lands)).ExactlyLands.Sum(), 9);
    }

    [Fact]
    public void Odds_of_all_lands_or_no_lands_are_certain()
    {
        Assert.Equal(1.0, TestDraw.Odds(Library(20, 20)).ExactlyLands[7], 9);
        Assert.Equal(1.0, TestDraw.Odds(Library(20, 0)).ExactlyLands[0], 9);
    }

    [Fact]
    public void Odds_of_an_empty_library_are_an_empty_hand()
    {
        var odds = TestDraw.Odds([]);

        Assert.Equal(0, odds.HandSize);
        Assert.Equal([1.0], odds.ExactlyLands);
        Assert.Equal(0, odds.AverageLands);
    }

    [Fact]
    public void Only_recognised_basics_and_non_basic_lands_are_lands()
    {
        Assert.True(Basic("Island", 1).IsLand);
        Assert.True(NonBasicLand("Watery Grave", 1).IsLand);
        Assert.False(Spell("Opt", 1).IsLand);
        Assert.False(Unrecognised("Islnad", 1).IsLand);
    }
}
