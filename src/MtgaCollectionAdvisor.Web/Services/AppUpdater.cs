using MtgaCollectionAdvisor.Core.Hosting;
using Velopack;
using Velopack.Sources;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// Keeps an installed copy current. It checks GitHub Releases once per start, downloads a
/// newer version in the background, and applies it when the player asks (Restart to update)
/// or, failing that, after the app stops. Only a Velopack install checks; <c>dotnet run</c>
/// and publish-local builds never touch the network for this.
/// </summary>
public sealed class AppUpdater
{
    private readonly ILogger<AppUpdater> _log;
    private readonly UpdateManager _manager;
    private VelopackAsset? _ready;

    public AppUpdater(ILogger<AppUpdater> log)
    {
        _log = log;
        var source = UpdateSource.OverrideFrom(Environment.GetEnvironmentVariable(UpdateSource.OverrideVariable));
        _manager = source is null
            ? new UpdateManager(new GithubSource(UpdateSource.RepositoryUrl, accessToken: null, prerelease: false))
            : new UpdateManager(source);
    }

    public event Action? Changed;

    public string CurrentVersion => AppVersion.Current;

    public string ReleaseNotesUrl => AppVersion.ReleaseNotesUrl(CurrentVersion);

    /// <summary>The version downloaded and waiting to be applied, if any.</summary>
    public string? ReadyVersion => _ready?.Version.ToString();

    public async Task CheckAndDownloadAsync(CancellationToken ct)
    {
        if (!_manager.IsInstalled) return;

        try
        {
            // Downloaded by an earlier run that stopped before applying it.
            if (_manager.UpdatePendingRestart is { } pending)
            {
                SetReady(pending);
                return;
            }

            if (await _manager.CheckForUpdatesAsync() is not { } update) return;

            await _manager.DownloadUpdatesAsync(update, cancelToken: ct);
            SetReady(update.TargetFullRelease);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Unreachable GitHub (offline, or the repository still private) means "no news",
            // not an error the player has to see.
            _log.LogWarning(ex, "Update check failed");
        }
    }

    /// <summary>Applies the downloaded update and restarts; the open window reloads onto it.</summary>
    public void RestartToUpdate()
    {
        if (_ready is null) return;

        // --no-browser: the window already open reconnects to the new instance (the reconnect
        // script reloads when its old circuit is rejected), so a second one is not wanted.
        _manager.ApplyUpdatesAndRestart(_ready, ["--no-browser"]);
    }

    /// <summary>Called once the app has stopped: applies a downloaded update for the next start.</summary>
    public void ApplyOnExitIfReady()
    {
        if (_ready is null) return;

        _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
    }

    private void SetReady(VelopackAsset asset)
    {
        _ready = asset;
        Changed?.Invoke();
    }
}
