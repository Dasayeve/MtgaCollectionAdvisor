using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Analysis;

/// <summary>A column of the visual deck view (#111): its title and its cards, in list order.</summary>
public sealed record DeckColumn(string Title, IReadOnlyList<CardGap> Cards);

/// <summary>
/// The order a deck list is shown in (#110), the way players read a deck: its curve first.
/// Spells by mana value, then non-basic lands, then basic lands, then cards the app doesn't
/// recognise. The name breaks every tie, so the order never depends on how the list was entered.
/// </summary>
public static class DeckListOrder
{
    /// <summary>Mana values from this one up share a column: few decks have more than one or two.</summary>
    private const int HighestOwnColumn = 7;

    public static IEnumerable<CardGap> Order(IEnumerable<CardGap> gaps) => gaps
        .OrderBy(Group)
        // A mana value not yet imported sorts after every known one.
        .ThenBy(g => Group(g) == 0 ? g.ManaValue ?? double.MaxValue : 0)
        .ThenBy(g => g.CardName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A board as the visual view's columns (#111): the commander, spells by mana value (7 and up
    /// together, an unknown value as "?"), lands, then unrecognised cards. Empty columns are left out.
    /// </summary>
    public static IReadOnlyList<DeckColumn> Columns(IEnumerable<CardGap> gaps)
    {
        var ordered = Order(gaps).ToList();
        var columns = new List<DeckColumn>();

        Add("Commander", ordered.Where(g => g.Board == DeckBoard.Commander));
        var rest = ordered.Where(g => g.Board != DeckBoard.Commander).ToList();
        foreach (var spells in rest.Where(g => Group(g) == 0).GroupBy(SpellColumn))
        {
            columns.Add(new DeckColumn(spells.Key, [.. spells]));
        }
        Add("Lands", rest.Where(g => Group(g) is 1 or 2));
        Add("Not recognised", rest.Where(g => Group(g) == 3));
        return columns;

        void Add(string title, IEnumerable<CardGap> cards)
        {
            var list = cards.ToList();
            if (list.Count > 0) columns.Add(new DeckColumn(title, list));
        }
    }

    // GroupBy keeps the order keys first appear in, which Order already sorted by mana value.
    private static string SpellColumn(CardGap gap) => gap.ManaValue switch
    {
        null => "?",
        >= HighestOwnColumn => $"{HighestOwnColumn}+",
        { } value => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // By the front face (#61): a spell with a land on its back is a spell.
    private static int Group(CardGap gap) =>
        !gap.AvailableOnArena ? 3 :
        gap.Rarity == CardRarity.Basic ? 2 :
        gap.IsNonBasicLand ? 1 :
        0;
}
