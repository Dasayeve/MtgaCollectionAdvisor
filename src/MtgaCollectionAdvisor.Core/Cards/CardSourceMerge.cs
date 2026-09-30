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
/// <item>An Arena card Scryfall lacks is built from Arena's file, with no image. In each format it
/// is legal as its set is: when most of the set's Arena prints Scryfall lists without an id are
/// legal there, as a set being released is (Reality Fracture's 459 are all Standard-legal). Any
/// other card is legal nowhere: an old set's (Arena keeps old cards under codes such as TMP or
/// PZA, whose prints Scryfall doesn't call legal), a digital-only or a rebalanced one. This lasts
/// until Scryfall catches up: the next import replaces it.</item>
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

        // Per set, the legality most of its id-less Arena prints have on Scryfall. A set being
        // released has them all legal; Arena's old cards (Lotus Petal under TMP, Umezawa's Jitte
        // under PZA) belong to sets with none, or none legal, and must never read as legal.
        var setLegality = scryfallWithoutId
            .SelectMany(entry => entry.Value.Select(print => (entry.Key.Set, print)))
            .GroupBy(p => p.Set, p => p.print)
            .ToDictionary(g => g.Key, g => SetLegality.Of(g.ToList()));

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
                var legal = !card.IsDigitalOnly && !card.IsRebalanced && !knownSets.Contains(set)
                    && setLegality.TryGetValue(set, out var ofSet)
                    ? ofSet
                    : SetLegality.None;
                cards.Add(new CardInfo(
                    GrpId: card.GrpId,
                    Name: card.Name,
                    SetCode: card.SetCode.ToLowerInvariant(),
                    ManaCost: card.ManaCost,
                    Colors: card.Colors,
                    Rarity: card.Rarity,
                    StandardLegal: legal.Standard,
                    PioneerLegal: legal.Pioneer,
                    IsNonBasicLand: card.IsNonBasicLand,
                    BrawlLegal: legal.Brawl,
                    StandardBrawlLegal: legal.StandardBrawl));
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

/// <summary>A set's legality in each format: legal where most of its prints are (#101).</summary>
internal sealed record SetLegality(bool Standard, bool Pioneer, bool Brawl, bool StandardBrawl)
{
    public static readonly SetLegality None = new(false, false, false, false);

    public static SetLegality Of(IReadOnlyList<CardInfo> prints)
    {
        bool Most(Func<CardInfo, bool> legal) => prints.Count(legal) * 2 > prints.Count;
        return new SetLegality(Most(p => p.StandardLegal), Most(p => p.PioneerLegal), Most(p => p.BrawlLegal), Most(p => p.StandardBrawlLegal));
    }
}
