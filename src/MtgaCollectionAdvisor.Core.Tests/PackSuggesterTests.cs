using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #84: which packs would get a deck the most of what it is missing. Rounds take the set with the
/// most missing rares and mythics, then copies, then the newest; each card appears once.
/// </summary>
public sealed class PackSuggesterTests
{
    private static readonly Dictionary<string, (string Name, DateOnly Released)> Sets = new()
    {
        ["fra"] = ("Reality Fracture", new DateOnly(2026, 10, 2)),
        ["dsk"] = ("Duskmourn: House of Horror", new DateOnly(2024, 9, 27)),
        ["fdn"] = ("Foundations", new DateOnly(2024, 11, 15)),
        ["blb"] = ("Bloomburrow", new DateOnly(2024, 8, 2)),
        ["j25"] = ("Foundations Jumpstart", new DateOnly(2024, 11, 15)),
        ["big"] = ("The Big Score", new DateOnly(2024, 4, 19)),
        ["ydft"] = ("Alchemy: Aetherdrift", new DateOnly(2025, 3, 4)),
        ["tmp"] = ("Tempest", new DateOnly(1997, 10, 14)),
    };

    private static CardInfo Print(string name, string set, CardRarity rarity) =>
        new(1, name, set, "{1}", "R", rarity, true, true,
            ImageUrl: $"https://cards.scryfall.io/{set}/{name}.jpg",
            SetName: Sets[set].Name, SetReleasedAt: Sets[set].Released);

    private static MissingCard Card(string name, int missing, params (string Set, CardRarity Rarity)[] printings) =>
        new(name, missing, printings.Select(p => Print(name, p.Set, p.Rarity)).ToList());

    private static string[] Order(PackSuggestion suggestion) => suggestion.Packs.Select(p => p.SetCode).ToArray();

    [Fact]
    public void Suggest_takes_the_set_with_the_most_rares_and_mythics_first()
    {
        var suggestion = PackSuggester.Suggest(
        [
            Card("Common A", 4, ("dsk", CardRarity.Common)), Card("Common B", 4, ("dsk", CardRarity.Common)),
            Card("Mythic A", 1, ("blb", CardRarity.Mythic)), Card("Mythic B", 1, ("blb", CardRarity.Mythic)),
            Card("Mythic C", 1, ("blb", CardRarity.Mythic)),
        ]);

        // Three mythics beat eight commons: packs are bought for rares and mythics.
        Assert.Equal(["BLB", "DSK"], Order(suggestion));
        Assert.Equal(3, suggestion.Packs[0].RareAndMythicCopies);
        Assert.Equal(8, suggestion.Packs[1].Copies(CardRarity.Common));
    }

    [Fact]
    public void Suggest_breaks_a_rare_tie_by_total_copies()
    {
        var suggestion = PackSuggester.Suggest(
        [
            Card("Rare A", 1, ("dsk", CardRarity.Rare)),
            Card("Rare B", 1, ("blb", CardRarity.Rare)), Card("Uncommon B", 3, ("blb", CardRarity.Uncommon)),
        ]);

        Assert.Equal(["BLB", "DSK"], Order(suggestion));
    }

    [Fact]
    public void Suggest_breaks_a_full_tie_by_the_newest_set()
    {
        var suggestion = PackSuggester.Suggest(
        [
            Card("Rare A", 2, ("dsk", CardRarity.Rare)),
            Card("Rare B", 2, ("fdn", CardRarity.Rare)),
            Card("Rare C", 2, ("fra", CardRarity.Rare)),
        ]);

        Assert.Equal(["FRA", "FDN", "DSK"], Order(suggestion));
    }

    [Fact]
    public void A_reprint_goes_to_the_set_that_covers_the_most()
    {
        // The issue's example: A only in FRA; B and C only in DSK; D in FRA, DSK and FDN.
        var suggestion = PackSuggester.Suggest(
        [
            Card("A", 1, ("fra", CardRarity.Rare)),
            Card("B", 1, ("dsk", CardRarity.Rare)),
            Card("C", 1, ("dsk", CardRarity.Rare)),
            Card("D", 1, ("fra", CardRarity.Rare), ("dsk", CardRarity.Rare), ("fdn", CardRarity.Rare)),
        ]);

        Assert.Equal(["DSK", "FRA"], Order(suggestion));
        Assert.Equal(["B", "C", "D"], suggestion.Packs[0].Cards.Select(c => c.Name).Order());
        Assert.Equal(["A"], suggestion.Packs[1].Cards.Select(c => c.Name));
    }

    [Fact]
    public void Every_missing_card_appears_once()
    {
        var suggestion = PackSuggester.Suggest(
        [
            Card("A", 2, ("fra", CardRarity.Rare), ("fdn", CardRarity.Uncommon)),
            Card("B", 1, ("fdn", CardRarity.Mythic), ("dsk", CardRarity.Mythic)),
            Card("C", 4, ("dsk", CardRarity.Common), ("fra", CardRarity.Common), ("fdn", CardRarity.Common)),
            Card("D", 1, ("j25", CardRarity.Rare)),
        ]);

        var names = suggestion.Packs.SelectMany(p => p.Cards).Concat(suggestion.WildcardsOnly).Select(c => c.Name).ToList();
        Assert.Equal(["A", "B", "C", "D"], names.Order());
    }

    [Fact]
    public void A_reprint_lists_the_other_pack_sets_it_comes_in()
    {
        var suggestion = PackSuggester.Suggest(
        [
            Card("Mythic", 1, ("dsk", CardRarity.Mythic)),
            Card("Reprint", 1, ("dsk", CardRarity.Rare), ("fra", CardRarity.Rare), ("blb", CardRarity.Rare), ("j25", CardRarity.Rare)),
        ]);

        var reprint = suggestion.Packs.Single().Cards.Single(c => c.Name == "Reprint");
        // Newest first, the chosen set left out, and never a set with no packs (Jumpstart).
        Assert.Equal(["Reality Fracture", "Bloomburrow"], reprint.AlsoIn);
    }

    [Fact]
    public void Rarity_is_the_printings_in_that_set()
    {
        // Uncommon in Foundations, rare in Reality Fracture: FRA counts it as a rare, and wins.
        var suggestion = PackSuggester.Suggest(
        [
            Card("Shifting", 2, ("fdn", CardRarity.Uncommon), ("fra", CardRarity.Rare)),
            Card("Only FDN", 1, ("fdn", CardRarity.Uncommon)),
        ]);

        Assert.Equal("FRA", suggestion.Packs[0].SetCode);
        Assert.Equal(CardRarity.Rare, suggestion.Packs[0].Cards.Single().Rarity);
    }

    [Theory]
    [InlineData("j25")]  // Jumpstart
    [InlineData("big")]  // a bonus sheet
    [InlineData("ydft")] // Alchemy
    [InlineData("tmp")]  // an old Arena-only card
    public void A_card_with_no_pack_printing_is_wildcards_only(string set)
    {
        var suggestion = PackSuggester.Suggest([Card("Nowhere", 2, (set, CardRarity.Rare)), Card("Somewhere", 1, ("fra", CardRarity.Common))]);

        var only = Assert.Single(suggestion.WildcardsOnly);
        Assert.Equal("Nowhere", only.Name);
        // It counts toward no pack: the only pack scores its own card.
        Assert.Equal(1, Assert.Single(suggestion.Packs).TotalCopies);
    }

    [Fact]
    public void A_rebalanced_card_is_wildcards_only_even_in_a_pack_set()
    {
        var suggestion = PackSuggester.Suggest([Card("A-Rebalanced", 1, ("dsk", CardRarity.Rare))]);

        Assert.Empty(suggestion.Packs);
        Assert.Single(suggestion.WildcardsOnly);
    }

    [Fact]
    public void A_set_left_with_nothing_is_not_listed()
    {
        // FDN could give both cards, but DSK and FRA take them first: FDN has nothing left.
        var suggestion = PackSuggester.Suggest(
        [
            Card("A", 1, ("dsk", CardRarity.Mythic), ("fdn", CardRarity.Common)),
            Card("B", 1, ("fra", CardRarity.Mythic), ("fdn", CardRarity.Common)),
        ]);

        Assert.DoesNotContain("FDN", Order(suggestion));
        Assert.Equal(2, suggestion.Packs.Count);
    }

    [Fact]
    public void Complete_cards_are_left_out()
    {
        var suggestion = PackSuggester.Suggest([Card("Owned", 0, ("fra", CardRarity.Rare))]);

        Assert.Equal(PackSuggestion.Empty.Packs, suggestion.Packs);
        Assert.Empty(suggestion.WildcardsOnly);
    }

    [Theory]
    [InlineData("fra", true)]
    [InlineData("dom", true)]
    [InlineData("klr", true)]
    [InlineData("ltr", true)]
    [InlineData("mh3", true)]
    [InlineData("FRA", true)]
    [InlineData("j25", false)]
    [InlineData("big", false)]
    [InlineData("lrw", false)]
    [InlineData("2xm", false)]
    [InlineData("pza", false)]
    public void HasPacks_uses_the_pack_set_list(string set, bool expected)
    {
        Assert.Equal(expected, PackSets.HasPacks(new CardInfo(1, "Card", set, "{1}", "R", CardRarity.Rare, true, true)));
    }

    [Fact]
    public void The_pack_set_list_is_lower_case_and_complete()
    {
        // 51 sets on Wizards' drop-rates page, Alchemy packs left out.
        Assert.Equal(51, PackSets.Codes.Count);
        Assert.All(PackSets.Codes, code => Assert.Equal(code.ToLowerInvariant(), code));
    }
}
