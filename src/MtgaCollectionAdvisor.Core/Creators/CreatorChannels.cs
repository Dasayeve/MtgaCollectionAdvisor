namespace MtgaCollectionAdvisor.Core.Creators;

/// <summary>
/// The channels the Creators tab lists. The rule for being here: the creator posts Arena
/// decks, puts the list (or a link to it) in the video description, and is still active.
/// The first seven paste the list or link Archidekt, so their decks are read automatically;
/// the next four link sites the app does not read, and get the semi-automatic import.
///
/// This is the list a build falls back to. The list players see is <c>creators.json</c> in the
/// repository (<see cref="CreatorRoster"/>), which changes without a release: edit that first,
/// and bring this one in line when releasing.
///
/// Portuguese channels are listed for Brazilian players and hidden by default from anyone
/// whose Windows is in English. A channel that mixes Magic with other videos is marked so
/// only its Magic ones are listed.
/// </summary>
public static class CreatorChannels
{
    public static IReadOnlyList<CreatorChannel> All { get; } =
    [
        new("ACCILLESS", "UCKivtYJCyZn-uaTr5uAozAg"),
        new("ReikoBoy", "UC4yLxGrrK6J_Y6cszlhguYQ"),
        new("TheBigMammoo", "UCN42nWcSnpJjjBUC637VZwQ"),
        new("ChickensoftheCoast", "UCkIEpOfVFF1uDfbSQ9llEjA"),
        new("Crokeyz", "UCz3pj4DM9MuRWJ5nCCvBe_g"),
        new("mtgdeckcreations", "UCh-psfBt9Not_8nS0IdaUNw"),
        new("MasterOfMonoBlack", "UCn7WPnkOG5946nO7DphBPvg"),
        new("LegenVD", "UCd0kth9C1hqJiaoedeBZ0cQ"),
        new("Ashlizzlle", "UC9HvNU6-MihLe5JGBkJ3DgQ"),
        new("CovertGoBlue", "UC-UZjHl2kZ-6XKBLgbFgGAQ"),
        new("TOTALmtg", "UCTDUGsZDe7_UuLdyxQI_E0w"),

        new("blackmanamtg", "UCuSA9mdqY63ZP1weX1h8MAw", Language: Portuguese),
        new("UMotivo", "UCQxWq7wL4HY40mqbr3f0Z2A", PostsOtherContent: true, Language: Portuguese),
    ];

    private const string Portuguese = "pt";
}
