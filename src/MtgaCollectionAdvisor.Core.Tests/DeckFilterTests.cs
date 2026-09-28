using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

public class DeckFilterTests
{
    private static readonly WildcardInventory EmptyWallet = WildcardInventory.Empty;

    [Fact]
    public void Apply_Should_KeepDeck_When_ItContainsEveryRequiredCard()
    {
        var deck = Deck("Boros", "WR", cards: ["Lightning Bolt", "Mountain", "Plains"]);
        var criteria = new DeckFilterCriteria { ContainsCards = ["Lightning Bolt", "Plains"] };

        var result = DeckFilter.Apply([deck], criteria, EmptyWallet);

        Assert.Single(result);
    }

    [Fact]
    public void Apply_Should_DropDeck_When_ItIsMissingOneRequiredCard()
    {
        var deck = Deck("Boros", "WR", cards: ["Lightning Bolt", "Mountain"]);
        var criteria = new DeckFilterCriteria { ContainsCards = ["Lightning Bolt", "Sheoldred"] };

        var result = DeckFilter.Apply([deck], criteria, EmptyWallet);

        Assert.Empty(result);
    }

    [Fact]
    public void Apply_Should_DropDeck_When_ItContainsAnyExcludedCard()
    {
        var deck = Deck("Mono Black", "B", cards: ["Swamp", "Sheoldred"]);
        var criteria = new DeckFilterCriteria { ExcludesCards = ["Sheoldred"] };

        var result = DeckFilter.Apply([deck], criteria, EmptyWallet);

        Assert.Empty(result);
    }

    [Fact]
    public void Apply_Should_MatchCardNames_CaseInsensitively()
    {
        var deck = Deck("Burn", "R", cards: ["Lightning Bolt"]);
        var criteria = new DeckFilterCriteria { ContainsCards = ["lightning bolt"] };

        var result = DeckFilter.Apply([deck], criteria, EmptyWallet);

        Assert.Single(result);
    }

    [Fact]
    public void Apply_Should_MatchSideboardCards()
    {
        var deck = Deck("Burn", "R", cards: ["Mountain"], sideboard: ["Abrade"]);
        var criteria = new DeckFilterCriteria { ContainsCards = ["Abrade"] };

        var result = DeckFilter.Apply([deck], criteria, EmptyWallet);

        Assert.Single(result);
    }

    [Fact]
    public void Apply_Should_CombineCardFilters_WithColorAndBudgetFilters()
    {
        var cheapBoros = Deck("Cheap Boros", "WR", cards: ["Lightning Bolt"], rares: 2);
        var expensiveBoros = Deck("Expensive Boros", "WR", cards: ["Lightning Bolt"], rares: 40);
        var cheapMono = Deck("Cheap Mono Red", "R", cards: ["Lightning Bolt"], rares: 1);

        var criteria = new DeckFilterCriteria
        {
            Colors = new HashSet<char> { 'W' },
            MaxWildcards = 10,
            ContainsCards = ["Lightning Bolt"]
        };

        var result = DeckFilter.Apply([cheapBoros, expensiveBoros, cheapMono], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Cheap Boros", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_ReturnEverything_When_CriteriaIsEmpty()
    {
        var decks = new[] { Deck("A", "W", ["Plains"]), Deck("B", "U", ["Island"]) };

        var result = DeckFilter.Apply(decks, new DeckFilterCriteria(), EmptyWallet);

        Assert.Equal(2, result.Count);
        Assert.True(new DeckFilterCriteria().IsEmpty);
    }

    [Fact]
    public void Apply_Should_KeepOnlyExactColorCombination_When_ExactColorsIsSet()
    {
        var boros = Deck("Boros", "WR", ["Plains"]);
        var mono = Deck("Mono White", "W", ["Plains"]);

        var criteria = new DeckFilterCriteria
        {
            Colors = new HashSet<char> { 'W' },
            ExactColors = true
        };

        var result = DeckFilter.Apply([boros, mono], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Mono White", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_KeepOnlyAffordableDecks_When_OnlyCraftableIsSet()
    {
        var affordable = Deck("Affordable", "R", ["Mountain"], rares: 2);
        var tooExpensive = Deck("Too Expensive", "R", ["Mountain"], rares: 9);
        var wallet = new WildcardInventory(0, 0, Rares: 4, Mythics: 0);

        var result = DeckFilter.Apply(
            [affordable, tooExpensive], new DeckFilterCriteria { OnlyCraftable = true }, wallet);

        Assert.Single(result);
        Assert.Equal("Affordable", result[0].Deck.Name);
    }

    // #57: wildcards never read can't say what is craftable; the filter keeps every deck
    // rather than claiming none can be built.
    [Fact]
    public void Apply_Should_SkipCraftableOnly_When_WalletUnknown()
    {
        var affordable = Deck("Affordable", "R", ["Mountain"], rares: 2);
        var tooExpensive = Deck("Too Expensive", "R", ["Mountain"], rares: 9);

        var result = DeckFilter.Apply([affordable, tooExpensive], new DeckFilterCriteria { OnlyCraftable = true }, wallet: null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Apply_Should_DropUnplayableDecks_When_IncludeUnplayableIsFalse()
    {
        var playable = Deck("Playable", "R", ["Mountain"]);
        var unplayable = Deck("Not On Arena", "R", ["Mountain"], unavailableOnArena: ["Mosswood Dreadknight"]);

        var result = DeckFilter.Apply([playable, unplayable], new DeckFilterCriteria(), EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Playable", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_DropIllegalDecks_When_IncludeUnplayableIsFalse()
    {
        var legal = Deck("Legal", "R", ["Mountain"]);
        var illegal = Deck("Rotated Out", "R", ["Mountain"], illegalInFormat: ["Lightning Bolt"]);

        var result = DeckFilter.Apply([legal, illegal], new DeckFilterCriteria(), EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Legal", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_KeepUnplayableDecks_When_IncludeUnplayableIsSet()
    {
        var unavailable = Deck("Not On Arena", "R", ["Mountain"], unavailableOnArena: ["Mosswood Dreadknight"]);
        var illegal = Deck("Rotated Out", "R", ["Mountain"], illegalInFormat: ["Lightning Bolt"]);

        var criteria = new DeckFilterCriteria { IncludeUnplayable = true };
        var result = DeckFilter.Apply([unavailable, illegal], criteria, EmptyWallet);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Apply_Should_StillNarrowUnplayableDecks_When_OtherFiltersAreSet()
    {
        // Keeping unplayable decks must not exempt them from the user's actual filters.
        var boros = Deck("Boros", "WR", ["Plains"], unavailableOnArena: ["Some Card"]);
        var mono = Deck("Mono Blue", "U", ["Island"], unavailableOnArena: ["Some Card"]);

        var criteria = new DeckFilterCriteria
        {
            IncludeUnplayable = true,
            Colors = new HashSet<char> { 'W' }
        };

        var result = DeckFilter.Apply([boros, mono], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Boros", result[0].Deck.Name);
    }

    [Fact]
    public void ActiveCount_Should_CountEachControlOnce()
    {
        // Two colours are one filter, and so is a budget set on two rarities.
        var criteria = new DeckFilterCriteria
        {
            Colors = new HashSet<char> { 'W', 'B' },
            OnlyCraftable = true,
            RarityBudget = new RarityBudget(null, null, 4, 0),
            NameSearch = "lifegain",
        };

        Assert.Equal(4, criteria.ActiveCount);
        Assert.Equal(0, new DeckFilterCriteria { Source = DeckSourceFilter.User, IncludeUnplayable = true }.ActiveCount);
    }

    [Fact]
    public void IsEmpty_Should_BeTrue_When_OnlyIncludeUnplayableIsSet()
    {
        // Which list is being shown is not a filter the user picked, so it must not
        // offer them a "clear filters" link.
        Assert.True(new DeckFilterCriteria { IncludeUnplayable = true }.IsEmpty);
    }

    [Fact]
    public void IsEmpty_Should_BeTrue_When_OnlySourceIsSet()
    {
        Assert.True(new DeckFilterCriteria { Source = DeckSourceFilter.User }.IsEmpty);
    }

    [Fact]
    public void IsUserDeck_Should_FollowTheSourceIdPrefix()
    {
        Assert.True(Deck("Mine", "R", ["Mountain"], userDeck: true).Deck.IsUserDeck);
        Assert.False(Deck("Theirs", "R", ["Mountain"]).Deck.IsUserDeck);
    }

    [Fact]
    public void Apply_Should_ReturnOnlyUserDecks_When_SourceIsUser()
    {
        var mine = Deck("Mine", "R", ["Mountain"], userDeck: true);
        var theirs = Deck("Theirs", "R", ["Mountain"]);

        var criteria = new DeckFilterCriteria { Source = DeckSourceFilter.User };
        var result = DeckFilter.Apply([mine, theirs], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Mine", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_ReturnOnlyFetchedDecks_When_SourceIsFetched()
    {
        var mine = Deck("Mine", "R", ["Mountain"], userDeck: true);
        var theirs = Deck("Theirs", "R", ["Mountain"]);

        var criteria = new DeckFilterCriteria { Source = DeckSourceFilter.Fetched };
        var result = DeckFilter.Apply([mine, theirs], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Theirs", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_ReturnBothSources_When_SourceIsAny()
    {
        var mine = Deck("Mine", "R", ["Mountain"], userDeck: true);
        var theirs = Deck("Theirs", "R", ["Mountain"]);

        var result = DeckFilter.Apply([mine, theirs], new DeckFilterCriteria(), EmptyWallet);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Apply_Should_KeepUnplayableUserDeck_When_ShowingTheUserDecksTab()
    {
        // The exact combination the User decks tab uses, and the whole point of #11:
        // an imported deck naming a card that is not on Arena must still be listed.
        var brokenUserDeck = Deck(
            "Golgari pest", "BG", ["Swamp"], userDeck: true, unavailableOnArena: ["Mosswood Dreadknight"]);
        var fetched = Deck("Theirs", "R", ["Mountain"]);

        var criteria = new DeckFilterCriteria
        {
            Source = DeckSourceFilter.User,
            IncludeUnplayable = true
        };

        var result = DeckFilter.Apply([brokenUserDeck, fetched], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Golgari pest", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_HideUserDecks_When_ShowingTheSuggestionsTab()
    {
        var mine = Deck("Mine", "R", ["Mountain"], userDeck: true);
        var theirs = Deck("Theirs", "R", ["Mountain"]);

        var criteria = new DeckFilterCriteria
        {
            Source = DeckSourceFilter.Fetched,
            IncludeUnplayable = false
        };

        var result = DeckFilter.Apply([mine, theirs], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Theirs", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_KeepOnlyPinnedDecks_When_OnlyPinnedIsSet()
    {
        var tracked = Deck("Tracked", "R", ["Mountain"]);
        var other = Deck("Other", "R", ["Mountain"]);
        var criteria = new DeckFilterCriteria
        {
            OnlyPinned = true,
            PinnedSourceIds = new HashSet<string> { tracked.Deck.SourceId }
        };

        var result = DeckFilter.Apply([tracked, other], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Tracked", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_ReturnEmpty_When_OnlyPinnedIsSetAndNothingIsPinned()
    {
        var deck = Deck("Anything", "R", ["Mountain"]);

        var result = DeckFilter.Apply([deck], new DeckFilterCriteria { OnlyPinned = true }, EmptyWallet);

        Assert.Empty(result);
    }

    [Fact]
    public void Apply_Should_DropDecks_When_TheyExceedTheRareCap()
    {
        var cheap = Deck("Cheap", "R", ["Mountain"], rares: 2);
        var expensive = Deck("Expensive", "R", ["Mountain"], rares: 6);
        var criteria = new DeckFilterCriteria { RarityBudget = new RarityBudget(null, null, Rares: 3, null) };

        var result = DeckFilter.Apply([cheap, expensive], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Cheap", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_TreatAZeroCapAsARealConstraint()
    {
        // "No mythics needed" is the headline case, and it is expressed as a cap of zero.
        var noMythics = Deck("No Mythics", "R", ["Mountain"], rares: 8);
        var oneMythic = Deck("One Mythic", "R", ["Mountain"], mythics: 1);
        var criteria = new DeckFilterCriteria { RarityBudget = new RarityBudget(null, null, null, Mythics: 0) };

        var result = DeckFilter.Apply([noMythics, oneMythic], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("No Mythics", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_IgnoreRaritiesWithNoCap()
    {
        var commonHeavy = Deck("Pauper-ish", "G", ["Forest"], commons: 40, rares: 1);
        var criteria = new DeckFilterCriteria { RarityBudget = new RarityBudget(null, null, Rares: 2, null) };

        var result = DeckFilter.Apply([commonHeavy], criteria, EmptyWallet);

        Assert.Single(result);
    }

    [Fact]
    public void Apply_Should_RequireEveryRarityCap_When_SeveralAreSet()
    {
        var passesBoth = Deck("Both", "R", ["Mountain"], rares: 2, mythics: 0);
        var failsMythics = Deck("Mythic Heavy", "R", ["Mountain"], rares: 2, mythics: 3);
        var criteria = new DeckFilterCriteria { RarityBudget = new RarityBudget(null, null, Rares: 4, Mythics: 1) };

        var result = DeckFilter.Apply([passesBoth, failsMythics], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Both", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_CombineRarityCaps_WithTheTotalBudget()
    {
        var underTotalOverRares = Deck("Rare Heavy", "R", ["Mountain"], rares: 5);
        var underRaresOverTotal = Deck("Common Heavy", "R", ["Mountain"], commons: 30, rares: 1);
        var passesBoth = Deck("Cheap", "R", ["Mountain"], commons: 4, rares: 1);

        var criteria = new DeckFilterCriteria
        {
            MaxWildcards = 10,
            RarityBudget = new RarityBudget(null, null, Rares: 2, null)
        };

        var result = DeckFilter.Apply([underTotalOverRares, underRaresOverTotal, passesBoth], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Cheap", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_KeepEverything_When_BudgetIsUnlimited()
    {
        var decks = new[]
        {
            Deck("A", "R", ["Mountain"], commons: 20, uncommons: 12, rares: 16, mythics: 8),
            Deck("B", "U", ["Island"])
        };

        var result = DeckFilter.Apply(decks, new DeckFilterCriteria { RarityBudget = RarityBudget.Unlimited }, EmptyWallet);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Apply_Should_KeepDecksAtOrAboveTheOwnedThreshold()
    {
        // The default single card is 4 copies, so owned copies map to quarters.
        var mostlyOwned = Deck("Mostly Owned", "R", ["Mountain"], ownedCopies: 3);
        var barelyOwned = Deck("Barely Owned", "R", ["Mountain"], ownedCopies: 1);
        var criteria = new DeckFilterCriteria { MinOwnedFraction = 0.5 };

        var result = DeckFilter.Apply([mostlyOwned, barelyOwned], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Mostly Owned", result[0].Deck.Name);
    }

    [Fact]
    public void Apply_Should_KeepDeckExactlyAtTheOwnedThreshold()
    {
        var threeQuarters = Deck("Three Quarters", "R", ["Mountain"], ownedCopies: 3);

        var result = DeckFilter.Apply([threeQuarters], new DeckFilterCriteria { MinOwnedFraction = 0.75 }, EmptyWallet);

        Assert.Single(result);
    }

    [Fact]
    public void Apply_Should_DropDeckWithNoCopies_When_AnOwnedThresholdIsSet()
    {
        // An empty list is not "nearly finished", however the fraction is defined.
        var empty = Deck("Empty", "R", cards: []);

        var result = DeckFilter.Apply([empty], new DeckFilterCriteria { MinOwnedFraction = 0.05 }, EmptyWallet);

        Assert.Equal(0, empty.TotalCopies);
        Assert.Empty(result);
    }

    [Fact]
    public void Apply_Should_CombineOwnedThreshold_WithColorAndRarityFilters()
    {
        var match = Deck("Match", "WR", ["Plains"], rares: 1, ownedCopies: 4);
        var wrongColor = Deck("Wrong Colour", "U", ["Island"], rares: 1, ownedCopies: 4);
        var tooManyRares = Deck("Too Many Rares", "WR", ["Plains"], rares: 5, ownedCopies: 4);
        var notOwnedEnough = Deck("Not Owned Enough", "WR", ["Plains"], rares: 1, ownedCopies: 1);

        var criteria = new DeckFilterCriteria
        {
            Colors = new HashSet<char> { 'W' },
            RarityBudget = new RarityBudget(null, null, Rares: 2, null),
            MinOwnedFraction = 0.5
        };

        var result = DeckFilter.Apply([match, wrongColor, tooManyRares, notOwnedEnough], criteria, EmptyWallet);

        Assert.Single(result);
        Assert.Equal("Match", result[0].Deck.Name);
    }

    [Fact]
    public void IsEmpty_Should_BeFalse_When_ARarityCapIsSet()
    {
        Assert.False(new DeckFilterCriteria { RarityBudget = new RarityBudget(null, null, Rares: 3, null) }.IsEmpty);
    }

    [Fact]
    public void IsEmpty_Should_BeFalse_When_AZeroRarityCapIsSet()
    {
        Assert.False(new DeckFilterCriteria { RarityBudget = new RarityBudget(null, null, null, Mythics: 0) }.IsEmpty);
    }

    [Fact]
    public void IsEmpty_Should_BeFalse_When_AnOwnedThresholdIsSet()
    {
        Assert.False(new DeckFilterCriteria { MinOwnedFraction = 0.5 }.IsEmpty);
    }

    [Fact]
    public void IsEmpty_Should_BeTrue_When_NeitherNewFilterIsSet()
    {
        var criteria = new DeckFilterCriteria();

        Assert.True(criteria.RarityBudget.IsUnlimited);
        Assert.Null(criteria.MinOwnedFraction);
        Assert.True(criteria.IsEmpty);
    }

    private static DeckAnalysisResult Deck(
        string name,
        string colors,
        IReadOnlyList<string> cards,
        IReadOnlyList<string>? sideboard = null,
        int commons = 0,
        int uncommons = 0,
        int rares = 0,
        int mythics = 0,
        int ownedCopies = 0,
        IReadOnlyList<string>? unavailableOnArena = null,
        IReadOnlyList<string>? illegalInFormat = null,
        bool userDeck = false)
    {
        var refs = cards.Select(c => new DeckCardRef(c, 4, DeckBoard.Main))
            .Concat((sideboard ?? []).Select(c => new DeckCardRef(c, 2, DeckBoard.Sideboard)))
            .ToList();

        var candidate = new CandidateDeck(
            SourceId: userDeck ? $"{CandidateDeck.ManualSourcePrefix}{name}" : $"archidekt:{name}",
            Name: name,
            Url: "",
            FormatKey: Formats.Standard.Key,
            Popularity: 0,
            Cards: refs,
            FetchedAt: DateTimeOffset.UtcNow);

        return new DeckAnalysisResult(
            Deck: candidate,
            Needed: new WildcardNeed(commons, uncommons, rares, mythics),
            OwnedCopies: ownedCopies,
            TotalCopies: refs.Sum(c => c.Quantity),
            Gaps: [],
            UnavailableOnArena: unavailableOnArena ?? [],
            IllegalInFormat: illegalInFormat ?? [],
            Colors: colors);
    }
}
