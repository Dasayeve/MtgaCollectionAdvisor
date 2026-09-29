using System.Diagnostics;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// Chromium's --app mode gives a plain window with no address bar or tabs, so the tool
/// feels like a desktop app. Falls back to the default browser when neither is present.
/// </summary>
public static class AppWindowLaunch
{
    private static readonly (string WindowsCommand, string MacAppName)[] Browsers =
    [
        ("msedge", "Microsoft Edge"),
        ("chrome", "Google Chrome"),
    ];

    private const string WindowSize = "--window-size=1500,950";

    public static void Open(string url)
    {
        var opened = OperatingSystem.IsMacOS() ? TryOpenMac(url) : TryOpenWindows(url);
        if (!opened) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>The browser by command name; a started process is success.</summary>
    private static bool TryOpenWindows(string url)
    {
        foreach (var (command, _) in Browsers)
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo(command)
                {
                    UseShellExecute = true,
                    Arguments = $"--app={url} {WindowSize}"
                });
                if (process is not null) return true;
            }
            catch
            {
                // Browser not installed - try the next one.
            }
        }

        return false;
    }

    /// <summary>
    /// <c>open -na</c> exits non-zero when the app is not installed, and that exit code is the
    /// only signal: Process.Start with UseShellExecute would run <c>open msedge</c>, which fails
    /// but still hands back a process.
    /// </summary>
    private static bool TryOpenMac(string url)
    {
        foreach (var (_, appName) in Browsers)
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/open")
            {
                ArgumentList = { "-na", appName, "--args", $"--app={url}", WindowSize }
            });
            process?.WaitForExit();
            if (process?.ExitCode == 0) return true;
        }

        return false;
    }
}
