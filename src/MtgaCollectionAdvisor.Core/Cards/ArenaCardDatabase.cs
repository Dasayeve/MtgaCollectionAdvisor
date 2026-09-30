using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>A card as MTG Arena's own card database describes it (#101): no legality, no image.</summary>
public sealed record ArenaDatabaseCard(
    int GrpId,
    string Name,
    string SetCode,
    string CollectorNumber,
    CardRarity Rarity,
    string ManaCost,
    string Colors,
    bool IsNonBasicLand,
    bool IsDigitalOnly,
    bool IsRebalanced);

/// <summary>One of Arena's card database files. Its name carries a hash that changes with each Arena data update.</summary>
public sealed record ArenaCardFile(string Path, string FileName)
{
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>Arena's card database could not be read: a layout this build doesn't know, a locked or corrupt file.</summary>
public sealed class ArenaCardDatabaseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// MTG Arena keeps every card it knows in a plain SQLite file,
/// <c>MTGA_Data\Downloads\Raw\Raw_CardDatabase_&lt;hash&gt;.mtga</c>, days before Scryfall
/// publishes the new set's Arena ids (#101). Its ids are the ones Scryfall uses. It has no
/// legality and no images, so it only lends ids: <see cref="CardSourceMerge"/> decides the rest.
/// </summary>
public static class ArenaCardDatabase
{
    private const string FilePattern = "Raw_CardDatabase_*.mtga";

    /// <summary>
    /// The newest card database in the first folder that has one: the running MTG Arena's, the one
    /// remembered from the last read, then the usual install folders. Null when there is none, as
    /// on a machine without Arena; macOS is only found through a remembered folder for now.
    /// </summary>
    public static ArenaCardFile? Find(string? rememberedRawFolder)
    {
        foreach (var folder in CandidateFolders(rememberedRawFolder))
        {
            if (NewestIn(folder) is { } file) return file;
        }
        return null;
    }

    /// <summary>The newest <c>Raw_CardDatabase_*.mtga</c> in <paramref name="rawFolder"/> by write time; null if none.</summary>
    public static ArenaCardFile? NewestIn(string rawFolder)
    {
        try
        {
            if (!Directory.Exists(rawFolder)) return null;
            var newest = new DirectoryInfo(rawFolder).GetFiles(FilePattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest is null ? null : new ArenaCardFile(newest.FullName, newest.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> CandidateFolders(string? remembered)
    {
        if (OperatingSystem.IsWindows() && RunningArenaFolder() is { } running) yield return running;
        if (!string.IsNullOrWhiteSpace(remembered)) yield return remembered;
        if (!OperatingSystem.IsWindows()) yield break;

        foreach (var install in (string[])
                 [
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wizards of the Coast", "MTGA"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "MagicTheGathering"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "MTGA"),
                 ])
        {
            yield return RawFolder(install);
        }
    }

    private static string? RunningArenaFolder()
    {
        foreach (var process in Process.GetProcessesByName("MTGA"))
        {
            using (process)
            {
                try
                {
                    if (Path.GetDirectoryName(process.MainModule?.FileName) is { } install) return RawFolder(install);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Exited meanwhile, or not ours to inspect: the other folders are still tried.
                }
            }
        }
        return null;
    }

    private static string RawFolder(string install) => Path.Combine(install, "MTGA_Data", "Downloads", "Raw");

    private static readonly string[] CardColumns =
    [
        "GrpId", "TitleId", "ExpansionCode", "CollectorNumber", "Rarity", "OldSchoolManaText", "Colors",
        "Types", "Supertypes", "LinkedFaceGrpIds", "IsToken", "IsPrimaryCard", "IsDigitalOnly", "IsRebalanced",
    ];

    private static readonly string[] NameColumns = ["LocId", "Formatted", "Loc"];

    /// <summary>
    /// Every primary, non-token card in the file. Opened read-only and unpooled, so no handle
    /// outlives the read to get in the way of Arena's own update.
    /// </summary>
    /// <exception cref="ArenaCardDatabaseException">The layout is unknown, or the file can't be read.</exception>
    public static async Task<IReadOnlyList<ArenaDatabaseCard>> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new ArenaCardDatabaseException($"Arena card database not found: {Path.GetFileName(path)}");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);

            await RequireColumnsAsync(connection, "Cards", CardColumns, ct);
            await RequireColumnsAsync(connection, "Localizations_enUS", NameColumns, ct);

            var rows = await ReadRowsAsync(connection, ct);
            return rows.Values
                .Where(row => row.IsPrimary)
                .Select(row => ToCard(row, rows))
                .OfType<ArenaDatabaseCard>()
                .ToList();
        }
        catch (SqliteException ex)
        {
            throw new ArenaCardDatabaseException($"Arena card database unreadable: {ex.Message}", ex);
        }
    }

    private static async Task RequireColumnsAsync(SqliteConnection connection, string table, string[] columns, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) present.Add(reader.GetString(0));
        }

        if (present.Count == 0) throw new ArenaCardDatabaseException($"Unknown Arena card database layout: no {table} table");
        var missing = columns.Where(c => !present.Contains(c)).ToList();
        if (missing.Count > 0)
            throw new ArenaCardDatabaseException($"Unknown Arena card database layout: {table} has no {string.Join(", ", missing)}");
    }

    private sealed record Row(
        int GrpId, string? Name, string? SetCode, string? CollectorNumber, int Rarity, string? ManaText,
        string? Colors, string? Types, string? Supertypes, string? LinkedFaces, bool IsPrimary, bool IsDigitalOnly, bool IsRebalanced);

    // Every non-token row, second faces included: a primary card names itself "Front // Back" from its linked face.
    // Formatted = 1 is the name Arena displays; the others are fallbacks for a title that lacks it.
    private static async Task<Dictionary<int, Row>> ReadRowsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.GrpId,
                   (SELECT l.Loc FROM Localizations_enUS l WHERE l.LocId = c.TitleId ORDER BY l.Formatted = 1 DESC LIMIT 1),
                   c.ExpansionCode, c.CollectorNumber, c.Rarity, c.OldSchoolManaText, c.Colors, c.Types, c.Supertypes,
                   c.LinkedFaceGrpIds, c.IsPrimaryCard, c.IsDigitalOnly, c.IsRebalanced
            FROM Cards c
            WHERE c.IsToken = 0
            """;

        var rows = new Dictionary<int, Row>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Row(
                GrpId: reader.GetInt32(0),
                Name: Text(reader, 1),
                SetCode: Text(reader, 2),
                CollectorNumber: Text(reader, 3),
                Rarity: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                ManaText: Text(reader, 5),
                Colors: Text(reader, 6),
                Types: Text(reader, 7),
                Supertypes: Text(reader, 8),
                LinkedFaces: Text(reader, 9),
                IsPrimary: Flag(reader, 10),
                IsDigitalOnly: Flag(reader, 11),
                IsRebalanced: Flag(reader, 12));
            rows.TryAdd(row.GrpId, row);
        }
        return rows;
    }

    private static ArenaDatabaseCard? ToCard(Row row, IReadOnlyDictionary<int, Row> rows)
    {
        if (row.GrpId <= 0 || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.SetCode)) return null;

        var name = ArenaCardText.CleanName(row.Name);
        if (name.Length == 0) return null;

        // A card with one other face under another name (adventure, omen, modal and transforming
        // double-faced cards) is "Front // Back", as Scryfall names it. Several faces
        // (specialize) or one of the same name (prototype) keep the front's name, as Scryfall does.
        var face = SecondFace(row, rows);
        if (face?.Name is { } faceName && !name.Contains(" // ", StringComparison.Ordinal)
            && ArenaCardText.CleanName(faceName) is { Length: > 0 } back && back != name)
        {
            name = $"{name} // {back}";
        }

        return new ArenaDatabaseCard(
            GrpId: row.GrpId,
            Name: name,
            SetCode: row.SetCode.Trim(),
            CollectorNumber: row.CollectorNumber?.Trim() ?? "",
            Rarity: ArenaCardText.Rarity(row.Rarity),
            ManaCost: ArenaCardText.ManaCost(row.ManaText),
            Colors: ArenaCardText.Colors([row.Colors, face?.Colors]),
            IsNonBasicLand: ArenaCardText.IsNonBasicLand(row.Types, row.Supertypes),
            IsDigitalOnly: row.IsDigitalOnly,
            // Alchemy's rebalanced cards were not flagged IsRebalanced in the file checked; their "A-" name says it.
            IsRebalanced: row.IsRebalanced || name.StartsWith("A-", StringComparison.Ordinal));
    }

    private static Row? SecondFace(Row row, IReadOnlyDictionary<int, Row> rows)
    {
        var ids = (row.LinkedFaces ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return ids.Length == 1 && int.TryParse(ids[0], out var id) && rows.TryGetValue(id, out var face) && !face.IsPrimary
            ? face
            : null;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static bool Flag(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture) != 0;
}
