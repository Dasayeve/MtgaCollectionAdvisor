using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MtgaCollectionAdvisor.Core.Arena;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Export;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// Getting data out. What matters is the round trip: every export must read back in
/// through the parsers and importers the app already has, or the user cannot rely on it.
/// </summary>
public sealed class DataExportTests : IAsyncLifetime
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [100] = "Lightning Bolt",
        [101] = "Lightning Bolt",
        [200] = "Bonecrusher Giant // Stomp",
        [300] = "Island",
    };

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"advisor-export-{Guid.NewGuid():N}.db");
    private Database _database = null!;

    public async Task InitializeAsync()
    {
        _database = new Database(_databasePath);
        await SchemaMigrator.MigrateAsync(_database);
    }

    public Task DisposeAsync()
    {
        TestDatabaseFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public void Group_Should_SumPrintings_ByName()
    {
        var grouped = CollectionExportWriter.Group(Collection((100, 3), (101, 1), (300, 7)), Names);

        var bolt = Assert.Single(grouped, c => c.Name == "Lightning Bolt");
        Assert.Equal(4, bolt.Count);
        Assert.Equal([100, 101], bolt.ArenaIds);
    }

    [Fact]
    public void Group_Should_KeepUnknownIds_WithEmptyName()
    {
        var grouped = CollectionExportWriter.Group(Collection((300, 2), (999, 1), (998, 3)), Names);

        Assert.Equal(2, grouped.Count(c => c.Name == ""));
        Assert.DoesNotContain("999", CollectionExportWriter.WriteText(grouped));
    }

    [Fact]
    public void WriteText_Should_RoundTrip_ThroughCollectionListParser()
    {
        var grouped = CollectionExportWriter.Group(Collection((100, 3), (101, 1), (300, 7)), Names);

        var parsed = CollectionListParser.Parse(CollectionExportWriter.WriteText(grouped));

        Assert.Equal(2, parsed.Count);
        Assert.Equal(4, parsed["Lightning Bolt"]);
        Assert.Equal(7, parsed["Island"]);
    }

    [Fact]
    public void WriteText_Should_WriteFrontFaceOnly()
    {
        var text = CollectionExportWriter.WriteText(CollectionExportWriter.Group(Collection((200, 2)), Names));

        Assert.Equal("2 Bonecrusher Giant", text.Trim());
    }

    [Fact]
    public async Task WriteJson_Should_RoundTrip_ThroughTheImporter()
    {
        var original = Collection((100, 3), (101, 1), (200, 2), (999, 1));
        var json = CollectionExportWriter.WriteJson(CollectionExportWriter.Group(original, Names), WildcardInventory.Empty, DateTimeOffset.UtcNow);

        var store = new CollectionStore(_database);
        await new CollectionExporterJsonImporter(store).ImportAsync(json);
        var imported = (await store.LoadAsync()).OwnedByGrpId;

        // The importer puts a name's whole count on its first id; totals per name are what count.
        Assert.Equal(4, imported[100]);
        Assert.Equal(2, imported[200]);
        Assert.Equal(1, imported[999]);
        Assert.Equal(original.OwnedByGrpId.Values.Sum(), imported.Values.Sum());
    }

    [Fact]
    public void WriteJson_Should_WriteNull_When_WildcardsUnknown()
    {
        var json = CollectionExportWriter.WriteJson([], wildcards: null, DateTimeOffset.UtcNow);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, JsonDocument.Parse(json).RootElement.GetProperty("wildcards").ValueKind);
    }

    // #57: no row means never read, not zero; a saved inventory comes back as saved.
    [Fact]
    public async Task CollectionStore_Should_ReturnNullWildcards_When_NeverSaved()
    {
        var store = new CollectionStore(_database);
        Assert.Null((await store.LoadAsync()).Wildcards);

        await store.SaveWildcardsAsync(new WildcardInventory(3, 2, 1, 0));

        Assert.Equal(new WildcardInventory(3, 2, 1, 0), (await store.LoadAsync()).Wildcards);
    }

    [Fact]
    public void WriteJson_Should_IncludeWildcards()
    {
        var json = CollectionExportWriter.WriteJson([], new WildcardInventory(120, 64, 6, 11), DateTimeOffset.UtcNow);

        var wildcards = JsonDocument.Parse(json).RootElement.GetProperty("wildcards");
        Assert.Equal(120, wildcards.GetProperty("common").GetInt32());
        Assert.Equal(64, wildcards.GetProperty("uncommon").GetInt32());
        Assert.Equal(6, wildcards.GetProperty("rare").GetInt32());
        Assert.Equal(11, wildcards.GetProperty("mythic").GetInt32());
    }

    [Fact]
    public void WriteZip_Should_WriteOneParsableFilePerDeck()
    {
        var mono = UserDeck("Mono Red", Formats.Standard, ("Lightning Bolt", 4, DeckBoard.Main), ("Mountain", 20, DeckBoard.Main));
        var giant = UserDeck("Giants", Formats.Pioneer, ("Bonecrusher Giant // Stomp", 4, DeckBoard.Main), ("Negate", 2, DeckBoard.Sideboard));

        var files = ReadZip(UserDeckExportWriter.WriteZip([mono, giant]));

        Assert.Equal(["pioneer/Giants.txt", "standard/Mono Red.txt"], files.Keys.Order());
        Assert.Equal(mono.Cards, ArenaDeckListParser.Parse(files["standard/Mono Red.txt"]));

        var giantCards = ArenaDeckListParser.Parse(files["pioneer/Giants.txt"]);
        Assert.Contains(new DeckCardRef("Bonecrusher Giant", 4, DeckBoard.Main), giantCards);
        Assert.Contains(new DeckCardRef("Negate", 2, DeckBoard.Sideboard), giantCards);
    }

    [Fact]
    public void WriteZip_Should_DisambiguateDuplicateAndUnsafeNames()
    {
        var files = ReadZip(UserDeckExportWriter.WriteZip([
            UserDeck("Burn", Formats.Standard, ("Mountain", 20, DeckBoard.Main)),
            UserDeck("burn", Formats.Standard, ("Mountain", 20, DeckBoard.Main)),
            UserDeck("Izzet: Spells / Tempo?", Formats.Standard, ("Island", 20, DeckBoard.Main)),
            UserDeck("  ", Formats.Standard, ("Island", 20, DeckBoard.Main)),
        ]));

        Assert.Contains("standard/Burn.txt", files.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("standard/burn (2).txt", files.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("standard/Izzet_ Spells _ Tempo_.txt", files.Keys);
        Assert.Contains("standard/deck.txt", files.Keys);
    }

    [Fact]
    public async Task UserDecks_Should_ExportOnlyManualDecks()
    {
        var decks = new CuratedDeckStore(_database);
        await decks.AddDeckAsync(UserDeck("Mine", Formats.Standard, ("Mountain", 20, DeckBoard.Main)));
        await decks.AddDeckAsync(UserDeck("Also mine", Formats.Pioneer, ("Island", 20, DeckBoard.Main)));
        await decks.AddDeckAsync(UserDeck("Fetched", Formats.Standard, ("Forest", 20, DeckBoard.Main)) with { SourceId = "archidekt:1" });

        var service = new DataExportService(new CollectionStore(_database), new CardDatabaseStore(_database), decks, new ArenaDeckStore(_database));
        var file = await service.UserDecksAsync();

        Assert.Equal("application/zip", file.ContentType);
        Assert.Equal(["pioneer/Also mine.txt", "standard/Mine.txt"], ReadZip(file.Content).Keys.Order());
    }

    // Each Export menu entry is one kind: the hosts save whatever ExportAsync returns.
    [Theory]
    [InlineData(ExportKind.CollectionText)]
    [InlineData(ExportKind.CollectionJson)]
    [InlineData(ExportKind.UserDecks)]
    [InlineData(ExportKind.ArenaDecks)]
    [InlineData(ExportKind.AllArenaDecks)]
    public async Task ExportAsync_Should_ReturnTheSameFileAsTheNamedMethod(ExportKind kind)
    {
        var decks = new CuratedDeckStore(_database);
        await decks.AddDeckAsync(UserDeck("Mine", Formats.Standard, ("Mountain", 20, DeckBoard.Main)));
        var arenaDecks = new ArenaDeckStore(_database);
        await arenaDecks.ReplaceAsync(
            [new ArenaDeck("a", "Mine", "Standard", false, [], [], [new ArenaCard(300, 20)], []),
             new ArenaDeck("b", "Precon", "Standard", true, [], [], [new ArenaCard(300, 20)], [])],
            DateTimeOffset.UtcNow);
        var service = new DataExportService(new CollectionStore(_database), new CardDatabaseStore(_database), decks, arenaDecks);

        var expected = kind switch
        {
            ExportKind.CollectionText => await service.CollectionTextAsync(),
            ExportKind.CollectionJson => await service.CollectionJsonAsync(),
            ExportKind.UserDecks => await service.UserDecksAsync(),
            ExportKind.ArenaDecks => await service.ArenaDecksAsync(includeWizards: false),
            _ => await service.ArenaDecksAsync(includeWizards: true),
        };
        var file = await service.ExportAsync(kind);

        Assert.NotNull(expected);
        Assert.NotNull(file);
        Assert.Equal(expected.FileName, file.FileName);
        Assert.Equal(expected.ContentType, file.ContentType);
        // The JSON carries the moment it was written, and a zip stamps each entry with the time:
        // compare what they hold, not their bytes.
        if (file.ContentType == "application/zip") Assert.Equal(ReadZip(expected.Content), ReadZip(file.Content));
        else if (kind != ExportKind.CollectionJson) Assert.Equal(expected.Content, file.Content);
    }

    [Fact]
    public async Task ExportAsync_Should_Throw_When_KindIsUnknown()
    {
        var service = new DataExportService(
            new CollectionStore(_database), new CardDatabaseStore(_database), new CuratedDeckStore(_database), new ArenaDeckStore(_database));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExportAsync((ExportKind)99));
    }

    private static CollectionSnapshot Collection(params (int GrpId, int Qty)[] owned) =>
        new(owned.ToDictionary(o => o.GrpId, o => o.Qty), WildcardInventory.Empty, DateTimeOffset.UtcNow);

    private static CandidateDeck UserDeck(string name, FormatDefinition format, params (string Name, int Qty, DeckBoard Board)[] cards) => new(
        SourceId: $"{CandidateDeck.ManualSourcePrefix}{Guid.NewGuid():N}",
        Name: name,
        Url: "",
        FormatKey: format.Key,
        Popularity: 0,
        Cards: cards.Select(c => new DeckCardRef(c.Name, c.Qty, c.Board)).ToList(),
        FetchedAt: DateTimeOffset.UtcNow);

    private static Dictionary<string, string> ReadZip(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(
            e => e.FullName,
            e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
    }
}
