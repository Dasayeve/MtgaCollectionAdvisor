using MtgaCollectionAdvisor.Core.Hosting;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>The desktop window's right-click menu (#119): edit items on text, nothing elsewhere.</summary>
public class DesktopContextMenuTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ShouldShow_Should_OnlyAllow_EditableOrSelection(bool isEditable, bool hasSelection, bool expected)
    {
        Assert.Equal(expected, DesktopContextMenu.ShouldShow(isEditable, hasSelection));
    }

    [Theory]
    [InlineData("undo")]
    [InlineData("redo")]
    [InlineData("cut")]
    [InlineData("copy")]
    [InlineData("paste")]
    [InlineData("pasteAndMatchStyle")]
    [InlineData("selectAll")]
    public void Keeps_Should_KeepEditItems(string item)
    {
        Assert.True(DesktopContextMenu.Keeps(item));
    }

    [Theory]
    [InlineData("inspectElement")]
    [InlineData("print")]
    [InlineData("reload")]
    [InlineData("saveAs")]
    [InlineData("back")]
    [InlineData("forward")]
    [InlineData("other")]
    [InlineData("Copy")]
    public void Keeps_Should_DropTheRest(string item)
    {
        Assert.False(DesktopContextMenu.Keeps(item));
    }

    // The three menus WebView2 offered in the desktop window (#119), as it names their items.
    private static readonly (string, bool)[] PageMenu =
    [
        ("back", false), ("forward", false), ("reload", false), ("other", true), ("saveAs", false), ("print", false),
        ("other", true), ("moreTools", false), ("other", true), ("inspectElement", false),
    ];

    private static readonly (string, bool)[] SelectionMenu =
    [
        ("copy", false), ("copyLinkToHighlight", false), ("print", false), ("other", true), ("moreTools", false),
        ("other", true), ("inspectElement", false),
    ];

    private static readonly (string, bool)[] TextFieldMenu =
    [
        ("emoji", false), ("other", true), ("undo", false), ("redo", false), ("other", true), ("cut", false),
        ("copy", false), ("paste", false), ("pasteAndMatchStyle", false), ("selectAll", false), ("other", true),
        ("other", false), ("other", true), ("moreTools", false), ("other", true), ("inspectElement", false),
    ];

    [Fact]
    public void KeptItems_Should_KeepTheEditGroups_WithOneSeparatorBetween()
    {
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9], DesktopContextMenu.KeptItems(TextFieldMenu));
    }

    [Fact]
    public void KeptItems_Should_KeepOnlyCopy_OnASelection()
    {
        Assert.Equal([0], DesktopContextMenu.KeptItems(SelectionMenu));
    }

    [Fact]
    public void KeptItems_Should_KeepNothing_OnThePage()
    {
        // ShouldShow already says no here; nothing would be left anyway.
        Assert.Empty(DesktopContextMenu.KeptItems(PageMenu));
    }
}
