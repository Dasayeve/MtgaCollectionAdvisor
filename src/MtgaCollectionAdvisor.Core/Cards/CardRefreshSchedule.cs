namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>
/// When the card database refreshes itself for a new set (#89), and how often it may ask.
///
/// Scryfall's bulk file is regenerated every 12 hours with prices in it, so its date alone says
/// nothing about new cards: the maintainer's flag (card-data.json) says a set has landed, and
/// the file's date only answers "does Scryfall have a file from after that yet". Every call has
/// a ceiling per install, so the player's app never meets a provider's rate limit.
/// </summary>
public static class CardRefreshSchedule
{
    /// <summary>
    /// The least time between two reads of card-data.json, two looks at Scryfall's listing, or
    /// two automatic imports. Held across restarts: the times are stored.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public static bool IsDue(DateTimeOffset? lastAt, DateTimeOffset now) =>
        lastAt is not { } last || now - last >= Interval;

    /// <summary>
    /// The flag's date has passed and this copy's cards came from a Scryfall file older than it,
    /// or from one of unknown date (a database imported before #89).
    /// </summary>
    public static bool IsPending(DateTimeOffset? refreshAfter, DateTimeOffset? importedSource, DateTimeOffset now) =>
        refreshAfter is { } flag
        && flag <= now
        && (importedSource is not { } source || source < flag);

    /// <summary>
    /// Import now: the flag is pending, Scryfall's current file was generated after the flag (an
    /// older one wouldn't have the set yet), and no automatic import was tried within
    /// <see cref="Interval"/>, which holds a failing import to one try per window.
    /// </summary>
    public static bool ShouldImport(
        DateTimeOffset? refreshAfter,
        DateTimeOffset? importedSource,
        DateTimeOffset? scryfallFileAt,
        DateTimeOffset? lastAutoAttempt,
        DateTimeOffset now) =>
        IsPending(refreshAfter, importedSource, now)
        && scryfallFileAt is { } file && file >= refreshAfter!.Value
        && IsDue(lastAutoAttempt, now);
}
