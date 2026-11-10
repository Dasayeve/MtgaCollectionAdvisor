using System.Text.Json;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>#125: which of an Archidekt deck's cards are in the deck, now that the Brawls need an exact size.</summary>
public sealed class ArchidektDeckReadTests
{
    private static IReadOnlyList<DeckCardRef> Read(string json) =>
        ArchidektClient.ReadCards(JsonSerializer.Deserialize<ArchidektDeckDetail>(json)!);

    private static string Card(string name, int quantity, params string[] categories) =>
        JsonSerializer.Serialize(new { quantity, categories, card = new { oracleCard = new { name } } });

    [Fact]
    public void ReadCards_Should_LeaveOutCategoriesNotIncludedInTheDeck()
    {
        // A real Standard Brawl list read as 63 cards: 3 of them were in "Maybe Draw".
        var cards = Read($$"""
            {"categories": [{"name": "Draw", "includedInDeck": true}, {"name": "Maybe Draw", "includedInDeck": false},
                            {"name": "Commander", "includedInDeck": true}],
             "cards": [{{Card("King T'Challa", 1, "Commander")}}, {{Card("Island", 59, "Draw")}},
                       {{Card("Opt", 3, "Maybe Draw")}}]}
            """);

        Assert.Equal(60, cards.Sum(c => c.Quantity));
        Assert.DoesNotContain(cards, c => c.Name == "Opt");
    }

    [Fact]
    public void ReadCards_Should_GoByTheFirstCategory()
    {
        var cards = Read($$"""
            {"categories": [{"name": "Draw", "includedInDeck": true}, {"name": "Considering", "includedInDeck": false}],
             "cards": [{{Card("Opt", 4, "Draw", "Considering")}}, {{Card("Quick Study", 2, "Considering", "Draw")}}]}
            """);

        Assert.Equal(["Opt"], cards.Select(c => c.Name));
    }

    [Fact]
    public void ReadCards_Should_CountCards_WhenTheDeckSaysNothingOfItsCategories()
    {
        var cards = Read($$"""
            {"cards": [{{Card("Swamp", 56, "Land")}}, {{Card("Duress", 4)}}, {{Card("Ghost", 2, "Maybeboard")}},
                       {{Card("Lurrus of the Dream-Den", 1, "Sideboard")}}]}
            """);

        Assert.Equal(60, cards.Where(c => c.Board == DeckBoard.Main).Sum(c => c.Quantity));
        Assert.Equal(DeckBoard.Sideboard, Assert.Single(cards, c => c.Name == "Lurrus of the Dream-Den").Board);
        Assert.DoesNotContain(cards, c => c.Name == "Ghost");
    }
}
