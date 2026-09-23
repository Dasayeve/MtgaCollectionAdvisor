using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MtgaCollectionAdvisor.Core.Creators;

/// <summary>A YouTube channel we pull videos from. The id is verified by hand: resolving
/// a creator's name to a channel lands on a stranger's channel surprisingly often.</summary>
public sealed record CreatorChannel(string Name, string ChannelId);

public static class CreatorChannels
{
    // SPIKE: hand-picked from a survey of channel feeds. Groups 1-2 (deck recoverable
    // automatically) plus a few that only link AetherHub/Moxfield, to exercise that card.
    public static readonly IReadOnlyList<CreatorChannel> All =
    [
        new("ACCILLESS", "UCKivtYJCyZn-uaTr5uAozAg"),
        new("ReikoBoy", "UC4yLxGrrK6J_Y6cszlhguYQ"),
        new("TheBigMammoo", "UCN42nWcSnpJjjBUC637VZwQ"),
        new("ChickensoftheCoast", "UCkIEpOfVFF1uDfbSQ9llEjA"),
        new("Crokeyz", "UCz3pj4DM9MuRWJ5nCCvBe_g"),
        new("mtgdeckcreations", "UCh-psfBt9Not_8nS0IdaUNw"),
        new("LegenVD", "UCd0kth9C1hqJiaoedeBZ0cQ"),
        new("Ashlizzlle", "UC9HvNU6-MihLe5JGBkJ3DgQ"),
        new("CovertGoBlue", "UC-UZjHl2kZ-6XKBLgbFgGAQ"),
        new("TOTALmtg", "UCTDUGsZDe7_UuLdyxQI_E0w")
    ];
}

public sealed record CreatorVideo(
    string Creator,
    string VideoId,
    string Title,
    DateTimeOffset Published,
    string Description)
{
    public string Url => $"https://www.youtube.com/watch?v={VideoId}";
    public string ThumbnailUrl => $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg";
}

/// <summary>
/// Reads a channel's public Atom feed: the latest ~15 uploads with full descriptions,
/// with no API key and no quota.
/// </summary>
public sealed class YouTubeFeedClient(HttpClient http)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    public async Task<IReadOnlyList<CreatorVideo>> FetchAsync(CreatorChannel channel, CancellationToken ct = default)
    {
        var xml = await http.GetStringAsync(
            $"https://www.youtube.com/feeds/videos.xml?channel_id={channel.ChannelId}", ct);

        return XDocument.Parse(xml).Root!.Elements(Atom + "entry")
            .Select(e => new CreatorVideo(
                Creator: channel.Name,
                VideoId: e.Element(Yt + "videoId")?.Value ?? "",
                Title: e.Element(Atom + "title")?.Value ?? "",
                Published: DateTimeOffset.Parse(e.Element(Atom + "published")?.Value ?? "2000-01-01"),
                Description: e.Element(Media + "group")?.Element(Media + "description")?.Value ?? ""))
            .Where(v => v.VideoId.Length > 0)
            .ToList();
    }
}

public enum DeckSourceKind { None, InlineList, Archidekt, External }

/// <summary>Where a video's decklist can be recovered from, if anywhere.</summary>
public sealed record VideoDeckSource(DeckSourceKind Kind, string? Decklist = null, int? ArchidektId = null,
    string? ExternalSite = null, string? ExternalUrl = null)
{
    public static readonly VideoDeckSource None = new(DeckSourceKind.None);
}

public static partial class VideoDeckExtractor
{
    // A real list has dozens of card lines; a description with a handful of "1 thing"
    // lines (timestamps, top-5 lists) is not a deck.
    private const int MinimumCardLines = 15;

    private static readonly (string Site, Regex Pattern)[] ExternalSites =
    [
        ("AetherHub", new Regex(@"https?://(?:www\.)?aetherhub\.com/Deck/\S+", RegexOptions.IgnoreCase)),
        ("Moxfield", new Regex(@"https?://(?:www\.)?moxfield\.com/decks/[\w-]+", RegexOptions.IgnoreCase)),
        ("Untapped", new Regex(@"https?://(?:www\.)?mtga\.untapped\.gg/\S*deck\S*", RegexOptions.IgnoreCase)),
        ("MTGGoldfish", new Regex(@"https?://(?:www\.)?mtggoldfish\.com/(?:deck|archetype)/\S+", RegexOptions.IgnoreCase)),
    ];

    public static VideoDeckSource Extract(string description)
    {
        var lines = description.Split('\n').Select(l => l.Trim()).ToList();
        var cardLines = lines.Where(l => CardLine().IsMatch(l)).ToList();
        if (cardLines.Count >= MinimumCardLines)
        {
            // Keep the section headers so the parser can split main from sideboard.
            var list = lines.Where(l => CardLine().IsMatch(l) || Header().IsMatch(l));
            return new VideoDeckSource(DeckSourceKind.InlineList, Decklist: string.Join('\n', list));
        }

        if (ArchidektLink().Match(description) is { Success: true } a)
        {
            return new VideoDeckSource(DeckSourceKind.Archidekt, ArchidektId: int.Parse(a.Groups[1].Value));
        }

        foreach (var (site, pattern) in ExternalSites)
        {
            if (pattern.Match(description) is { Success: true } m)
            {
                return new VideoDeckSource(DeckSourceKind.External, ExternalSite: site, ExternalUrl: m.Value);
            }
        }

        return VideoDeckSource.None;
    }

    [GeneratedRegex(@"^\d{1,2}\s+[A-Z][^\n]{1,}$")]
    private static partial Regex CardLine();

    [GeneratedRegex(@"^(Deck|Sideboard|Companion|Commander)$", RegexOptions.IgnoreCase)]
    private static partial Regex Header();

    [GeneratedRegex(@"archidekt\.com/decks/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ArchidektLink();
}
