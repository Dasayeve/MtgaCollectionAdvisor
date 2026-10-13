using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MtgaCollectionAdvisor.Core;
using MtgaCollectionAdvisor.Core.Configuration;
using MtgaCollectionAdvisor.Core.Hosting;
using MtgaCollectionAdvisor.Core.Storage;
using MtgaCollectionAdvisor.Web.Services;
using Velopack;

namespace MtgaCollectionAdvisor.Desktop;

// Spike for #119. No Kestrel, no port, no SignalR: the process owns its window, so closing it
// ends the app and nothing has to guess whether a browser window is still there.
public static class Program
{
    // WebView2 and WinForms need a single-threaded apartment, which an async Main can't have.
    [STAThread]
    public static void Main()
    {
        // First, as in the Web project: the installer runs the exe with hook arguments.
        VelopackApp.Build().Run();

        // One instance per database. A second launch asks the first to come to the front and exits.
        var databasePath = Database.CreateDefault(AppConfig.Default.DatabasePathOverride).FilePath;
        var instanceName = "MtgaDeckAdvisor.Desktop." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(databasePath).ToLowerInvariant())))[..16];
        using var instance = new Mutex(initiallyOwned: true, instanceName, out var first);
        using var showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".show");
        if (!first)
        {
            // Windows lets a process take the focus only if the one holding it allows: this one
            // was just launched by the player, so it can pass that on to the running window.
            AllowSetForegroundWindow(AnyProcess);
            showWindow.Set();
            return;
        }

        ApplicationConfiguration.Initialize();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

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
        services.AddSingleton(AppConfig.Default with
        {
            CreatorVideosEnabled = configuration.GetValue("Features:CreatorVideos", true)
        });
        services.AddSingleton<AdvisorSession>();
        services.AddSingleton<AppUpdater>();

        var provider = services.BuildServiceProvider();

        fileLog.DeleteExpired(DateOnly.FromDateTime(DateTime.Now));
        var startupLog = provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogFiles.StartupCategory);
        startupLog.LogInformation("MTGA Deck Advisor {Version} starting (desktop window, #119 spike)", AppVersion.Current);
        startupLog.LogInformation("Database: {Database}", databasePath);

        provider.GetRequiredService<AdvisorSession>().InitializeAsync().GetAwaiter().GetResult();

        using var window = new MainWindow(provider);
        _ = Task.Run(() =>
        {
            // Second launches: bring this window forward. Ends with the process.
            while (showWindow.WaitOne())
            {
                window.BeginInvoke(window.ShowToFront);
            }
        });

        Application.Run(window);
        startupLog.LogInformation("Stopping: window closed");

        // AdvisorSession is only IAsyncDisposable: this ends the MTG Arena watcher and the timers.
        provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private const int AnyProcess = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
