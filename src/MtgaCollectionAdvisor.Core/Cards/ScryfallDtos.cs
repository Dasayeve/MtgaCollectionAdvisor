using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MtgaCollectionAdvisor.Core.Cards;

internal sealed class ScryfallBulkDataEntry
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    /// <summary>
    /// Scryfall retired the old single-JSON-array "download_uri" field; bulk data is
    /// now published as gzip-compressed JSON Lines (one card object per line).
    /// </summary>
    [JsonPropertyName("jsonl_download_uri")] public string JsonlDownloadUri { get; set; } = "";

    /// <summary>
    /// When Scryfall generated this file. It changes every 12 hours, prices included, so it
    /// is no sign of new cards by itself; #89 only asks whether it is later than a given date.
    /// </summary>
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
}

internal sealed class ScryfallBulkDataResponse
{
    [JsonPropertyName("data")] public List<ScryfallBulkDataEntry> Data { get; set; } = [];
}

/// <summary>Only the size the app shows; Scryfall also lists small, large, png and crops.</summary>
internal sealed class ScryfallImageUris
{
    [JsonPropertyName("normal")] public string? Normal { get; set; }
}

internal sealed class ScryfallCardFace
{
    [JsonPropertyName("mana_cost")] public string? ManaCost { get; set; }
    [JsonPropertyName("colors")] public List<string>? Colors { get; set; }
    [JsonPropertyName("image_uris")] public ScryfallImageUris? ImageUris { get; set; }
    [JsonPropertyName("type_line")] public string? TypeLine { get; set; }
}

internal sealed class ScryfallCard
{
    [JsonPropertyName("arena_id")] public int? ArenaId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("set")] public string Set { get; set; } = "";
    [JsonPropertyName("type_line")] public string TypeLine { get; set; } = "";
    [JsonPropertyName("mana_cost")] public string? ManaCost { get; set; }
    [JsonPropertyName("colors")] public List<string>? Colors { get; set; }
    [JsonPropertyName("rarity")] public string Rarity { get; set; } = "";
    [JsonPropertyName("legalities")] public Dictionary<string, string> Legalities { get; set; } = [];
    [JsonPropertyName("card_faces")] public List<ScryfallCardFace>? CardFaces { get; set; }
    [JsonPropertyName("image_uris")] public ScryfallImageUris? ImageUris { get; set; }

    public string EffectiveManaCost =>
        !string.IsNullOrEmpty(ManaCost) ? ManaCost :
        CardFaces?.FirstOrDefault()?.ManaCost ?? "";

    public string EffectiveColors()
    {
        if (Colors is { Count: > 0 }) return string.Concat(Colors);
        if (CardFaces is null) return "";
        var union = CardFaces
            .SelectMany(f => f.Colors ?? [])
            .Distinct()
            .OrderBy(c => c);
        return string.Concat(union);
    }

    /// <summary>
    /// The card's "normal" image (488x680), and its back face's for a double-faced card. A
    /// card with one image for all its faces (split, adventure, flip) has it at the top level;
    /// a double-faced card has none there and one per face instead.
    /// </summary>
    public (string? Front, string? Back) NormalImageUrls()
    {
        if (Https(ImageUris?.Normal) is { } single) return (single, null);

        var faces = (CardFaces ?? []).Select(f => Https(f.ImageUris?.Normal)).ToList();
        return faces.Count > 0 && faces[0] is { } front
            ? (front, faces.Count > 1 ? faces[1] : null)
            : (null, null);
    }

    // Stored and later put in an <img src>: anything but an https URL is dropped.
    private static string? Https(string? url) =>
        url is not null && url.StartsWith("https://", StringComparison.Ordinal) ? url : null;

    /// <summary>
    /// A land that costs a wildcard: its front face is a Land and not Basic (#61). The front
    /// face decides, so a spell with a land on its back, or a creature that transforms into a
    /// land, is not one: it is played, and crafted, as the spell.
    /// </summary>
    public bool IsNonBasicLand() => IsNonBasicLandType(FrontTypeLine);

    /// <summary>
    /// A basic land costs no wildcard. The "Basic" supertype decides, so the snow-covered basics
    /// ("Basic Snow Land — Island") and Wastes are basic too, as the maintainer wants them
    /// treated, not priced as the commons Scryfall prints them as.
    /// </summary>
    public bool IsBasicLand() => IsBasicLandType(FrontTypeLine);

    private string FrontTypeLine =>
        CardFaces is { Count: > 0 } faces && faces[0].TypeLine is { } front ? front : TypeLine.Split(" // ")[0];

    // Only the types before the dash count ("Land Creature — Forest Dryad"), and each as a whole
    // word, so a subtype that merely contains the letters does not match.
    internal static bool IsNonBasicLandType(string? typeLine) =>
        Types(typeLine) is { } types && LandWord.IsMatch(types) && !BasicWord.IsMatch(types);

    internal static bool IsBasicLandType(string? typeLine) =>
        Types(typeLine) is { } types && LandWord.IsMatch(types) && BasicWord.IsMatch(types);

    private static string? Types(string? typeLine) =>
        string.IsNullOrWhiteSpace(typeLine) ? null : typeLine.Split('—')[0];

    private static readonly Regex LandWord = new(@"\bLand\b", RegexOptions.Compiled);
    private static readonly Regex BasicWord = new(@"\bBasic\b", RegexOptions.Compiled);

    public bool IsLegal(string formatKey) =>
        Legalities.TryGetValue(formatKey, out var status) && status == "legal";
}
