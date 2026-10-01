using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Analysis;

/// <summary>
/// The sets whose packs MTG Arena sells (#84), by Scryfall code, from Wizards' own drop-rates page
/// (https://magic.wizards.com/en/mtgarena/drop-rates). A fixed list because nothing in the card
/// data can say it: Scryfall's <c>booster</c> flag is empty for a new set, and its set type matches
/// Jumpstart, bonus sheets (The Big Score) and old reprints that have no packs. Never Alchemy: the
/// app has no Alchemy format. Add a new set's code when it reaches Arena.
/// </summary>
public static class PackSets
{
    public static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        "xln", "rix", "dom", "m19", "grn", "rna", "war", "m20", "eld", "thb", "iko", "m21", "stx", "neo",
        "ktk", "mkm", "znr", "khm", "mid", "vow", "afr", "snc", "dmu", "one", "mom", "woe", "lci", "otj",
        "blb", "dsk", "fdn", "dft", "tdm", "eoe", "fin", "spm", "tmt", "hob", "tla", "ecl", "sos", "msh",
        "fra", "bro", "mat",
        "klr", "sir", "akr", "pio", // Remastered and Masters
        "ltr", "mh3",               // Scryfall's draft_innovation
    };

    /// <summary>
    /// The set's symbol on Scryfall's image CDN (no rate limit), for a listed set; null otherwise,
    /// and the dialog then shows none. Checked for every listed code against Scryfall's
    /// <c>icon_svg_uri</c>: it is always <c>svgs.scryfall.io/sets/{code}.svg</c>.
    /// </summary>
    public static string? IconUrl(string? setCode) =>
        setCode?.ToLowerInvariant() is { } code && Codes.Contains(code) ? $"https://svgs.scryfall.io/sets/{code}.svg" : null;

    /// <summary>A printing a pack can give: its set is on the list, and it isn't a rebalanced Alchemy card.</summary>
    public static bool HasPacks(CardInfo printing) =>
        Codes.Contains(printing.SetCode.ToLowerInvariant())
        && !printing.Name.StartsWith("A-", StringComparison.Ordinal);
}
