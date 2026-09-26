using System.Text;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Decks;

/// <summary>
/// A deck as Liga Magic's card list page takes it (#62), so a player in Brazil can price the
/// real cards. The app only copies the list and opens the page, where the player is logged in
/// and pastes it: it never reads Liga Magic's prices.
/// </summary>
public static class LigaMagicList
{
    public const string PageUrl = "https://www.ligamagic.com.br/?view=cards/lista";

    /// <summary>
    /// Shown only to players in Brazil (maintainer's decision), with no setting. Either Windows'
    /// home location or its regional format counts: many players run Windows in English with
    /// one of them set to Brazil. Decided locally; nothing about the player is sent anywhere.
    /// </summary>
    public static bool IsAvailable(string? homeLocation, string? regionalFormat) =>
        IsBrazil(homeLocation) || IsBrazil(regionalFormat);

    private static bool IsBrazil(string? regionCode) =>
        string.Equals(regionCode?.Trim(), "BR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "quantity name" per line, commander, mainboard then sideboard, one line per card. Basic lands are
    /// left out, since nobody buys them; the deck given is the one shown, so the non-basic lands
    /// #61 excludes are already gone. Names are the front face, as Arena exports them.
    /// </summary>
    public static string Write(DeckAnalysisResult deck)
    {
        var basics = deck.Gaps.Where(g => g.Rarity == CardRarity.Basic)
            .Select(g => g.CardName).ToHashSet(StringComparer.Ordinal);

        var lines = deck.Deck.Cards
            .Where(c => !basics.Contains(c.Name))
            .OrderBy(c => c.Board switch { DeckBoard.Commander => 0, DeckBoard.Main => 1, _ => 2 })
            .GroupBy(c => ArenaDeckListWriter.ArenaName(c.Name), StringComparer.Ordinal)
            .Select(g => $"{g.Sum(c => c.Quantity)} {g.Key}");

        var sb = new StringBuilder();
        foreach (var line in lines) sb.AppendLine(line);
        return sb.ToString();
    }
}
