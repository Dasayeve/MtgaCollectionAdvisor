using Microsoft.AspNetCore.Components;
using MtgaCollectionAdvisor.Core.Export;
using MtgaCollectionAdvisor.UI.Services;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// Exports in the browser: the /export endpoints answer with an attachment, so loading one
/// downloads the file and leaves the page where it is.
/// </summary>
public sealed class WebExportSaver(NavigationManager navigation) : IExportSaver
{
    public Task SaveAsync(ExportKind kind)
    {
        navigation.NavigateTo(PathFor(kind), forceLoad: true);
        return Task.CompletedTask;
    }

    public static string PathFor(ExportKind kind) => kind switch
    {
        ExportKind.CollectionText => "export/collection.txt",
        ExportKind.CollectionJson => "export/collection.json",
        ExportKind.UserDecks => "export/user-decks.zip",
        ExportKind.ArenaDecks => "export/arena-decks.zip",
        ExportKind.AllArenaDecks => "export/arena-decks-all.zip",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an export."),
    };
}
