using System.Text.RegularExpressions;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Decks;

/// <summary>
/// Parses the standard MTGA "Export Deck" text format (also produced/accepted by most
/// deckbuilding sites), e.g.:
///
///   Deck
///   4 Lightning Bolt (STA) 42
///   2 Mountain (ANB) 250
///
///   Sideboard
///   2 Abrade (BLB) 100
///
/// The trailing "(SET) collector-number" is optional and stripped when present, since
/// cards are matched by name against the local card database.
/// </summary>
public static partial class ArenaDeckListParser
{
    [GeneratedRegex(@"^(?<qty>\d+)\s+(?<name>.+?)(?:\s+\([A-Za-z0-9]{2,6}\)\s*[\w\-★]*)?$")]
    private static partial Regex CardLineRegex();

    public static IReadOnlyList<DeckCardRef> Parse(string text)
    {
        var result = new List<DeckCardRef>();
        var board = DeckBoard.Main;
        var inCompanion = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.Equals("Deck", StringComparison.OrdinalIgnoreCase))
            {
                board = DeckBoard.Main;
                inCompanion = false;
                continue;
            }

            if (line.Equals("Commander", StringComparison.OrdinalIgnoreCase))
            {
                board = DeckBoard.Commander;
                inCompanion = false;
                continue;
            }

            if (line.Equals("Sideboard", StringComparison.OrdinalIgnoreCase))
            {
                board = DeckBoard.Sideboard;
                inCompanion = false;
                continue;
            }

            // Arena's export lists the companion in its own section *and* again in the
            // sideboard, where it actually lives. Counting the section too made a 60-card
            // deck read as 61 in the mainboard.
            if (line.Equals("Companion", StringComparison.OrdinalIgnoreCase))
            {
                inCompanion = true;
                continue;
            }

            if (inCompanion) continue;

            var match = CardLineRegex().Match(line);
            if (!match.Success) continue;

            var quantity = int.Parse(match.Groups["qty"].Value);
            var name = match.Groups["name"].Value.Trim();
            result.Add(new DeckCardRef(name, quantity, board));
        }

        return result;
    }
}
