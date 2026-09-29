using MtgaCollectionAdvisor.Core.Arena;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Logs;

/// <summary>
/// Where MTG Arena writes Player.log: <c>%LOCALAPPDATA%\..\LocalLow\Wizards Of The Coast\MTGA</c>
/// on Windows, <c>~/Library/Logs/Wizards Of The Coast/MTGA</c> on macOS.
/// <c>MTGA_ADVISOR_PLAYERLOG_PATH</c> overrides it.
/// </summary>
public static class PlayerLogPaths
{
    public static string DefaultPath => For(
        OperatingSystem.IsMacOS(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The path for an OS from its base folders; pure, so tests can pin each shape.</summary>
    public static string For(bool isMacOS, string localApplicationData, string userHome) => isMacOS
        ? Path.Combine(userHome, "Library", "Logs", "Wizards Of The Coast", "MTGA", "Player.log")
        : Path.Combine(localApplicationData, "..", "LocalLow", "Wizards Of The Coast", "MTGA", "Player.log");
}

/// <summary>
/// Tails Player.log on a poll loop (the file is kept open for writing by MTGA/Unity,
/// so FileSystemWatcher alone is unreliable) and raises typed events as collection or
/// wildcard data is observed. Handles log truncation/rotation, which happens whenever
/// the MTGA client restarts.
/// </summary>
public sealed class PlayerLogWatcher : IDisposable
{
    private readonly string _path;
    private readonly TimeSpan _pollInterval;
    private readonly LogMessageParser _parser = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private long _position;

    public event Action<WildcardInventory>? InventoryUpdated;
    public event Action<IReadOnlyDictionary<int, int>>? CollectionUpdated;
    public event Action<Exception>? WatchError;

    /// <summary>The decks saved in Arena, raised at each login Arena logs.</summary>
    public event Action<IReadOnlyList<ArenaDeck>>? ArenaDecksUpdated;

    /// <summary>Whether MTG Arena's Detailed Logs option is on, each time a session's log says so (#57).</summary>
    public event Action<bool>? DetailedLogsReported;

    public bool LogExists => File.Exists(_path);

    public PlayerLogWatcher(string? path = null, TimeSpan? pollInterval = null)
    {
        _path = path ?? PlayerLogPaths.DefaultPath;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    public void Start()
    {
        _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, _pollInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Poll()
    {
        if (!Monitor.TryEnter(_gate)) return; // previous poll still running (slow disk); skip this tick
        try
        {
            if (!File.Exists(_path)) return;

            var length = new FileInfo(_path).Length;
            if (length < _position)
            {
                // MTGA restarted and truncated/rotated the log.
                _position = 0;
                _parser.Reset();
            }
            if (length == _position) return;

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // A plain line, not JSON: checked before the event parser, which skips it.
                if (DetailedLogsLine.Parse(line) is { } detailedLogs)
                {
                    DetailedLogsReported?.Invoke(detailedLogs);
                    continue;
                }

                LogEvent? evt;
                try
                {
                    evt = _parser.ProcessLine(line);
                }
                catch
                {
                    // A malformed/partial line must never stop the watcher.
                    continue;
                }

                if (evt is null) continue;
                Dispatch(evt);
            }

            _position = stream.Position;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File momentarily locked by MTGA's own writer; try again next tick.
        }
        catch (Exception ex)
        {
            WatchError?.Invoke(ex);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void Dispatch(LogEvent evt)
    {
        if (LogEventInterpreter.TryGetPlayerCards(evt, out var ownedByGrpId))
        {
            CollectionUpdated?.Invoke(ownedByGrpId);
            return;
        }

        if (LogEventInterpreter.TryGetInventoryInfo(evt, out var inventory))
        {
            InventoryUpdated?.Invoke(inventory);
        }

        // The login message carries the decks alongside the inventory, so this is not an else.
        if (LogEventInterpreter.TryGetArenaDecks(evt, out var decks))
        {
            ArenaDecksUpdated?.Invoke(decks);
        }
    }

    public void Dispose() => Stop();
}
