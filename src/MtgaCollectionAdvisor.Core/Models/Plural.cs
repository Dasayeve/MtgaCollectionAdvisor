namespace MtgaCollectionAdvisor.Core.Models;

/// <summary>English counts for what the app says: "1 card", "3 cards".</summary>
public static class Plural
{
    /// <summary>The noun as the count needs it: "card" for 1, "cards" otherwise.</summary>
    public static string Noun(int count, string noun) => count == 1 ? noun : noun + "s";

    /// <summary>The count and its noun: "1 land", "24 lands".</summary>
    public static string Of(int count, string noun) => $"{count} {Noun(count, noun)}";
}
