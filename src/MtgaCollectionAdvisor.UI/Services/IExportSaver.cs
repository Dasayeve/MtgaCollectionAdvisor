using MtgaCollectionAdvisor.Core.Export;

namespace MtgaCollectionAdvisor.UI.Services;

/// <summary>
/// Where an Export menu entry's file goes, which only the host knows (#119): the desktop window
/// asks with a Save dialog, the browser host downloads it.
/// </summary>
public interface IExportSaver
{
    Task SaveAsync(ExportKind kind);
}
