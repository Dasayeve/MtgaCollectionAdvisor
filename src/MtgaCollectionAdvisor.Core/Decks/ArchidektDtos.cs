using System.Text.Json.Serialization;

namespace MtgaCollectionAdvisor.Core.Decks;

// Every collection property here is nullable on purpose: Archidekt sends explicit
// nulls for empty lists, and System.Text.Json writes those over property initializers.
internal sealed class ArchidektSearchResponse
{
    [JsonPropertyName("results")] public List<ArchidektDeckSummary>? Results { get; set; }
    [JsonPropertyName("next")] public string? Next { get; set; }
}

internal sealed class ArchidektDeckSummary
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("viewCount")] public int ViewCount { get; set; }
    [JsonPropertyName("private")] public bool Private { get; set; }
    [JsonPropertyName("unlisted")] public bool Unlisted { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ArchidektDeckDetail
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("categories")] public List<ArchidektCategory>? Categories { get; set; }
    [JsonPropertyName("cards")] public List<ArchidektCardEntry>? Cards { get; set; }
}

/// <summary>A deck's own category. Null when Archidekt leaves the flag out: count the card.</summary>
internal sealed class ArchidektCategory
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("includedInDeck")] public bool? IncludedInDeck { get; set; }
}

internal sealed class ArchidektCardEntry
{
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("card")] public ArchidektCardRef? Card { get; set; }
}

internal sealed class ArchidektCardRef
{
    [JsonPropertyName("oracleCard")] public ArchidektOracleCard? OracleCard { get; set; }
}

internal sealed class ArchidektOracleCard
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}
