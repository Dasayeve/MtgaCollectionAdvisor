using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Creators;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #87: a card name the card database doesn't know costs nothing, because its cost is unknown.
/// A list with such cards (a creator's list in Portuguese) must never be called craftable,
/// legal, or within a budget: what can't be known is not claimed.
/// </summary>
public sealed class UnrecognisedCardsTests
{
    private static readonly WildcardInventory RichWallet = new(100, 100, 100, 100);

    [Fact]
    public void A_list_of_unknown_names_recognises_nothing_and_is_not_craftable()
    {
        var withOneKnown = CreatorVideoPricingTests.Analysis(unrecognised: 3);
        var analysis = withOneKnown with { Gaps = [.. withOneKnown.Gaps.Where(g => !g.AvailableOnArena)] };

        Assert.True(analysis.NothingRecognised);
        Assert.Equal(12, analysis.UnrecognisedCopies);
        Assert.Equal(0, analysis.Needed.Total); // the trap: nothing read, nothing to pay
        Assert.False(analysis.IsCraftableWith(RichWallet));
    }

    [Fact]
    public void One_unknown_card_is_enough_to_withhold_craftable()
    {
        var analysis = CreatorVideoPricingTests.Analysis(rares: 1, unrecognised: 1);

        Assert.False(analysis.NothingRecognised);
        Assert.True(analysis.Needed.IsAffordableWith(RichWallet));
        Assert.False(analysis.IsCraftableWith(RichWallet));
    }

    [Fact]
    public void A_fully_recognised_list_is_craftable_when_the_wallet_covers_it()
    {
        var analysis = CreatorVideoPricingTests.Analysis(rares: 1);

        Assert.Equal(0, analysis.UnrecognisedCopies);
        Assert.True(analysis.IsCraftableWith(RichWallet));
    }

    [Fact]
    public void A_creator_list_with_unknown_cards_is_not_legal_in_an_app_format()
    {
        var card = new CreatorVideoCard(Video("pt"), Formats.Standard, CreatorVideoPricingTests.Analysis(unrecognised: 3));

        Assert.False(card.IsLegalInAppFormat);
    }

    [Fact]
    public void Creator_filters_leave_out_unknown_lists_and_sort_them_after_known_costs()
    {
        var known = new CreatorVideoCard(Video("known"), Formats.Standard, CreatorVideoPricingTests.Analysis(rares: 3));
        var unknown = new CreatorVideoCard(Video("pt"), Formats.Standard, CreatorVideoPricingTests.Analysis(unrecognised: 3));

        var craftable = CreatorVideoFilter.Apply([known, unknown], new CreatorVideoFilterCriteria { OnlyCraftable = true }, RichWallet);
        var appFormats = CreatorVideoFilter.Apply([known, unknown], new CreatorVideoFilterCriteria { OnlyAppFormats = true }, RichWallet);
        var cheapest = CreatorVideoFilter.Apply([unknown, known], new CreatorVideoFilterCriteria { Sort = CreatorVideoSort.Cheapest }, RichWallet);

        Assert.Equal("known", Assert.Single(craftable).Video.VideoId);
        Assert.Equal("known", Assert.Single(appFormats).Video.VideoId);
        Assert.Equal(["known", "pt"], cheapest.Select(c => c.Video.VideoId));
    }

    [Fact]
    public void Deck_filters_leave_out_a_deck_with_unknown_cards_from_craftable_and_budgets()
    {
        // My decks keeps decks with unknown cards (IncludeUnplayable); the filters that claim
        // a cost must still leave them out.
        var decks = new[] { CreatorVideoPricingTests.Analysis(rares: 1), CreatorVideoPricingTests.Analysis(unrecognised: 2) };

        Assert.Single(DeckFilter.Apply(decks, new DeckFilterCriteria { IncludeUnplayable = true, OnlyCraftable = true }, RichWallet));
        Assert.Single(DeckFilter.Apply(decks, new DeckFilterCriteria { IncludeUnplayable = true, MaxWildcards = 5 }, RichWallet));
        Assert.Single(DeckFilter.Apply(decks, new DeckFilterCriteria
        {
            IncludeUnplayable = true,
            RarityBudget = new RarityBudget(null, null, 5, null),
        }, RichWallet));
        Assert.Equal(2, DeckFilter.Apply(decks, new DeckFilterCriteria { IncludeUnplayable = true }, RichWallet).Count);
    }

    private static CreatorVideo Video(string id) =>
        new(id, "Alice", "Title", new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero),
            DeckSourceKind.None, null, null, null, null);
}
