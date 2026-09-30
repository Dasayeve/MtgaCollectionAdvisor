using MtgaCollectionAdvisor.Core.Hosting;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// Stops the app once <see cref="WindowPresence"/> says its window is gone, and writes why to
/// the log (#99): the installed app has no console, and an app that vanished with nothing
/// said is what made #99 hard to see. Stopping disposes <see cref="AdvisorSession"/>, which
/// ends the MTG Arena watcher and the Player.log poller, and frees port 5199.
/// </summary>
public sealed class StopWhenNoWindowService(
    WindowPresence presence, IHostApplicationLifetime lifetime, ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    private readonly ILogger _log = loggers.CreateLogger(LogFiles.StartupCategory);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (presence.ShouldStop(DateTimeOffset.UtcNow) is { } reason)
            {
                _log.LogInformation("Stopping: {Reason}", Describe(reason));
                lifetime.StopApplication();
                return;
            }
        }
    }

    private string Describe(StopReason reason) => reason switch
    {
        StopReason.WindowClosed => "every window closed",
        StopReason.NoWindow => $"no window for {presence.SilenceLimit.TotalMinutes:0} min",
        StopReason.AsleepTooLong => $"window asleep for {presence.SleepLimit.TotalHours:0} h",
        _ => reason.ToString(),
    };
}
