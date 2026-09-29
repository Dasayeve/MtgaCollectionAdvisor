using Microsoft.AspNetCore.Components.Server.Circuits;
using MtgaCollectionAdvisor.Core;
using MtgaCollectionAdvisor.Core.Configuration;
using MtgaCollectionAdvisor.Core.Export;
using MtgaCollectionAdvisor.Core.Hosting;
using MtgaCollectionAdvisor.Core.Storage;
using MtgaCollectionAdvisor.Web.Components;
using MtgaCollectionAdvisor.Web.Services;
using Velopack;

// First, before anything else runs: the installer starts the exe with hook arguments while it
// installs, updates or uninstalls, and this handles them and exits.
VelopackApp.Build().Run();

const string InstanceMarker = "MtgaDeckAdvisor";

// 5199 unless MTGA_ADVISOR_PORT says otherwise. A second copy on another port - a test
// run next to the one the user has open - then never takes over the user's window.
var appUrl = $"http://localhost:{Environment.GetEnvironmentVariable("MTGA_ADVISOR_PORT") ?? "5199"}";

var openWindow = !args.Contains("--no-browser");

// Launched again while an instance is still running: open a window on that one rather
// than failing to bind the port.
if (await IsAlreadyRunningAsync(appUrl))
{
    if (openWindow) AppWindowLaunch.Open(appUrl);
    return;
}

// A published build carries its wwwroot next to the exe, but the content root defaults to the
// working directory, which the installer's restart does not set: without this the page would
// arrive with no CSS and no error. `dotnet run` has no wwwroot there and keeps the default.
var publishedRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = publishedRoot });

builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Warnings, errors and a few startup lines go to a daily file next to the database (#52): the
// installed app has no console, and this is what a player can send when something goes wrong.
var databasePath = Database.CreateDefault(AppConfig.Default.DatabasePathOverride).FilePath;
var fileLog = new FileLoggerProvider(LogFiles.FolderFor(databasePath));
builder.Logging.AddProvider(fileLog);
builder.Logging.AddFilter<FileLoggerProvider>(LogFiles.StartupCategory, LogLevel.Information);

builder.WebHost.UseUrls(appUrl);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Features:CreatorVideos in appsettings.json (or Features__CreatorVideos in the
// environment). Absent means off, so a release never gains the tab by accident.
builder.Services.AddSingleton(AppConfig.Default with
{
    CreatorVideosEnabled = builder.Configuration.GetValue("Features:CreatorVideos", false)
});
builder.Services.AddSingleton<AdvisorSession>();
builder.Services.AddSingleton<AppUpdater>();

// The window is only a browser pointed at this server; these stop the server once it has
// been closed, so nothing keeps watching MTG Arena with no window open.
builder.Services.AddSingleton<WindowPresence>();
builder.Services.AddScoped<CircuitHandler, WindowPresenceCircuitHandler>();
builder.Services.AddHostedService<StopWhenNoWindowService>();

var app = builder.Build();

fileLog.DeleteExpired(DateOnly.FromDateTime(DateTime.Now));
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LogFiles.StartupCategory);
startupLog.LogInformation("MTGA Deck Advisor {Version} starting", AppVersion.Current);
startupLog.LogInformation("Content root: {ContentRoot}", app.Environment.ContentRootPath);
startupLog.LogInformation("Database: {Database}", databasePath);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapGet("/instance", () => InstanceMarker);

// Downloads for the Export menu. Plain links with a download attribute, which Blazor
// leaves to the browser rather than routing.
app.MapGet("/export/collection.txt", (AdvisorSession session, CancellationToken ct) =>
    Download(session, async (export, token) => await export.CollectionTextAsync(token), ct));
app.MapGet("/export/collection.json", (AdvisorSession session, CancellationToken ct) =>
    Download(session, async (export, token) => await export.CollectionJsonAsync(token), ct));
app.MapGet("/export/user-decks.zip", (AdvisorSession session, CancellationToken ct) =>
    Download(session, async (export, token) => await export.UserDecksAsync(token), ct));
app.MapGet("/export/arena-decks.zip", (AdvisorSession session, bool? all, CancellationToken ct) =>
    Download(session, (export, token) => export.ArenaDecksAsync(all == true, token), ct));

await app.Services.GetRequiredService<AdvisorSession>().InitializeAsync();

if (openWindow)
{
    _ = Task.Run(() => AppWindowLaunch.Open(appUrl));
}

var updater = app.Services.GetRequiredService<AppUpdater>();
_ = Task.Run(() => updater.CheckAndDownloadAsync(app.Lifetime.ApplicationStopping));

app.Run();

// The window was closed and the app stopped: an update the player did not restart for is
// applied now, so the next start is the new version.
updater.ApplyOnExitIfReady();

static async Task<IResult> Download(
    AdvisorSession session, Func<DataExportService, CancellationToken, Task<ExportFile?>> write, CancellationToken ct)
{
    if (session.DataExport is not { } export) return Results.Problem("The app has not finished starting.");
    if (await write(export, ct) is not { } file) return Results.Problem("Nothing to export yet.", statusCode: 404);
    return Results.File(file.Content, file.ContentType, file.FileName);
}

// Asks the port whether this app is already on it. Anything else there - or nothing -
// is left for the bind to report as usual.
static async Task<bool> IsAlreadyRunningAsync(string appUrl)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        return await client.GetStringAsync($"{appUrl}/instance") == InstanceMarker;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return false;
    }
}
