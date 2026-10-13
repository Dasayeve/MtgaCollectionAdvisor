namespace MtgaCollectionAdvisor.Core.Hosting;

/// <summary>
/// The right-click menu in the desktop window (#119). A web page's menu (Back, Reload, Save as,
/// Print) is what makes a window feel like a browser, so there is none, except where text is
/// edited or selected: there Copy and Paste are what a player reaches for (pasting a deck list
/// into Import deck), and only the edit items are kept.
/// </summary>
public static class DesktopContextMenu
{
    // WebView2's names for the default menu's edit items.
    private static readonly HashSet<string> EditItems = new(StringComparer.Ordinal)
    {
        "undo", "redo", "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll",
    };

    public static bool ShouldShow(bool isEditable, bool hasSelection) => isEditable || hasSelection;

    public static bool Keeps(string itemName) => EditItems.Contains(itemName);

    /// <summary>
    /// Which of the default menu's items stay, by index in order: the edit items, and the
    /// separators between groups of them; none at either end or two in a row.
    /// </summary>
    public static IReadOnlyList<int> KeptItems(IReadOnlyList<(string Name, bool IsSeparator)> items)
    {
        var kept = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].IsSeparator)
            {
                if (kept.Count > 0 && !items[kept[^1]].IsSeparator) kept.Add(i);
            }
            else if (Keeps(items[i].Name))
            {
                kept.Add(i);
            }
        }

        if (kept.Count > 0 && items[kept[^1]].IsSeparator) kept.RemoveAt(kept.Count - 1);
        return kept;
    }
}
