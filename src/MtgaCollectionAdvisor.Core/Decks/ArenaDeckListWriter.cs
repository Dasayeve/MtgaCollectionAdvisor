using System.Text;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Decks;

/// <summary>
/// Writes a deck back out in the same "Export Deck" text format MTGA's own deckbuilder
/// accepts on import, so a suggested deck can be copied straight into the game.
/// </summary>
public static class ArenaDeckListWriter
{
    private const string FaceSeparator = " // ";

    public static string Write(CandidateDeck deck)
    {
        var sb = new StringBuilder();

        // A Brawl deck (#76): Arena's importer takes the commander from this section only.
        var commanders = deck.Cards.Where(c => c.Board == DeckBoard.Commander).ToList();
        if (commanders.Count > 0)
        {
            sb.AppendLine("Commander");
            foreach (var card in commanders)
            {
                sb.AppendLine($"{card.Quantity} {ArenaName(card.Name)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Deck");
        foreach (var card in deck.Cards.Where(c => c.Board == DeckBoard.Main))
        {
            sb.AppendLine($"{card.Quantity} {ArenaName(card.Name)}");
        }

        var sideboard = deck.Cards.Where(c => c.Board == DeckBoard.Sideboard).ToList();
        if (sideboard.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Sideboard");
            foreach (var card in sideboard)
            {
                sb.AppendLine($"{card.Quantity} {ArenaName(card.Name)}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The name Arena's importer accepts: the front face alone.
    ///
    /// Deck sources name a two-faced card the way Scryfall does, "Front // Back", and
    /// Arena rejects that for adventures and double-faced cards - the line is simply
    /// dropped, so the deck arrives short and the game does not say which card it lost.
    /// The front face works for every layout, split cards included, which is why this
    /// does not need to know one layout from another.
    ///
    /// Safe to do blindly: across the whole Arena pool, no front face is also the name of
    /// a different card, so truncating here cannot pick the wrong one.
    /// </summary>
    public static string ArenaName(string cardName)
    {
        var separator = cardName.IndexOf(FaceSeparator, StringComparison.Ordinal);
        return separator < 0 ? cardName : cardName[..separator];
    }
}
