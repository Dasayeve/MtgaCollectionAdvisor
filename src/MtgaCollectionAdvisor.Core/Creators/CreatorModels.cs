using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Creators;

/// <summary>
/// A YouTube channel whose videos the Creators tab lists. The id is verified by hand:
/// resolving a creator's @handle landed on a stranger's channel 4 times in 13.
///
/// <paramref name="PostsOtherContent"/> marks a channel that mixes Magic with other
/// videos: only its Magic ones are listed (see <see cref="CreatorVideoRelevance"/>).
/// <paramref name="Language"/> is the two-letter language the channel speaks; the app
/// ships mainly in English, so anything else is behind a filter.
/// </summary>
public sealed record CreatorChannel(
    string Name,
    string ChannelId,
    bool PostsOtherContent = false,
    string Language = CreatorChannel.English)
{
    public const string English = "en";

    public bool IsEnglish => Language.Equals(English, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One upload as its channel feed lists it. The description is read, never stored.</summary>
public sealed record FeedVideo(
    string Creator,
    string VideoId,
    string Title,
    DateTimeOffset Published,
    string Description,
    string Language = CreatorChannel.English);

/// <summary>Where a video's decklist can be recovered from, if anywhere.</summary>
public enum DeckSourceKind
{
    None,

    /// <summary>An Arena export pasted into the description.</summary>
    InlineList,

    /// <summary>A link to an Archidekt deck, which the app can read.</summary>
    Archidekt,

    /// <summary>A link to a site the app must not read (AetherHub, Moxfield, ...).</summary>
    External
}

public sealed record VideoDeckSource(
    DeckSourceKind Kind,
    string? Decklist = null,
    int? ArchidektId = null,
    string? ExternalSite = null,
    string? ExternalUrl = null)
{
    public static VideoDeckSource None { get; } = new(DeckSourceKind.None);
}

/// <summary>What is kept about a video: everything its description told us, but not the text.</summary>
public sealed record CreatorVideo(
    string VideoId,
    string Creator,
    string Title,
    DateTimeOffset Published,
    DeckSourceKind Kind,
    string? Decklist,
    int? ArchidektId,
    string? ExternalSite,
    string? ExternalUrl,
    string Language = CreatorChannel.English)
{
    private const int MaxDeckNameLength = 60;

    public bool IsEnglish => Language.Equals(CreatorChannel.English, StringComparison.OrdinalIgnoreCase);

    public string Url => $"https://www.youtube.com/watch?v={VideoId}";

    public string ThumbnailUrl => $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg";

    /// <summary>An Archidekt link whose list has not been read yet, so a refresh should try again.</summary>
    public bool NeedsArchidektFetch => Kind == DeckSourceKind.Archidekt && Decklist is null && ArchidektId is not null;

    /// <summary>
    /// A name for the deck if the user imports it. Video titles are clickbait-long; the
    /// part before the first separator is usually the deck ("MONO RED ★ RELENTLESS SPEED").
    /// </summary>
    public string SuggestedDeckName
    {
        get
        {
            var cut = Title.IndexOfAny(['|', '★', '#']);
            var name = (cut > 0 ? Title[..cut] : Title).Trim();
            if (name.Length == 0) name = Title.Trim();
            return name.Length > MaxDeckNameLength ? name[..MaxDeckNameLength].TrimEnd() : name;
        }
    }

    public static CreatorVideo From(FeedVideo video, VideoDeckSource source) => new(
        video.VideoId,
        video.Creator,
        video.Title,
        video.Published,
        source.Kind,
        source.Decklist,
        source.ArchidektId,
        source.ExternalSite,
        source.ExternalUrl,
        video.Language);

    /// <summary>A YouTube video link, as a deck imported from a creator video carries for its source.</summary>
    public static bool IsVideoUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && ((uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
             && uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase)
             && uri.Query.Contains("v=", StringComparison.Ordinal))
            || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A video with its price. Analysis is null when its deck could not be read.</summary>
public sealed record CreatorVideoCard(CreatorVideo Video, FormatDefinition? Format, DeckAnalysisResult? Analysis)
{
    /// <summary>
    /// Legal in a format the app ranks, known for certain: a list with unrecognised cards
    /// (#87, e.g. written in Portuguese) has no illegal card only because none was read.
    /// </summary>
    public bool IsLegalInAppFormat => Analysis is { IllegalInFormat.Count: 0, FullyPlayableOnArena: true };
}

/// <summary>The cache: what each video's deck is, and when each channel's feed was last asked.</summary>
public sealed record CreatorVideoSnapshot(
    IReadOnlyList<CreatorVideo> Videos,
    IReadOnlyDictionary<string, CreatorFeedState> Feeds)
{
    public static CreatorVideoSnapshot Empty { get; } = new([], new Dictionary<string, CreatorFeedState>());

    public CreatorFeedState? FeedOf(string creator) => Feeds.TryGetValue(creator, out var state) ? state : null;
}
