using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// The promise these tests keep: whoever installs a new version keeps their data. Each test
/// works in its own temp folder, since a migration writes backups next to the database.
/// Fixtures/schema-v{N}.sql rebuilds a database as release N left it; add one per release.
/// </summary>
public sealed class SchemaMigratorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"advisor-migrate-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public SchemaMigratorTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "advisor.db");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.GetFiles(_directory)) TestDatabaseFiles.Release(file);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Migrations_Should_BeContiguousFromOne()
    {
        Assert.Equal(Enumerable.Range(1, Migrations.All.Count), Migrations.All.Select(m => m.Version));
        Assert.Equal(Migrations.All.Count, Migrations.Latest);
    }

    // A database in the wild may already have run any of these. Pin each migration here when it
    // is written; if one of these hashes stops matching, write a new migration instead.
    private static readonly Dictionary<int, string> PinnedHashes = new()
    {
        [1] = "CDFDBBCFB6F513B97556F6019CA939096D4E944643F4C0F8ADBA3CD0131814C8",
        [2] = "F5D3029F155A9FE97412FAC67B8E438D88ECADD8D24556BE96335A22FC0A67E2",
        [3] = "9295042A1BFB089A5A3C313F1A9D9623466CEBA52700FAE3506C30EA7A46BAFB",
        [4] = "E31607529E7C7F350E3675973FC828A8B0FE2FA9B735D3F8C80E4A575A6A52A4",
        [5] = "573DA4142B7AEED7977BA2D245B9FD10B2EDBD0DB6871FA60CCC4DAA8C26E050",
    };

    [Fact]
    public void ReleasedMigrations_Should_BeUnchanged()
    {
        foreach (var migration in Migrations.All)
        {
            Assert.True(PinnedHashes.ContainsKey(migration.Version),
                $"Migration {migration.Version} has no pinned hash; add {Hash(migration.Sql)}.");
            Assert.True(PinnedHashes[migration.Version] == Hash(migration.Sql),
                $"Migration {migration.Version} ({migration.Name}) changed: add a new migration instead. Hash now {Hash(migration.Sql)}.");
        }
    }

    [Fact]
    public async Task MigrateAsync_Should_CreateLatestSchema_When_FileIsNew()
    {
        var result = await SchemaMigrator.MigrateAsync(new Database(_databasePath));

        Assert.Equal(new MigrationResult(0, Migrations.Latest, null), result);
        Assert.Equal(Migrations.Latest, await ScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM sqlite_master WHERE name = 'arena_decks'"));
        Assert.Empty(Backups());
    }

    [Theory]
    [InlineData("schema-v0.sql")]
    [InlineData("schema-v2.sql")]
    public async Task MigrateAsync_Should_KeepUserData_When_Upgrading(string fixture)
    {
        await CreateFromFixtureAsync(fixture);

        await SchemaMigrator.MigrateAsync(new Database(_databasePath));

        Assert.Equal(["90001:4", "90002:2", "90003:1"],
            await ColumnAsync("SELECT grp_id || ':' || quantity FROM collection_cards ORDER BY grp_id"));
        Assert.Equal(["31/22/7/3"],
            await ColumnAsync("SELECT commons || '/' || uncommons || '/' || rares || '/' || mythics FROM wildcard_inventory"));
        Assert.Equal(["My Rakdos Midrange|pioneer"],
            await ColumnAsync("SELECT name || '|' || format_key FROM decks WHERE source_id = 'manual:my-rakdos'"));
        Assert.Equal(["Lightning Strike|sideboard|2", "Sheoldred, the Apocalypse|main|3"],
            await ColumnAsync("""
                SELECT card_name || '|' || board || '|' || quantity FROM deck_cards
                WHERE source_id = 'manual:my-rakdos' ORDER BY card_name
                """));
        Assert.Equal(["archidekt:123456|5"],
            await ColumnAsync("SELECT source_id || '|' || wildcards_when_pinned FROM pinned_decks"));
        Assert.Equal(["Izzet Prowess", "?=?Loc/Decks/Precon/Red"],
            await ColumnAsync("SELECT name FROM arena_decks ORDER BY deck_id"));
        Assert.Equal(["{\"MainDeck\":[{\"cardId\":90001,\"quantity\":4}]}"],
            await ColumnAsync("SELECT cards_json FROM arena_decks WHERE deck_id = 'a1b2c3'"));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM creator_videos"));
    }

    [Fact]
    public async Task MigrateAsync_Should_DropCreatorFeedState_When_UpgradingPreVersioningDatabase()
    {
        await CreateFromFixtureAsync("schema-v0.sql");

        await SchemaMigrator.MigrateAsync(new Database(_databasePath));

        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM sqlite_master WHERE name = 'creator_feed_state'"));
    }

    [Theory]
    [InlineData("schema-v0.sql")]
    [InlineData("schema-v2.sql")]
    public async Task MigrateAsync_Should_ProduceSameSchema_As_FreshDatabase(string fixture)
    {
        var freshPath = Path.Combine(_directory, "fresh.db");
        await SchemaMigrator.MigrateAsync(new Database(freshPath));
        await CreateFromFixtureAsync(fixture);

        await SchemaMigrator.MigrateAsync(new Database(_databasePath));

        Assert.Equal(await SchemaShapeAsync(freshPath), await SchemaShapeAsync(_databasePath));
    }

    [Fact]
    public async Task MigrateAsync_Should_WriteReadableBackup_When_MigrationsArePending()
    {
        await CreateFromFixtureAsync("schema-v0.sql");

        var result = await SchemaMigrator.MigrateAsync(new Database(_databasePath));

        Assert.Equal(_databasePath + ".backup-v0", result.BackupPath);
        Assert.Equal(0, await ScalarAsync<long>("PRAGMA user_version", result.BackupPath));
        Assert.Equal(3, await ScalarAsync<long>("SELECT count(*) FROM collection_cards", result.BackupPath));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM creator_feed_state", result.BackupPath));
    }

    [Fact]
    public async Task MigrateAsync_Should_DoNothing_When_AlreadyUpToDate()
    {
        await CreateFromFixtureAsync("schema-v0.sql");
        await SchemaMigrator.MigrateAsync(new Database(_databasePath));
        var backupsAfterUpgrade = Backups();

        var result = await SchemaMigrator.MigrateAsync(new Database(_databasePath));

        Assert.Equal(new MigrationResult(Migrations.Latest, Migrations.Latest, null), result);
        Assert.Equal(backupsAfterUpgrade, Backups());
        Assert.Equal(Migrations.Latest, await ScalarAsync<long>("PRAGMA user_version"));
    }

    [Fact]
    public async Task MigrateAsync_Should_RollBack_When_AMigrationFails()
    {
        await CreateFromFixtureAsync("schema-v0.sql");
        Migration[] migrations =
        [
            .. Migrations.All,
            new(Migrations.Latest + 1, "Fails halfway",
                "CREATE TABLE half_done (x INTEGER); SELECT * FROM no_such_table;"),
        ];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SchemaMigrator.MigrateAsync(new Database(_databasePath), migrations));

        Assert.Contains($"still at schema {Migrations.Latest}", error.Message);
        Assert.Equal(Migrations.Latest, await ScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM sqlite_master WHERE name = 'half_done'"));
        Assert.Equal(3, await ScalarAsync<long>("SELECT count(*) FROM collection_cards"));
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM deck_cards WHERE source_id = 'manual:my-rakdos'"));
    }

    [Fact]
    public async Task MigrateAsync_Should_RollBack_When_ForeignKeysAreBroken()
    {
        await SchemaMigrator.MigrateAsync(new Database(_databasePath));
        Migration[] migrations =
        [
            .. Migrations.All,
            new(Migrations.Latest + 1, "Orphans a deck card",
                "INSERT INTO deck_cards VALUES ('archidekt:no-such-deck', 'Lightning Strike', 'main', 1);"),
        ];

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SchemaMigrator.MigrateAsync(new Database(_databasePath), migrations));

        Assert.Equal(Migrations.Latest, await ScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM deck_cards"));
    }

    [Fact]
    public async Task MigrateAsync_Should_RefuseAndNotTouchFile_When_DatabaseIsNewer()
    {
        await SchemaMigrator.MigrateAsync(new Database(_databasePath));
        await ExecuteAsync($"PRAGMA user_version = {Migrations.Latest + 1}");
        TestDatabaseFiles.Release(_databasePath);
        var before = FileHash(_databasePath);

        var error = await Assert.ThrowsAsync<SchemaTooNewException>(
            () => SchemaMigrator.MigrateAsync(new Database(_databasePath)));

        Assert.Equal(Migrations.Latest + 1, error.DatabaseVersion);
        Assert.Equal(Migrations.Latest, error.LatestKnown);
        TestDatabaseFiles.Release(_databasePath);
        Assert.Equal(before, FileHash(_databasePath));
        Assert.Empty(Backups());
    }

    [Fact]
    public async Task MigrateAsync_Should_KeepThreeNewestBackups()
    {
        await CreateFromFixtureAsync("schema-v0.sql");
        for (var i = 0; i < 4; i++)
        {
            var old = $"{_databasePath}.backup-v9{i}";
            await File.WriteAllTextAsync(old, "an older backup");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10 + i));
        }

        await SchemaMigrator.MigrateAsync(new Database(_databasePath));

        Assert.Equal(["advisor.db.backup-v0", "advisor.db.backup-v92", "advisor.db.backup-v93"], Backups());
    }

    private async Task CreateFromFixtureAsync(string fixture)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        await using (var connection = await new Database(_databasePath).OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        TestDatabaseFiles.Release(_databasePath);
    }

    private string[] Backups() => Directory.GetFiles(_directory, "advisor.db.backup-v*")
        .Select(Path.GetFileName)
        .Order(StringComparer.Ordinal)
        .ToArray()!;

    private async Task<T> ScalarAsync<T>(string sql, string? path = null)
    {
        await using var connection = Open(path ?? _databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    private async Task<string[]> ColumnAsync(string sql)
    {
        await using var connection = Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return [.. values];
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // Columns, indexes and foreign keys of every table. Not sqlite_master.sql: that keeps each
    // CREATE statement's own comments and spacing, which say nothing about the schema.
    private static async Task<string> SchemaShapeAsync(string path)
    {
        await using var connection = Open(path);
        var shape = new StringBuilder();
        foreach (var table in await ListAsync(connection,
                     "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"))
        {
            shape.AppendLine($"table {table}");
            foreach (var column in await ListAsync(connection,
                         $"SELECT name || ' ' || type || ' notnull=' || \"notnull\" || ' default=' || ifnull(dflt_value, '-') || ' pk=' || pk FROM pragma_table_info('{table}') ORDER BY cid"))
                shape.AppendLine($"  column {column}");
            foreach (var index in await ListAsync(connection,
                         $"SELECT name || ' unique=' || \"unique\" || ' origin=' || origin FROM pragma_index_list('{table}') ORDER BY name"))
            {
                var indexName = index.Split(' ')[0];
                var columns = await ListAsync(connection, $"SELECT ifnull(name, '<expr>') FROM pragma_index_xinfo('{indexName}') WHERE key = 1 ORDER BY seqno");
                shape.AppendLine($"  index {index} ({string.Join(", ", columns)})");
            }
            foreach (var key in await ListAsync(connection,
                         $"SELECT \"from\" || ' -> ' || \"table\" || '.' || \"to\" || ' on_delete=' || on_delete FROM pragma_foreign_key_list('{table}') ORDER BY id"))
                shape.AppendLine($"  foreign key {key}");
        }
        return shape.ToString();
    }

    private static async Task<List<string>> ListAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    // Unpooled, so checking a file never keeps it open for the next step of the test.
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    // Line endings normalised: the same migration checked out on Windows or elsewhere is the same migration.
    private static string Hash(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
