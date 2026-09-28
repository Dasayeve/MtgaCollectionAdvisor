using System.Text.Json;

namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>
/// card-data.json at the repository root (#89): the maintainer's "refresh your cards after this
/// date", set when a new set reaches MTG Arena. Read from GitHub like creators.json (#64), so
/// changing it needs a commit, not a release.
/// </summary>
public static class CardDataFlag
{
    public const string RemoteUrl =
        "https://raw.githubusercontent.com/Dasayeve/MtgaCollectionAdvisor/master/card-data.json";

    /// <summary>The date in <c>refreshCardsAfter</c>; null for no file, no date, or anything unreadable.</summary>
    public static DateTimeOffset? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("refreshCardsAfter", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.TryGetDateTimeOffset(out var at)
                    ? at
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
