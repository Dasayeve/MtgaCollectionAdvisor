using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #101: Scryfall stays the source of every card's data; MTG Arena's card database only lends
/// the ids Scryfall doesn't publish yet.
/// </summary>
public sealed class CardSourceMergeTests
{
    private static readonly CardInfo Sheoldred =
        new(83000, "Sheoldred, the Apocalypse", "dmu", "{2}{B}{B}", "B", CardRarity.Mythic, true, true,
            ImageUrl: "https://cards.scryfall.io/sheoldred.jpg", IsNonBasicLand: false, BrawlLegal: true, StandardBrawlLegal: true);

    // Scryfall's FRA print with no arena_id yet: real legality and image, GrpId 0 until merged.
    private static readonly CardInfo AngelPrint =
        new(0, "Blossom-Blessed Angel // Seed Suture", "fra", "{3}{W}", "GW", CardRarity.Common, true, true,
            ImageUrl: "https://cards.scryfall.io/angel.jpg", BackImageUrl: "https://cards.scryfall.io/suture.jpg",
            IsNonBasicLand: false, BrawlLegal: true, StandardBrawlLegal: true);

    private static readonly Dictionary<(string, string), IReadOnlyList<CardInfo>> FraPrints = new()
    {
        [CardSourceMerge.Key("fra", "3")] = [AngelPrint],
    };

    private static ArenaDatabaseCard Arena(int grpId, string name, string set, string number,
        bool digital = false, bool rebalanced = false) =>
        new(grpId, name, set, number, CardRarity.Rare, "{1}{R}", "R", IsNonBasicLand: false, digital, rebalanced);

    [Fact]
    public void Scryfall_ids_are_kept_unchanged()
    {
        var arenaSaysOtherwise = Arena(83000, "Something Else", "FRA", "99");

        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints, [arenaSaysOtherwise]);

        Assert.Equal([Sheoldred], merge.Cards);
    }

    [Fact]
    public void An_idless_Scryfall_print_takes_the_Arena_id_by_set_and_number()
    {
        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints, [Arena(106227, "Blossom-Blessed Angel // Seed Suture", "FRA", "3")]);

        var angel = merge.Cards.Single(c => c.GrpId == 106227);
        Assert.Equal(AngelPrint with { GrpId = 106227 }, angel);
        Assert.True(angel.StandardLegal);
        Assert.Equal("https://cards.scryfall.io/angel.jpg", angel.ImageUrl);
    }

    [Fact]
    public void A_print_under_the_same_number_but_another_name_is_not_matched()
    {
        // Older Arena-only sets give one collector number to several different cards.
        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints, [Arena(106400, "Campus Crier", "FRA", "3")]);

        var crier = merge.Cards.Single(c => c.GrpId == 106400);
        Assert.Equal("Campus Crier", crier.Name);
        Assert.Null(crier.ImageUrl);
        Assert.Equal(new CardMergeCounts(1, 0, 1), merge.Counts);
    }

    [Fact]
    public void An_Arena_only_card_of_a_new_set_is_legal_everywhere_without_an_image()
    {
        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints, [Arena(106500, "Fracture Bolt", "FRA", "150")]);

        var bolt = merge.Cards.Single(c => c.GrpId == 106500);
        Assert.Equal(new CardInfo(106500, "Fracture Bolt", "fra", "{1}{R}", "R", CardRarity.Rare, true, true,
            IsNonBasicLand: false, BrawlLegal: true, StandardBrawlLegal: true), bolt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void An_Arena_only_card_is_legal_nowhere_when_digital_only_or_rebalanced(bool digital, bool rebalanced)
    {
        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints, [Arena(106500, "Fracture Bolt", "YFRA", "7", digital, rebalanced)]);

        var card = merge.Cards.Single(c => c.GrpId == 106500);
        Assert.False(card.StandardLegal || card.PioneerLegal || card.BrawlLegal || card.StandardBrawlLegal);
    }

    [Fact]
    public void An_Arena_only_card_of_a_set_Scryfall_knows_is_legal_nowhere()
    {
        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints, [Arena(83999, "Forgotten Card", "DMU", "300")]);

        Assert.False(merge.Cards.Single(c => c.GrpId == 83999).StandardLegal);
    }

    [Fact]
    public void An_Arena_only_card_of_a_set_known_under_Arenas_own_code_is_legal_nowhere()
    {
        // Arena calls Dominaria DAR, Scryfall dom: Arena's other DAR cards Scryfall has ids for make the set known.
        var dominaria = new CardInfo(67000, "Llanowar Elves", "dom", "{G}", "G", CardRarity.Common, false, true);
        var merge = CardSourceMerge.Merge([dominaria], FraPrints,
            [Arena(67000, "Llanowar Elves", "DAR", "168"), Arena(67999, "Forgotten Card", "DAR", "300")]);

        Assert.False(merge.Cards.Single(c => c.GrpId == 67999).PioneerLegal);
    }

    [Fact]
    public void An_Arena_only_card_of_a_set_Scryfall_lists_no_Arena_prints_for_is_legal_nowhere()
    {
        // Arena's file keeps old cards under codes Scryfall never gave Arena ids (Lotus Petal
        // under TMP): not a new set, so never Standard-legal (found in the v0.7.0 smoke test).
        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints, [Arena(9475, "Lotus Petal", "TMP", "294")]);

        var petal = merge.Cards.Single(c => c.GrpId == 9475);
        Assert.False(petal.StandardLegal || petal.PioneerLegal || petal.BrawlLegal || petal.StandardBrawlLegal);
    }

    [Fact]
    public void An_Arena_only_card_takes_the_legality_most_of_its_sets_prints_have()
    {
        // PZA-style: Scryfall lists the set's Arena prints without ids, none of them Standard-legal
        // (found in the v0.7.0 smoke test: Umezawa's Jitte under PZA had become Standard-legal).
        var promo = AngelPrint with { SetCode = "pza", StandardLegal = false, StandardBrawlLegal = false, PioneerLegal = false };
        var prints = new Dictionary<(string, string), IReadOnlyList<CardInfo>>
        {
            [CardSourceMerge.Key("pza", "1")] = [promo with { Name = "One" }],
            [CardSourceMerge.Key("pza", "2")] = [promo with { Name = "Two", BrawlLegal = false }],
            [CardSourceMerge.Key("pza", "3")] = [promo with { Name = "Three" }],
        };

        var merge = CardSourceMerge.Merge([Sheoldred], prints, [Arena(9999, "Umezawa's Jitte", "PZA", "99")]);

        var jitte = merge.Cards.Single(c => c.GrpId == 9999);
        Assert.False(jitte.StandardLegal || jitte.PioneerLegal || jitte.StandardBrawlLegal);
        Assert.True(jitte.BrawlLegal); // 2 of 3 prints are Brawl-legal
    }

    [Fact]
    public void An_Arena_only_card_of_a_set_nobody_lists_is_legal_nowhere()
    {
        var merge = CardSourceMerge.Merge([Sheoldred], new Dictionary<(string, string), IReadOnlyList<CardInfo>>(),
            [Arena(106500, "Fracture Bolt", "FRA", "150")]);

        Assert.False(merge.Cards.Single(c => c.GrpId == 106500).StandardLegal);
    }

    [Fact]
    public void Each_path_is_counted()
    {
        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints,
        [
            Arena(83000, "Sheoldred, the Apocalypse", "DMU", "107"),
            Arena(106227, "Blossom-Blessed Angel // Seed Suture", "FRA", "3"),
            Arena(106500, "Fracture Bolt", "FRA", "150"),
            Arena(106500, "Fracture Bolt", "FRA", "150"),
        ]);

        Assert.Equal(new CardMergeCounts(FromScryfall: 1, MatchedByNumber: 1, ArenaOnly: 1), merge.Counts);
        Assert.Equal(3, merge.Cards.Count);
    }

    [Fact]
    public void With_no_Arena_cards_the_result_is_Scryfalls()
    {
        var merge = CardSourceMerge.Merge([Sheoldred], FraPrints, []);

        Assert.Equal([Sheoldred], merge.Cards);
        Assert.Equal(new CardMergeCounts(1, 0, 0), merge.Counts);
    }
}
