using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MtgaCollectionAdvisor.Core.Creators;

/// <summary>
/// The Creators tab's channel list, kept in <c>creators.json</c> at the repository's root so the
/// maintainer can change it without a release (#64). Every copy reads it at most once a day,
/// keeps the last good one, and falls back to the list compiled in (<see cref="CreatorChannels.All"/>).
/// The list is curated by the maintainer only: players cannot add channels.
///
/// The file is read as external data, although it is the maintainer's own: an entry that fails
/// validation is dropped, and a file that yields nothing changes nothing.
/// </summary>
public static partial class CreatorRoster
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/Dasayeve/MtgaCollectionAdvisor/master/creators.json";

    /// <summary>How long a fetched list is trusted; also the wait after a failed fetch.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    public const int MaxNameLength = 60;

    [GeneratedRegex("^UC[0-9A-Za-z_-]{22}$")]
    private static partial Regex ChannelId();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The valid channels in <paramref name="json"/>, in file order; null when the file can't be
    /// read or has no valid channel. A channel whose name or id repeats an earlier one is dropped,
    /// since the app keys a creator's videos and feed schedule by name.
    /// </summary>
    public static IReadOnlyList<CreatorChannel>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        RosterFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RosterFile>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var channels = new List<CreatorChannel>();

        // Explicit nulls where a list is expected are coalesced here, not trusted to initializers.
        foreach (var entry in file?.Channels ?? [])
        {
            if (entry is null) continue;

            var name = entry.Name?.Trim();
            var id = entry.ChannelId?.Trim();
            var language = string.IsNullOrWhiteSpace(entry.Language)
                ? CreatorChannel.English
                : entry.Language.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl)) continue;
            if (id is null || !ChannelId().IsMatch(id)) continue;
            if (!IsLanguageCode(language)) continue;
            if (!names.Add(name) || !ids.Add(id)) continue;

            channels.Add(new CreatorChannel(name, id, entry.PostsOtherContent ?? false, language));
        }

        return channels.Count > 0 ? channels : null;
    }

    /// <summary>The list to use: the one just fetched, else the last one stored, else the compiled one.</summary>
    public static IReadOnlyList<CreatorChannel> Choose(
        IReadOnlyList<CreatorChannel>? fetched, IReadOnlyList<CreatorChannel>? stored) =>
        fetched ?? stored ?? CreatorChannels.All;

    /// <summary>At most one request a day, whether the last one worked or not.</summary>
    public static bool IsDue(DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        lastAttempt is not { } attempt || now - attempt >= CheckEvery || attempt > now;

    /// <summary>A two-letter language .NET knows, like "en" or "pt".</summary>
    private static bool IsLanguageCode(string code) => LanguageCodes.Contains(code);

    // Neutral cultures only: asking for a culture by name accepts any well-formed code ("zz").
    private static readonly HashSet<string> LanguageCodes = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Select(c => c.TwoLetterISOLanguageName)
        .Where(code => code.Length == 2)
        .ToHashSet(StringComparer.Ordinal);

    private sealed record RosterFile(IReadOnlyList<RosterEntry?>? Channels);

    private sealed record RosterEntry(string? Name, string? ChannelId, string? Language, bool? PostsOtherContent);
}
