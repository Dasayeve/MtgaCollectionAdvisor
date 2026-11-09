using System.Text;
using MtgaCollectionAdvisor.Core.Arena;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Export;

/// <summary>
/// A deck saved in Arena, written back in Arena's own export format - Commander,
/// Companion, Deck and Sideboard sections, each only when it has cards - so the file
/// pastes straight into the game or into <b>Import deck</b>.
/// </summary>
public static class ArenaDeckTextWriter
{
    public static string Write(ArenaDeck deck, IReadOnlyDictionary<int, string> names)
    {
        var unknown = 0;
        var sections = new List<string>();

        void Section(string title, IReadOnlyList<ArenaCard> cards)
        {
            if (cards.Count == 0) return;
            var sb = new StringBuilder().AppendLine(title);
            foreach (var card in cards)
            {
                if (!names.TryGetValue(card.GrpId, out var name))
                {
                    unknown += card.Quantity;
                    continue;
                }
                sb.AppendLine($"{card.Quantity} {ArenaDeckListWriter.ArenaName(name)}");
            }
            sections.Add(sb.ToString());
        }

        Section("Commander", deck.Commander);
        Section("Companion", deck.Companion);
        Section("Deck", deck.Main);
        Section("Sideboard", deck.Sideboard);

        // Arena's importer and ArenaDeckListParser both skip a line they cannot read, so
        // the note costs nothing on re-import and tells the user why a card is missing.
        var header = unknown > 0
            ? $"// {Plural.Of(unknown, "card")} not in the card database - run Update cards and export again{Environment.NewLine}"
            : "";

        return header + string.Join(Environment.NewLine, sections);
    }

    /// <summary>The folder a deck goes in: its Arena format, lowercased.</summary>
    public static string FolderFor(string format) =>
        string.IsNullOrWhiteSpace(format) || format.Equals("Unspecified", StringComparison.OrdinalIgnoreCase)
            ? "unspecified"
            : UserDeckExportWriter.SafeFileName(format.ToLowerInvariant());
}
