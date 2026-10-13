using System.Diagnostics;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// The macOS window: Chromium's --app mode gives a plain window with no address bar or tabs.
/// Falls back to the default browser when neither is installed, which is also what a
/// development run on Windows gets (the Windows app is MtgaCollectionAdvisor.Desktop, #119).
/// </summary>
public static class AppWindowLaunch
{
    private static readonly string[] MacBrowsers = ["Microsoft Edge", "Google Chrome"];

    private const string WindowSize = "--window-size=1500,950";

    public static void Open(string url)
    {
        if (OperatingSystem.IsMacOS() && TryOpenMac(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// <c>open -na</c> exits non-zero when the app is not installed, and that exit code is the
    /// only signal: Process.Start with UseShellExecute would run <c>open msedge</c>, which fails
    /// but still hands back a process.
    /// </summary>
    private static bool TryOpenMac(string url)
    {
        foreach (var appName in MacBrowsers)
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
