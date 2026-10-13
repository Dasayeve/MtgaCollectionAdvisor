using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using MtgaCollectionAdvisor.Core;
using MtgaCollectionAdvisor.Core.Configuration;
using MtgaCollectionAdvisor.Core.Hosting;
using MtgaCollectionAdvisor.Core.Storage;
using MtgaCollectionAdvisor.UI.Services;
using Velopack;

namespace MtgaCollectionAdvisor.Desktop;

/// <summary>
/// The Windows app (#119). The process owns its window: closing it ends the app, and nothing
/// has to guess whether a browser window is still there, asleep or gone.
/// </summary>
public static class Program
{
    // WinForms and WebView2 need a single-threaded apartment, which an async Main can't have.
    [STAThread]
    public static void Main()
    {
        // First, before anything else runs: the installer starts the exe with hook arguments
        // while it installs, updates or uninstalls, and this handles them and exits.
        VelopackApp.Build().Run();

        // One app per database. A second launch hands the focus to the running window (Windows
        // lets a process take it only when the one holding it allows) and exits.
        var databasePath = Database.CreateDefault(AppConfig.Default.DatabasePathOverride).FilePath;
        var instanceName = SingleInstance.NameFor(databasePath);
        using var instance = new Mutex(initiallyOwned: true, instanceName, out var first);
        using var showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".show");
        if (!first)
        {
            AllowSetForegroundWindow(AnyProcess);
            showWindow.Set();
            return;
        }

        ApplicationConfiguration.Initialize();

        // Warnings, errors and a few startup lines go to a daily file next to the database (#52):
        // the app has no console, and this is what a player can send when something goes wrong.
        var fileLog = new FileLoggerProvider(LogFiles.FolderFor(databasePath));
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddProvider(fileLog);
            logging.AddFilter<FileLoggerProvider>(LogFiles.StartupCategory, LogLevel.Information);
        });
        services.AddWindowsFormsBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton(AppConfig.Default with { CreatorVideosEnabled = CreatorVideosEnabled() });
        services.AddSingleton<AdvisorSession>();
        // A restart opens its own window: nothing to pass.
        services.AddSingleton(provider => new AppUpdater(
            provider.GetRequiredService<ILogger<AppUpdater>>(), provider.GetRequiredService<ILoggerFactory>(), []));
        services.AddSingleton<IExportSaver, DesktopExportSaver>();
        var provider = services.BuildServiceProvider();

        fileLog.DeleteExpired(DateOnly.FromDateTime(DateTime.Now));
        var loggers = provider.GetRequiredService<ILoggerFactory>();
        var startupLog = loggers.CreateLogger(LogFiles.StartupCategory);
        var errorLog = loggers.CreateLogger("MtgaDeckAdvisor.Desktop");
        startupLog.LogInformation("MTGA Deck Advisor {Version} starting", AppVersion.Current);
        startupLog.LogInformation("Database: {Database}", databasePath);

        // An error nothing handled: logged with its exception, and a short message instead of
        // .NET's crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowError(errorLog, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            errorLog.LogError(e.ExceptionObject as Exception, "Unhandled error");

        if (!WebView2IsInstalled(errorLog))
        {
            startupLog.LogInformation("Stopping: WebView2 is not installed");
            return;
        }

        provider.GetRequiredService<AdvisorSession>().InitializeAsync().GetAwaiter().GetResult();

        using var window = new MainWindow(provider);
        _ = Task.Run(() =>
        {
            // Second launches: bring this window forward. Ends with the process.
            while (showWindow.WaitOne()) window.BeginInvoke(window.ShowToFront);
        });

        var updater = provider.GetRequiredService<AppUpdater>();
        using var stopping = new CancellationTokenSource();
        _ = Task.Run(() => updater.CheckAndDownloadAsync(stopping.Token));

        Application.Run(window);

        startupLog.LogInformation("Stopping: window closed");
        stopping.Cancel();
        // AdvisorSession is only IAsyncDisposable: this ends the MTG Arena watcher and the timers.
        provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // An update downloaded but not restarted for is applied now, for the next start.
        updater.ApplyOnExitIfReady();
    }

    /// <summary>
    /// WebView2 ships with Windows 11 and the installer adds it where it is missing; a copy run
    /// from the portable zip on a machine without it gets told where to get it.
    /// </summary>
    private static bool WebView2IsInstalled(ILogger log)
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            log.LogError(ex, "WebView2 is not installed");
            MessageBox.Show(
                "MTGA Deck Advisor needs Microsoft Edge WebView2, which is not installed on this PC.\n\n" +
                "Get it from Microsoft (https://go.microsoft.com/fwlink/p/?LinkId=2124703), then open the app again.",
                "MTGA Deck Advisor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static void ShowError(ILogger log, Exception exception)
    {
        log.LogError(exception, "Unhandled error");
        MessageBox.Show(
            "Something went wrong. The Logs link in the status bar opens the log files, which say what happened.",
            "MTGA Deck Advisor", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// Features:CreatorVideos in appsettings.json next to the exe. Absent means off, so a release
    /// never gains the tab by accident.
    /// </summary>
    private static bool CreatorVideosEnabled()
    {
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
            return settings.RootElement.TryGetProperty("Features", out var features)
                && features.TryGetProperty("CreatorVideos", out var enabled)
                && enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private const int AnyProcess = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
