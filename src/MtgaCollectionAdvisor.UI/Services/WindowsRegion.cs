using System.Globalization;
using System.Runtime.InteropServices;

namespace MtgaCollectionAdvisor.UI.Services;

/// <summary>
/// The Windows region settings, read locally. Windows keeps two: the home location (Settings →
/// Time &amp; language → Language &amp; region → Country or region) and the regional format, which
/// is what .NET's <see cref="RegionInfo.CurrentRegion"/> follows. They are often different.
/// </summary>
public static class WindowsRegion
{
    /// <summary>The home location as a two-letter code, e.g. "BR"; null when unavailable.</summary>
    public static string? HomeLocation()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var buffer = new char[16];
            var length = GetUserDefaultGeoName(buffer, buffer.Length);
            return length > 1 ? new string(buffer, 0, length - 1) : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null; // Windows older than 10 1709
        }
    }

    /// <summary>The regional format's country, e.g. "BR" for Portuguese (Brazil).</summary>
    public static string? RegionalFormat()
    {
        try
        {
            return RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultGeoName([Out] char[] geoName, int geoNameCount);
}
