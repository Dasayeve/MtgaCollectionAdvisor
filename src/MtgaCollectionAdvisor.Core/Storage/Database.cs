using Microsoft.Data.Sqlite;

namespace MtgaCollectionAdvisor.Core.Storage;

/// <summary>
/// Opens connections to the app's local SQLite file. SQLite is embedded in the
/// executable, so a published build needs no database server, no Docker and no setup
/// from whoever runs it.
/// </summary>
public sealed class Database(string filePath)
{
    /// <summary>
    /// The folder under %LOCALAPPDATA% that holds the player's data. The installer must never
    /// install into it: uninstalling deletes the install folder (ReleaseWorkflowTests).
    /// </summary>
    public const string DataFolderName = "MtgaCollectionAdvisor";

    public string FilePath { get; } = filePath;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection($"Data Source={FilePath}");
        await connection.OpenAsync(ct);

        await using var pragmas = connection.CreateCommand();
        // WAL keeps reads working while a long import writes; cascading deletes on deck
        // cards need foreign keys switched on per connection.
        pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        await pragmas.ExecuteNonQueryAsync(ct);

        return connection;
    }

    /// <summary>The default database path, without creating its folder.</summary>
    public static string DefaultPath(string? overridePath = null) =>
        overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataFolderName,
            "advisor.db");

    public static Database CreateDefault(string? overridePath = null)
    {
        var path = DefaultPath(overridePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new Database(path);
    }
}
