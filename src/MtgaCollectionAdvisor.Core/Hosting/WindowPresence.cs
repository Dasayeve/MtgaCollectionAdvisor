namespace MtgaCollectionAdvisor.Core.Hosting;

/// <summary>What a window last said about itself (#99).</summary>
public enum WindowState { Visible, Hidden, Closed }

/// <summary>Why the app decided to stop, for the log.</summary>
public enum StopReason
{
    /// <summary>Every window the app heard from said it closed, and none came back.</summary>
    WindowClosed,

    /// <summary>No window for the silence limit, and the last one heard from was visible or none ever reported.</summary>
    NoWindow,
}

/// <summary>
/// Whether the app still has a window, and so whether it should keep running. The window
/// is only a browser pointed at the local server; without this, closing it leaves the
/// server - and its MTG Arena watcher - running invisibly until killed (#34).
///
/// A lost connection is not a closed window (#99). Browsers throttle and freeze hidden pages
/// (a window behind MTG Arena in full screen counts as hidden), and a frozen page drops its
/// connection. So each page reports its state with beacons - visible, hidden, closed - and
/// a window that went quiet after saying "hidden" is asleep, not gone. Only a close stops
/// the app quickly; the other cases wait long, because an app left running is harmless (a
/// new launch finds it) and one stopped under a sleeping window is the bug. A hidden window
/// is waited for however long it stays hidden (#118): a minimised desktop app stays open.
///
/// Time the machine spent asleep counts toward no limit (#118). The process sleeps with it, so
/// its first check on waking would otherwise see the whole night gone and stop before the
/// window has woken up to reconnect.
///
/// Nothing stops until a first window has connected, so a run nobody opens (smoke tests
/// with <c>--no-browser</c>) stays up.
/// </summary>
public sealed class WindowPresence
{
    public static readonly TimeSpan DefaultCloseGrace = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DefaultSilenceLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a window not heard from is remembered, and its circuit kept for it to resume
    /// where it was. A window back later than this reloads, losing only the screen's state.
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(12);

    /// <summary>A check this much later than due means the machine slept (or its clock jumped) in between.</summary>
    public static readonly TimeSpan SleepDetectionSlack = TimeSpan.FromMinutes(1);

    /// <summary>Any local page can send beacons; this keeps the table from growing without bound.</summary>
    public const int MaxTrackedWindows = 32;

    private const int MaxWindowIdLength = 64;

    private readonly TimeSpan _closeGrace;
    private readonly TimeSpan _silenceLimit;
    private readonly TimeSpan _retention;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (WindowState State, DateTimeOffset At)> _windows = new(StringComparer.Ordinal);
    private int _open;
    private bool _everConnected;
    private DateTimeOffset? _emptySince;

    public WindowPresence() : this(DefaultCloseGrace, DefaultSilenceLimit, DefaultRetention) { }

    public WindowPresence(TimeSpan closeGrace, TimeSpan silenceLimit, TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(closeGrace, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(silenceLimit, closeGrace);
        ArgumentOutOfRangeException.ThrowIfLessThan(retention, silenceLimit);
        _closeGrace = closeGrace;
        _silenceLimit = silenceLimit;
        _retention = retention;
    }

    public TimeSpan SilenceLimit => _silenceLimit;

    /// <summary>
    /// Whether the gap between two checks, run every <paramref name="checkEvery"/>, means the
    /// machine slept in between. A clock set forward reads the same, and is handled the same.
    /// </summary>
    public static bool SleptBetween(DateTimeOffset previousCheck, DateTimeOffset now, TimeSpan checkEvery) =>
        now - previousCheck > checkEvery + SleepDetectionSlack;

    /// <summary>
    /// The machine slept from <paramref name="from"/> to <paramref name="until"/>: everything
    /// recorded by then moves forward by the time asleep, as if it had not happened. Anything
    /// recorded after waking (a beacon, a reconnect) stays as it is.
    /// </summary>
    public void Slept(DateTimeOffset from, DateTimeOffset until)
    {
        var asleep = until - from;
        if (asleep <= TimeSpan.Zero) return;

        lock (_lock)
        {
            if (_emptySince is { } emptySince && emptySince <= from) _emptySince = emptySince + asleep;

            foreach (var (id, window) in _windows.Where(w => w.Value.At <= from).ToList())
            {
                _windows[id] = (window.State, window.At + asleep);
            }
        }
    }

    /// <summary>A page's own id: a UUID in practice, anything short and plain accepted.</summary>
    public static bool IsValidWindowId(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= MaxWindowIdLength
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public void Connected()
    {
        lock (_lock)
        {
            _open++;
            _everConnected = true;
            _emptySince = null;
        }
    }

    public void Disconnected(DateTimeOffset now)
    {
        lock (_lock)
        {
            // Never below zero: a stray extra disconnect must not make the next window
            // count as none.
            _open = Math.Max(0, _open - 1);
            if (_open == 0) _emptySince = now;
        }
    }

    /// <summary>A page's beacon. An invalid id is ignored.</summary>
    public void Reported(string windowId, WindowState state, DateTimeOffset now)
    {
        if (!IsValidWindowId(windowId)) return;

        lock (_lock)
        {
            _windows[windowId] = (state, now);
            if (_windows.Count > MaxTrackedWindows)
            {
                var stalest = _windows.MinBy(w => w.Value.At).Key;
                _windows.Remove(stalest);
            }
        }
    }

    /// <summary>Null means keep running; otherwise why to stop.</summary>
    public StopReason? ShouldStop(DateTimeOffset now)
    {
        lock (_lock)
        {
            // Forget windows not heard from for the retention, counted up to the last moment a
            // window was connected: a sleeping window says nothing after "hidden", and must not
            // be forgotten while the app waits for it.
            var lastConnected = _open > 0 || _emptySince is null ? now : _emptySince.Value;
            foreach (var stale in _windows.Where(w => lastConnected - w.Value.At > _retention).Select(w => w.Key).ToList())
            {
                _windows.Remove(stale);
            }

            if (!_everConnected || _open > 0 || _emptySince is not { } emptySince) return null;

            var live = _windows.Values.Where(w => w.State != WindowState.Closed).ToList();

            // Went quiet while hidden: the browser froze it. It wakes up when the player comes back,
            // however long that takes (#118).
            if (live.Any(w => w.State == WindowState.Hidden)) return null;

            // Every window that ever spoke said it closed. The grace lets a reload's new page connect.
            if (_windows.Count > 0 && live.Count == 0)
            {
                var lastClose = _windows.Values.Max(w => w.At);
                var since = lastClose > emptySince ? lastClose : emptySince;
                return now - since >= _closeGrace ? StopReason.WindowClosed : null;
            }

            // Last seen visible, or no beacon ever arrived: a killed browser, a blocked script.
            return now - emptySince >= _silenceLimit ? StopReason.NoWindow : null;
        }
    }
}
