using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Analysis;

/// <summary>
/// The order a deck list is shown in (#110), the way players read a deck: its curve first.
/// Spells by mana value, then non-basic lands, then basic lands, then cards the app doesn't
/// recognise. The name breaks every tie, so the order never depends on how the list was entered.
/// </summary>
public static class DeckListOrder
{
    public static IEnumerable<CardGap> Order(IEnumerable<CardGap> gaps) => gaps
        .OrderBy(Group)
        // A mana value not yet imported sorts after every known one.
        .ThenBy(g => Group(g) == 0 ? g.ManaValue ?? double.MaxValue : 0)
        .ThenBy(g => g.CardName, StringComparer.OrdinalIgnoreCase);

    // By the front face (#61): a spell with a land on its back is a spell.
    private static int Group(CardGap gap) =>
        !gap.AvailableOnArena ? 3 :
        gap.Rarity == CardRarity.Basic ? 2 :
        gap.IsNonBasicLand ? 1 :
        0;
}
