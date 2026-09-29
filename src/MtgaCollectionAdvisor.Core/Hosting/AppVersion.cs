using System.Reflection;

namespace MtgaCollectionAdvisor.Core.Hosting;

/// <summary>
/// The version shown to the player. The release workflow stamps it from the tag
/// (<c>-p:Version=0.1.0</c>); any other build keeps the SDK default and reads as "dev".
/// </summary>
public static class AppVersion
{
    private const string Development = "dev";

    // What a build without -p:Version reports; no release is ever tagged with it.
    private const string SdkDefault = "1.0.0";

    public static string Current { get; } = Display(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    // The SDK appends "+<commit>" to the informational version; the player only needs the release.
    public static string Display(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return Development;

        var version = informationalVersion.Split('+', 2)[0].Trim();
        return version is "" or SdkDefault ? Development : version;
    }

    private const string Releases = "https://github.com/Dasayeve/MtgaCollectionAdvisor/releases";

    /// <summary>The release notes of <paramref name="version"/> (#97); a dev build gets the list of releases.</summary>
    public static string ReleaseNotesUrl(string version) =>
        version == Development ? Releases : $"{Releases}/tag/v{version}";
}
