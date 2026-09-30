using Microsoft.Data.Sqlite;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Models;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// #101: reading MTG Arena's card database, over a small file in its layout (the columns read,
/// plus one it has that the reader ignores).
/// </summary>
public sealed class ArenaCardDatabaseTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("arena-cards-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReadAsync_returns_primary_non_token_cards()
    {
        var path = ArenaCardFiles.Create(Path.Combine(_directory, "Raw_CardDatabase_a.mtga"), ArenaCardFiles.SampleRows);

        var cards = await ArenaCardDatabase.ReadAsync(path);

        Assert.Equal([106225, 106227, 106300, 106301, 106302], cards.Select(c => c.GrpId).Order());
        var emrakul = cards.Single(c => c.GrpId == 106225);
        Assert.Equal(new ArenaDatabaseCard(106225, "Emrakul, the Exigent Doom", "FRA", "1", CardRarity.Mythic, "{10}", "",
            IsNonBasicLand: false, IsDigitalOnly: false, IsRebalanced: false), emrakul);
    }

    [Fact]
    public async Task ReadAsync_names_two_face_cards_front_slash_back()
    {
        var path = ArenaCardFiles.Create(Path.Combine(_directory, "Raw_CardDatabase_a.mtga"), ArenaCardFiles.SampleRows);

        var angel = (await ArenaCardDatabase.ReadAsync(path)).Single(c => c.GrpId == 106227);

        Assert.Equal("Blossom-Blessed Angel // Seed Suture", angel.Name);
        Assert.Equal("{3}{W}", angel.ManaCost);
        Assert.Equal("WG", angel.Colors);
    }

    [Fact]
    public async Task ReadAsync_reads_land_kind_digital_and_rebalanced_flags()
    {
        var path = ArenaCardFiles.Create(Path.Combine(_directory, "Raw_CardDatabase_a.mtga"), ArenaCardFiles.SampleRows);

        var cards = (await ArenaCardDatabase.ReadAsync(path)).ToDictionary(c => c.GrpId);

        Assert.True(cards[106300].IsNonBasicLand);
        Assert.Equal(CardRarity.Basic, cards[106301].Rarity);
        Assert.False(cards[106301].IsNonBasicLand);
        Assert.True(cards[106302].IsDigitalOnly);
    }

    [Fact]
    public async Task ReadAsync_marks_an_Alchemy_card_rebalanced_by_its_name()
    {
        var path = ArenaCardFiles.Create(Path.Combine(_directory, "Raw_CardDatabase_a.mtga"),
            [new(90, "<sprite=\"SpriteSheet_MiscIcons\" name=\"arena_a\">Demilich", "AFR", "53", 5, "oUoUoUoU", "2", "2")]);

        var demilich = Assert.Single(await ArenaCardDatabase.ReadAsync(path));

        Assert.Equal("A-Demilich", demilich.Name);
        Assert.True(demilich.IsRebalanced);
    }

    [Fact]
    public async Task ReadAsync_throws_on_an_unknown_layout()
    {
        var path = ArenaCardFiles.Create(Path.Combine(_directory, "Raw_CardDatabase_a.mtga"), ArenaCardFiles.SampleRows,
            withoutColumn: "CollectorNumber");

        var error = await Assert.ThrowsAsync<ArenaCardDatabaseException>(() => ArenaCardDatabase.ReadAsync(path));

        Assert.Contains("CollectorNumber", error.Message);
    }

    [Fact]
    public async Task ReadAsync_throws_on_a_file_that_is_not_a_database()
    {
        var path = Path.Combine(_directory, "Raw_CardDatabase_a.mtga");
        await File.WriteAllTextAsync(path, "not a database");

        await Assert.ThrowsAsync<ArenaCardDatabaseException>(() => ArenaCardDatabase.ReadAsync(path));
    }

    [Fact]
    public async Task ReadAsync_leaves_the_file_unlocked()
    {
        var path = ArenaCardFiles.Create(Path.Combine(_directory, "Raw_CardDatabase_a.mtga"), ArenaCardFiles.SampleRows);

        await ArenaCardDatabase.ReadAsync(path);

        // Arena replaces the file on its own update: no handle may outlive the read.
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(path);
    }

    [Fact]
    public void NewestIn_picks_the_latest_card_database_and_ignores_other_files()
    {
        var old = Path.Combine(_directory, "Raw_CardDatabase_old.mtga");
        var current = Path.Combine(_directory, "Raw_CardDatabase_new.mtga");
        var art = Path.Combine(_directory, "Raw_ArtCropDatabase_newest.mtga");
        foreach (var file in (string[])[old, current, art]) File.WriteAllText(file, "");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(art, DateTime.UtcNow);

        var newest = ArenaCardDatabase.NewestIn(_directory);

        Assert.Equal(new ArenaCardFile(current, "Raw_CardDatabase_new.mtga"), newest);
        Assert.Equal(_directory, newest!.Folder);
    }

    [Fact]
    public void NewestIn_is_null_for_a_missing_or_empty_folder()
    {
        Assert.Null(ArenaCardDatabase.NewestIn(_directory));
        Assert.Null(ArenaCardDatabase.NewestIn(Path.Combine(_directory, "missing")));
    }
}

/// <summary>A card row of MTG Arena's <c>Cards</c> table, as the tests build it.</summary>
internal sealed record ArenaRow(
    int GrpId, string Name, string Set, string Number, int Rarity, string ManaText, string Colors, string Types,
    string Supertypes = "", string LinkedFaces = "", bool IsPrimary = true, bool IsToken = false,
    bool IsDigitalOnly = false, bool IsRebalanced = false);

/// <summary>Small files in the layout of MTG Arena's card database (data version 2026.63), for tests.</summary>
internal static class ArenaCardFiles
{
    public static readonly ArenaRow[] SampleRows =
    [
        new(106225, "Emrakul, the Exigent Doom", "FRA", "1", 5, "o10", "", "2", Supertypes: "2"),
        new(106227, "<nobr>Blossom-Blessed</nobr> Angel", "FRA", "3", 2, "o3oW", "1", "2", LinkedFaces: "106228"),
        new(106228, "Seed Suture", "FRA", "3", 2, "o(G/W)", "1,5", "10", LinkedFaces: "106227", IsPrimary: false),
        new(106299, "Spirit", "TFRA", "4", 0, "", "1", "2", IsToken: true),
        new(106300, "Campus Gate", "FRA", "260", 3, "", "", "5"),
        new(106301, "Plains", "FRA", "262", 1, "", "", "5", Supertypes: "1"),
        new(106302, "Arena Wonder", "YFRA", "7", 4, "o2oU", "2", "2", IsDigitalOnly: true),
        new(106303, "Emrakul, the Exigent Doom", "FRA", "1", 5, "o10", "", "2", IsPrimary: false), // a style, not a card
    ];

    /// <summary>Writes the file unpooled, so nothing keeps it open once written.</summary>
    public static string Create(string path, IEnumerable<ArenaRow> rows, string? withoutColumn = null)
    {
        var columns = new[]
        {
            "GrpId INTEGER", "TitleId INTEGER", "ExpansionCode TEXT", "CollectorNumber TEXT", "Rarity INTEGER",
            "OldSchoolManaText TEXT", "Colors TEXT", "Types TEXT", "Supertypes TEXT", "LinkedFaceGrpIds TEXT",
            "IsToken INTEGER", "IsPrimaryCard INTEGER", "IsDigitalOnly INTEGER", "IsRebalanced INTEGER", "ArtistCredit TEXT",
        }.Where(c => withoutColumn is null || !c.StartsWith(withoutColumn + " ", StringComparison.Ordinal));

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, $"CREATE TABLE Cards ({string.Join(", ", columns)})");
        Execute(connection, "CREATE TABLE Localizations_enUS (LocId INTEGER, Formatted INTEGER, Loc TEXT)");

        var hasNumber = withoutColumn != "CollectorNumber";
        foreach (var row in rows)
        {
            var titleId = row.GrpId + 1_000_000;
            using var insert = connection.CreateCommand();
            insert.CommandText = hasNumber
                ? """
                  INSERT INTO Cards (GrpId, TitleId, ExpansionCode, CollectorNumber, Rarity, OldSchoolManaText, Colors, Types,
                                     Supertypes, LinkedFaceGrpIds, IsToken, IsPrimaryCard, IsDigitalOnly, IsRebalanced, ArtistCredit)
                  VALUES ($grpId, $titleId, $set, $number, $rarity, $mana, $colors, $types, $supertypes, $faces,
                          $token, $primary, $digital, $rebalanced, 'Someone')
                  """
                : """
                  INSERT INTO Cards (GrpId, TitleId, ExpansionCode, Rarity, OldSchoolManaText, Colors, Types,
                                     Supertypes, LinkedFaceGrpIds, IsToken, IsPrimaryCard, IsDigitalOnly, IsRebalanced, ArtistCredit)
                  VALUES ($grpId, $titleId, $set, $rarity, $mana, $colors, $types, $supertypes, $faces,
                          $token, $primary, $digital, $rebalanced, 'Someone')
                  """;
            insert.Parameters.AddWithValue("$grpId", row.GrpId);
            insert.Parameters.AddWithValue("$titleId", titleId);
            insert.Parameters.AddWithValue("$set", row.Set);
            if (hasNumber) insert.Parameters.AddWithValue("$number", row.Number);
            insert.Parameters.AddWithValue("$rarity", row.Rarity);
            insert.Parameters.AddWithValue("$mana", row.ManaText);
            insert.Parameters.AddWithValue("$colors", row.Colors);
            insert.Parameters.AddWithValue("$types", row.Types);
            insert.Parameters.AddWithValue("$supertypes", row.Supertypes);
            insert.Parameters.AddWithValue("$faces", row.LinkedFaces);
            insert.Parameters.AddWithValue("$token", row.IsToken ? 1 : 0);
            insert.Parameters.AddWithValue("$primary", row.IsPrimary ? 1 : 0);
            insert.Parameters.AddWithValue("$digital", row.IsDigitalOnly ? 1 : 0);
            insert.Parameters.AddWithValue("$rebalanced", row.IsRebalanced ? 1 : 0);
            insert.ExecuteNonQuery();

            // Arena keeps a plain name (Formatted 0) next to the displayed one (Formatted 1).
            using var name = connection.CreateCommand();
            name.CommandText = "INSERT INTO Localizations_enUS VALUES ($id, 0, 'plain'), ($id, 1, $name)";
            name.Parameters.AddWithValue("$id", titleId);
            name.Parameters.AddWithValue("$name", row.Name);
            name.ExecuteNonQuery();
        }
        return path;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
