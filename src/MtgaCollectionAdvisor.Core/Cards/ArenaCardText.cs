using System.Text;
using System.Text.RegularExpressions;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Core.Cards;

/// <summary>
/// Turns the columns of MTG Arena's own card database (#101) into what Scryfall would say: a
/// "{1}{W}" cost, a plain name, a rarity. The enum values were read from the file's own
/// <c>Enums</c> table (data version 2026.63): colours 1-5 are W, U, B, R, G; card type 5 is
/// Land; supertype 1 is Basic.
/// </summary>
public static partial class ArenaCardText
{
    private const string LandType = "5";
    private const string BasicSupertype = "1";

    /// <summary>"o1oW" is "{1}{W}", "o(G/W)" is "{G/W}", "o10" is "{10}", "o(U/P)" is "{U/P}".</summary>
    public static string ManaCost(string? oldSchoolManaText)
    {
        if (string.IsNullOrWhiteSpace(oldSchoolManaText)) return "";

        var cost = new StringBuilder();
        foreach (Match symbol in ManaSymbol().Matches(oldSchoolManaText))
        {
            cost.Append('{').Append(symbol.Groups[1].Value.Trim('(', ')')).Append('}');
        }
        return cost.ToString();
    }

    /// <summary>
    /// The mana value of a "{2}{R}" cost (#110): {X} is 0, a number its value, {2/W} 2, any other
    /// symbol 1. Arena's cost of an adventure is the main card's alone and a Room's both halves,
    /// so the sum is the card's mana value, as Scryfall's cmc would say.
    /// </summary>
    public static double ManaValue(string manaCost)
    {
        double total = 0;
        foreach (Match symbol in CostSymbol().Matches(manaCost))
        {
            var text = symbol.Groups[1].Value;
            total += text switch
            {
                "X" or "Y" or "Z" => 0,
                _ when int.TryParse(text, out var generic) => generic,
                _ when text.StartsWith("2/", StringComparison.Ordinal) => 2,
                _ => 1,
            };
        }
        return total;
    }

    /// <summary>
    /// The name as Scryfall writes it. Arena marks names up for its own display: "&lt;nobr&gt;"
    /// around hyphenated words, a sprite before an Alchemy card, which Scryfall calls "A-Name"
    /// (and which must not become the paper card's name), and "///" between split halves.
    /// </summary>
    public static string CleanName(string loc)
    {
        var name = AlchemySprite().Replace(loc, "A-");
        name = Markup().Replace(name, "");
        return name.Replace(" /// ", " // ").Trim();
    }

    /// <summary>Arena's rarity: 1 basic, 2 common, 3 uncommon, 4 rare, 5 mythic.</summary>
    public static CardRarity Rarity(int arenaRarity) => arenaRarity switch
    {
        1 => CardRarity.Basic,
        2 => CardRarity.Common,
        3 => CardRarity.Uncommon,
        4 => CardRarity.Rare,
        5 => CardRarity.Mythic,
        _ => CardRarity.Unknown,
    };

    /// <summary>A Land without the Basic supertype (#61), from the front face's comma-separated enum ids.</summary>
    public static bool IsNonBasicLand(string? types, string? supertypes) =>
        Ids(types).Contains(LandType) && !Ids(supertypes).Contains(BasicSupertype);

    /// <summary>"1,5" is "WG": the colour letters Scryfall uses, in WUBRG order.</summary>
    public static string Colors(IEnumerable<string?> arenaColors)
    {
        var ids = arenaColors.SelectMany(Ids).ToHashSet();
        return string.Concat(new[] { ("1", 'W'), ("2", 'U'), ("3", 'B'), ("4", 'R'), ("5", 'G') }
            .Where(color => ids.Contains(color.Item1))
            .Select(color => color.Item2));
    }

    private static string[] Ids(string? list) =>
        string.IsNullOrWhiteSpace(list) ? [] : list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // One symbol after each 'o': a hybrid or Phyrexian group in brackets, a number, or a letter.
    [GeneratedRegex(@"o(\([^)]*\)|\d+|[A-Z])")]
    private static partial Regex ManaSymbol();

    [GeneratedRegex(@"\{([^}]*)\}")]
    private static partial Regex CostSymbol();

    [GeneratedRegex(@"<sprite[^>]*name=""arena_a""[^>]*>")]
    private static partial Regex AlchemySprite();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Markup();
}
