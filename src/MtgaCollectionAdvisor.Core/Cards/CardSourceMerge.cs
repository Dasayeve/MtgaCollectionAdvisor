using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>How each card of a merge came in: with Scryfall's id, matched to an id-less Scryfall print, or from Arena's file alone.</summary>
public sealed record CardMergeCounts(int FromScryfall, int MatchedByNumber, int ArenaOnly);

public sealed record CardMerge(IReadOnlyList<CardInfo> Cards, CardMergeCounts Counts);

/// <summary>
/// Scryfall stays the source of every card's data; MTG Arena's own card database only lends the
/// id Scryfall doesn't publish yet (#101).
/// <list type="bullet">
/// <item>A card Scryfall lists with an arena_id is kept as is, whatever Arena's file says.</item>
/// <item>An Arena card Scryfall has no id for takes the Scryfall print with the same set,
/// collector number and name: its legality, images and name, under Arena's id.</item>
/// <item>An Arena card Scryfall lacks is built from Arena's file, with no image. It is legal in
/// every format only when it is a paper card of a set being released: Scryfall lists the set's
/// Arena prints, none with an id yet. Any other set's missing card (Arena keeps old cards under
/// codes such as TMP or BOK that Scryfall never gave Arena ids), a digital-only or a rebalanced
/// one, is legal nowhere. This lasts until Scryfall catches up: the next import replaces it.</item>
/// </list>
/// </summary>
public static class CardSourceMerge
{
    /// <summary>Scryfall's set and Arena's expansion code, and the collector number as text ("123a" too).</summary>
    public static (string Set, string Number) Key(string set, string collectorNumber) =>
        (set.Trim().ToLowerInvariant(), collectorNumber.Trim());

    public static CardMerge Merge(
        IReadOnlyList<CardInfo> scryfallWithId,
        IReadOnlyDictionary<(string Set, string Number), IReadOnlyList<CardInfo>> scryfallWithoutId,
        IReadOnlyList<ArenaDatabaseCard> arena)
    {
        var cards = new List<CardInfo>(scryfallWithId);
        var known = scryfallWithId.Select(c => c.GrpId).ToHashSet();

        // A set is known when Scryfall has ids in it, under its code or Arena's (they differ for
        // some older sets: DAR is Scryfall's dom), so a missing card of an old set is never
        // assumed legal.
        var knownSets = scryfallWithId.Select(c => c.SetCode.ToLowerInvariant())
            .Concat(arena.Where(c => known.Contains(c.GrpId)).Select(c => c.SetCode.ToLowerInvariant()))
            .ToHashSet();

        // A set being released: Scryfall already lists its Arena prints, without ids. Only these
        // are assumed legal. A set nobody lists that way is not new: Arena's file holds old cards
        // (Lotus Petal under TMP, Umezawa's Jitte under BOK) that must never read as Standard-legal.
        var releasingSets = scryfallWithoutId.Keys.Select(k => k.Set).ToHashSet();

        int matched = 0, arenaOnly = 0;
        foreach (var card in arena)
        {
            if (!known.Add(card.GrpId)) continue;

            if (Print(scryfallWithoutId, card) is { } print)
            {
                cards.Add(print with { GrpId = card.GrpId });
                matched++;
            }
            else
            {
                var set = card.SetCode.ToLowerInvariant();
                var legal = !card.IsDigitalOnly && !card.IsRebalanced && !knownSets.Contains(set) && releasingSets.Contains(set);
                cards.Add(new CardInfo(
                    GrpId: card.GrpId,
                    Name: card.Name,
                    SetCode: card.SetCode.ToLowerInvariant(),
                    ManaCost: card.ManaCost,
                    Colors: card.Colors,
                    Rarity: card.Rarity,
                    StandardLegal: legal,
                    PioneerLegal: legal,
                    IsNonBasicLand: card.IsNonBasicLand,
                    BrawlLegal: legal,
                    StandardBrawlLegal: legal));
                arenaOnly++;
            }
        }

        return new CardMerge(cards, new CardMergeCounts(scryfallWithId.Count, matched, arenaOnly));
    }

    // The same name too: older Arena-only sets (ANA, J21, the Y sets) give one collector number to
    // several different cards, and a card must never take another's data.
    private static CardInfo? Print(
        IReadOnlyDictionary<(string Set, string Number), IReadOnlyList<CardInfo>> scryfallWithoutId, ArenaDatabaseCard card) =>
        scryfallWithoutId.TryGetValue(Key(card.SetCode, card.CollectorNumber), out var prints)
            ? prints.FirstOrDefault(p => SameName(p.Name, card.Name))
            : null;

    private static bool SameName(string scryfall, string arena) =>
        string.Equals(scryfall, arena, StringComparison.OrdinalIgnoreCase)
        || string.Equals(FrontFace(scryfall), FrontFace(arena), StringComparison.OrdinalIgnoreCase);

    private static string FrontFace(string name) => name.Split(" // ")[0];
}
