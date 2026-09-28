namespace MtgaCollectionAdvisor.Core.Configuration;

/// <summary>
/// App-wide configuration. Data lives in a local SQLite file under the user's
/// LocalApplicationData, so a published build needs no database server or setup.
/// </summary>
public sealed record AppConfig(string? DatabasePathOverride, string? PlayerLogPathOverride)
{
    /// <summary>
    /// The Creators tab. Off unless configuration turns it on (Features:CreatorVideos), so
    /// a release can never gain it by accident; when off, it makes no network requests.
    /// </summary>
    public bool CreatorVideosEnabled { get; init; }

    /// <summary>
    /// Where to read card-data.json (#89) instead of the repository: a test points it at a local
    /// file server (MTGA_ADVISOR_CARD_DATA_URL), to watch a real refresh without touching master.
    /// </summary>
    public string? CardDataUrlOverride { get; init; }

    public static AppConfig Default { get; } = new(
        DatabasePathOverride: Environment.GetEnvironmentVariable("MTGA_ADVISOR_DB_PATH"),
        PlayerLogPathOverride: Environment.GetEnvironmentVariable("MTGA_ADVISOR_PLAYERLOG_PATH"))
    {
        CardDataUrlOverride = Environment.GetEnvironmentVariable("MTGA_ADVISOR_CARD_DATA_URL"),
    };
}
