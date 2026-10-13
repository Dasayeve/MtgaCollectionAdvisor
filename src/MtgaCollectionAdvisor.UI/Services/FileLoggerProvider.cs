using MtgaCollectionAdvisor.Core.Hosting;

namespace MtgaCollectionAdvisor.UI.Services;

/// <summary>
/// Writes warnings, errors and the app's startup lines to a daily file (#52): the installed app
/// has no console, so this file is what a player can send when something goes wrong. Naming,
/// retention and the line format are <see cref="LogFiles"/>. Writing never throws: a log that
/// can't be written must not break the app.
/// </summary>
public sealed class FileLoggerProvider(string folder) : ILoggerProvider
{
    private readonly Lock _gate = new();
    private DateOnly _cappedDay;

    public string Folder { get; } = folder;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Deletes the app's log files older than <see cref="LogFiles.KeepDays"/>.</summary>
    public void DeleteExpired(DateOnly today)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var names = Directory.GetFiles(Folder).Select(Path.GetFileName).OfType<string>();
            foreach (var name in LogFiles.ExpiredFiles(names, today)) File.Delete(Path.Combine(Folder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is housekeeping; a locked file is tried again next start.
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        lock (_gate)
        {
            if (_cappedDay == today) return;
            try
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, LogFiles.FileName(today));
                var file = new FileInfo(path);
                if (file.Exists && file.Length >= LogFiles.MaxBytesPerFile)
                {
                    _cappedDay = today;
                    File.AppendAllText(path, LogFiles.FormatEntry(now, LogFiles.LevelText((int)LogLevel.Warning),
                        "MtgaDeckAdvisor.Log", "Log size limit reached; nothing more is written today.", null));
                    return;
                }

                File.AppendAllText(path, LogFiles.FormatEntry(now, LogFiles.LevelText((int)level), category, message, exception));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unwritable log is dropped, never retried in a loop.
            }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Warnings and errors from anywhere; Information only from the app's own startup lines.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None
            && (logLevel >= LogLevel.Warning
                || (logLevel >= LogLevel.Information && category == LogFiles.StartupCategory));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
