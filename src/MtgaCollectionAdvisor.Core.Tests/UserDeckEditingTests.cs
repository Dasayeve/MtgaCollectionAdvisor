using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// Editing a deck the user owns. Runs against a throwaway SQLite file because what is
/// worth testing here is the write itself: that the source id survives, and with it the
/// pin whose baseline re-importing used to throw away.
/// </summary>
public sealed class UserDeckEditingTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"advisor-test-{Guid.NewGuid():N}.db");
    private CuratedDeckStore _decks = null!;
    private PinnedDeckStore _pins = null!;

    private const string DeckId = $"{CandidateDeck.ManualSourcePrefix}deck-under-test";

    public async Task InitializeAsync()
    {
        var database = new Database(_databasePath);
        await SchemaMigrator.MigrateAsync(database);
        _decks = new CuratedDeckStore(database);
        _pins = new PinnedDeckStore(database);

        await _decks.AddDeckAsync(Deck(DeckId, "Original name", Formats.Standard, [
            Card("Mountain", 20),
            Card("Lightning Bolt", 4)
        ]));
    }

    public Task DisposeAsync()
    {
        TestDatabaseFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Update_Should_ReplaceCards_When_DeckIsEdited()
    {
        await _decks.UpdateUserDeckAsync(Deck(DeckId, "Original name", Formats.Standard, [
            Card("Island", 24)
        ]));

        var stored = Assert.Single(await _decks.LoadAsync(Formats.Standard));
        var card = Assert.Single(stored.Cards);
        Assert.Equal("Island", card.Name);
        Assert.Equal(24, card.Quantity);
    }

    [Fact]
    public async Task Update_Should_KeepTheSourceId()
    {
        await _decks.UpdateUserDeckAsync(Deck(DeckId, "Renamed", Formats.Standard, [Card("Island", 24)]));

        var stored = Assert.Single(await _decks.LoadAsync(Formats.Standard));
        Assert.Equal(DeckId, stored.SourceId);
        Assert.Equal("Renamed", stored.Name);
    }

    [Fact]
    public async Task Update_Should_KeepThePin_When_DeckIsEdited()
    {
        // The regression this issue exists for: re-importing minted a new source id and
        // silently unpinned the deck, losing the progress baseline with it.
        await _pins.PinAsync(DeckId, Formats.Standard.Key, wildcardsNow: 17);

        await _decks.UpdateUserDeckAsync(Deck(DeckId, "Edited", Formats.Standard, [Card("Island", 24)]));

        var pins = await _pins.LoadAsync();
        Assert.True(pins.ContainsKey(DeckId));
    }

    [Fact]
    public async Task Update_Should_PreserveCreationDateAndUrl()
    {
        var original = Assert.Single(await _decks.LoadAsync(Formats.Standard));

        await _decks.UpdateUserDeckAsync(
            Deck(DeckId, "Edited", Formats.Standard, [Card("Island", 24)]) with
            {
                Url = original.Url,
                Popularity = original.Popularity,
                FetchedAt = original.FetchedAt
            });

        var stored = Assert.Single(await _decks.LoadAsync(Formats.Standard));
        Assert.Equal(original.FetchedAt, stored.FetchedAt);
        Assert.Equal(original.Url, stored.Url);
        Assert.Equal(original.Popularity, stored.Popularity);
    }

    [Fact]
    public async Task Update_Should_ChangeTheFormat_When_FormatIsEdited()
    {
        await _decks.UpdateUserDeckAsync(Deck(DeckId, "Edited", Formats.Pioneer, [Card("Island", 24)]));

        Assert.Empty(await _decks.LoadAsync(Formats.Standard));
        Assert.Single(await _decks.LoadAsync(Formats.Pioneer));
    }

    [Fact]
    public async Task Update_Should_Reject_When_DeckIsNotAUserDeck()
    {
        var fetched = Deck("archidekt:12345", "Someone else's", Formats.Standard, [Card("Island", 24)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _decks.UpdateUserDeckAsync(fetched));
    }

    [Fact]
    public async Task Update_Should_Throw_When_DeckNoLongerExists()
    {
        var missing = Deck($"{CandidateDeck.ManualSourcePrefix}gone", "Ghost", Formats.Standard, [Card("Island", 24)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _decks.UpdateUserDeckAsync(missing));
    }

    [Fact]
    public async Task Update_Should_CollapseDuplicateCardEntries()
    {
        await _decks.UpdateUserDeckAsync(Deck(DeckId, "Edited", Formats.Standard, [
            Card("Island", 4),
            Card("Island", 3)
        ]));

        var stored = Assert.Single(await _decks.LoadAsync(Formats.Standard));
        var card = Assert.Single(stored.Cards);
        Assert.Equal(7, card.Quantity);
    }

    [Fact]
    public async Task Update_Should_LeaveOtherDecksAlone()
    {
        var otherId = $"{CandidateDeck.ManualSourcePrefix}other";
        await _decks.AddDeckAsync(Deck(otherId, "Other", Formats.Standard, [Card("Forest", 18)]));

        await _decks.UpdateUserDeckAsync(Deck(DeckId, "Edited", Formats.Standard, [Card("Island", 24)]));

        var other = Assert.Single(await _decks.LoadAsync(Formats.Standard), d => d.SourceId == otherId);
        var card = Assert.Single(other.Cards);
        Assert.Equal("Forest", card.Name);
        Assert.Equal(18, card.Quantity);
    }

    [Fact]
    public async Task Rebase_Should_MoveTheBaseline()
    {
        await _pins.PinAsync(DeckId, Formats.Standard.Key, wildcardsNow: 17);

        await _pins.RebaseAsync(DeckId, wildcardsNow: 4);

        var pins = await _pins.LoadAsync();
        Assert.Equal(4, pins[DeckId].WildcardsWhenPinned);
    }

    [Fact]
    public async Task Rebase_Should_DoNothing_When_DeckIsNotPinned()
    {
        await _pins.RebaseAsync(DeckId, wildcardsNow: 4);

        Assert.Empty(await _pins.LoadAsync());
    }

    [Fact]
    public async Task Pin_Should_StillRefuseToOverwriteAnExistingBaseline()
    {
        // RebaseAsync exists precisely so PinAsync can keep refusing.
        await _pins.PinAsync(DeckId, Formats.Standard.Key, wildcardsNow: 17);
        await _pins.PinAsync(DeckId, Formats.Standard.Key, wildcardsNow: 99);

        var pins = await _pins.LoadAsync();
        Assert.Equal(17, pins[DeckId].WildcardsWhenPinned);
    }

    [Fact]
    public async Task CountUserDecks_Should_CountOnlyTheUsersDecks_PerFormat()
    {
        // An empty User decks tab names the formats the user's other decks are in (#76).
        await _decks.AddDeckAsync(Deck("manual:p1", "P1", Formats.Pioneer, [Card("Island", 60)]));
        await _decks.AddDeckAsync(Deck("manual:p2", "P2", Formats.Pioneer, [Card("Island", 60)]));
        await _decks.AddDeckAsync(Deck("archidekt:1", "Fetched", Formats.Pioneer, [Card("Island", 60)]));

        var counts = await _decks.CountUserDecksAsync();

        Assert.Equal(new Dictionary<string, int> { ["standard"] = 1, ["pioneer"] = 2 }, counts);
    }

    private static DeckCardRef Card(string name, int quantity) => new(name, quantity, DeckBoard.Main);

    private static CandidateDeck Deck(
        string sourceId, string name, FormatDefinition format, IReadOnlyList<DeckCardRef> cards) => new(
            SourceId: sourceId,
            Name: name,
            Url: "",
            FormatKey: format.Key,
            Popularity: 0,
            Cards: cards,
            FetchedAt: DateTimeOffset.UtcNow);
}
