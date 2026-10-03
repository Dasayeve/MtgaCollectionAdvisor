using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>#101: MTG Arena's card database columns, read as Scryfall would write them.</summary>
public sealed class ArenaCardTextTests
{
    [Theory]
    [InlineData("o1oW", "{1}{W}")]
    [InlineData("o(G/W)", "{G/W}")]
    [InlineData("o10", "{10}")]
    [InlineData("oXoR", "{X}{R}")]
    [InlineData("o1o(B/P)o(B/P)", "{1}{B/P}{B/P}")]
    [InlineData("o8oCoC", "{8}{C}{C}")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ManaCost_converts_Arena_text(string? arena, string expected)
    {
        Assert.Equal(expected, ArenaCardText.ManaCost(arena));
    }

    // #110: Scryfall's cmc for the cards Scryfall doesn't have yet.
    [Theory]
    [InlineData("{2}{R}", 3)]
    [InlineData("{X}{R}", 1)]
    [InlineData("{X}{X}{G}", 1)]
    [InlineData("{2/W}{2/W}", 4)]
    [InlineData("{G/W}", 1)]
    [InlineData("{U/P}", 1)]
    [InlineData("{10}", 10)]
    [InlineData("{8}{C}{C}", 10)]
    [InlineData("", 0)]
    public void ManaValue_sums_the_symbols(string cost, double expected)
    {
        Assert.Equal(expected, ArenaCardText.ManaValue(cost));
    }

    // Arena writes a Room's two doors as one cost, and an adventure's main card alone.
    [Theory]
    [InlineData("o2oBo3oBoB", 8)] // Unholy Annex // Ritual Chamber
    [InlineData("o2oR", 3)]       // Bonecrusher Giant // Stomp
    public void ManaValue_of_an_Arena_cost_is_the_cards(string arena, double expected)
    {
        Assert.Equal(expected, ArenaCardText.ManaValue(ArenaCardText.ManaCost(arena)));
    }

    [Theory]
    [InlineData("<nobr>Blossom-Blessed</nobr> Angel", "Blossom-Blessed Angel")]
    [InlineData("Emrakul, the Exigent Doom", "Emrakul, the Exigent Doom")]
    [InlineData("Dusk /// Dawn", "Dusk // Dawn")]
    [InlineData("<sprite=\"SpriteSheet_MiscIcons\" name=\"arena_a\">Demilich", "A-Demilich")]
    public void CleanName_strips_markup_as_Scryfall_names_it(string arena, string expected)
    {
        Assert.Equal(expected, ArenaCardText.CleanName(arena));
    }

    [Theory]
    [InlineData(1, CardRarity.Basic)]
    [InlineData(2, CardRarity.Common)]
    [InlineData(3, CardRarity.Uncommon)]
    [InlineData(4, CardRarity.Rare)]
    [InlineData(5, CardRarity.Mythic)]
    [InlineData(0, CardRarity.Unknown)]
    [InlineData(9, CardRarity.Unknown)]
    public void Rarity_maps_Arena_values(int arena, CardRarity expected)
    {
        Assert.Equal(expected, ArenaCardText.Rarity(arena));
    }

    [Theory]
    [InlineData("5", "", true)]          // Land
    [InlineData("5", "2", true)]         // Legendary Land
    [InlineData("5", "1", false)]        // Basic Land
    [InlineData("5", "1,4", false)]      // Basic Snow Land
    [InlineData("2", "", false)]         // Creature
    [InlineData("2,5", "", true)]        // Land Creature
    [InlineData(null, null, false)]
    public void IsNonBasicLand_uses_types_and_supertypes(string? types, string? supertypes, bool expected)
    {
        Assert.Equal(expected, ArenaCardText.IsNonBasicLand(types, supertypes));
    }

    [Fact]
    public void Colors_joins_faces_in_WUBRG_order()
    {
        Assert.Equal("WG", ArenaCardText.Colors(["5", "1,5"]));
        Assert.Equal("", ArenaCardText.Colors(["", null]));
    }
}
