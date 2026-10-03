using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>#110: a deck is listed by its curve, then non-basic lands, then basics.</summary>
public sealed class DeckListOrderTests
{
    private static CardGap Spell(string name, double? manaValue) =>
        new(name, DeckBoard.Main, 4, 0, 1, CardRarity.Rare, ManaValue: manaValue);

    private static CardGap NonBasicLand(string name) =>
        new(name, DeckBoard.Main, 4, 0, 1, CardRarity.Rare, IsNonBasicLand: true, ManaValue: 0);

    private static CardGap Basic(string name) =>
        new(name, DeckBoard.Main, 10, 10, 1, CardRarity.Basic, ManaValue: 0);

    private static CardGap Unrecognised(string name) =>
        new(name, DeckBoard.Main, 2, 0, null, CardRarity.Unknown);

    private static string[] Names(params CardGap[] gaps) => [.. DeckListOrder.Order(gaps).Select(g => g.CardName)];

    [Fact]
    public void Spells_go_by_mana_value_then_name()
    {
        Assert.Equal(["Bolt", "Opt", "Sheoldred"],
            Names(Spell("Sheoldred", 4), Spell("Opt", 1), Spell("Bolt", 1)));
    }

    [Fact]
    public void Non_basic_lands_follow_the_spells_and_basics_come_last()
    {
        Assert.Equal(["Big Spell", "Hallowed Fountain", "Watery Grave", "Island", "Plains"],
            Names(Basic("Plains"), NonBasicLand("Watery Grave"), Basic("Island"), Spell("Big Spell", 7),
                NonBasicLand("Hallowed Fountain")));
    }

    [Fact]
    public void A_mana_value_not_yet_imported_comes_after_the_known_ones()
    {
        Assert.Equal(["Seven", "Unknown", "Island"], Names(Spell("Unknown", null), Basic("Island"), Spell("Seven", 7)));
    }

    [Fact]
    public void Unrecognised_cards_come_last()
    {
        Assert.Equal(["Opt", "Island", "Mystery"], Names(Unrecognised("Mystery"), Basic("Island"), Spell("Opt", 1)));
    }

    // By the front face (#61): an MDFC with a spell on the front is a 2-drop, not a land.
    [Fact]
    public void A_spell_with_a_land_on_its_back_sits_with_its_mana_value()
    {
        Assert.Equal(["One", "Fell the Profane // Fell Mire", "Three", "Watery Grave"],
            Names(NonBasicLand("Watery Grave"), Spell("Three", 3), Spell("Fell the Profane // Fell Mire", 2), Spell("One", 1)));
    }

    [Fact]
    public void Names_tie_break_without_regard_to_case()
    {
        Assert.Equal(["abc", "Abd"], Names(Spell("Abd", 2), Spell("abc", 2)));
    }
}
