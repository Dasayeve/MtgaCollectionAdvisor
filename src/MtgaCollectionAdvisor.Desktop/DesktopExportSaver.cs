using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MtgaCollectionAdvisor.Core.Export;
using MtgaCollectionAdvisor.UI.Services;

namespace MtgaCollectionAdvisor.Desktop;

/// <summary>
/// Exports in the desktop window: a Save dialog asks where, starting in Downloads with the
/// file's own name, as a browser download would have. Cancel saves nothing.
/// </summary>
public sealed class DesktopExportSaver(AdvisorSession session, ILogger<DesktopExportSaver> log) : IExportSaver
{
    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    public async Task SaveAsync(ExportKind kind)
    {
        if (session.DataExport is not { } export) return;

        try
        {
            if (await export.ExportAsync(kind) is not { } file) return;

            var extension = Path.GetExtension(file.FileName);
            using var dialog = new SaveFileDialog
            {
                FileName = file.FileName,
                InitialDirectory = Downloads(),
                DefaultExt = extension,
                Filter = $"{extension.TrimStart('.').ToUpperInvariant()} file|*{extension}",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(Application.OpenForms.OfType<MainWindow>().FirstOrDefault()) != DialogResult.OK) return;

            await File.WriteAllBytesAsync(dialog.FileName, file.Content);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Export {Kind} failed", kind);
            MessageBox.Show($"The file could not be saved: {ex.Message}", "MTGA Deck Advisor",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>The player's Downloads folder, wherever they moved it; their profile if Windows won't say.</summary>
    private static string Downloads()
    {
        if (SHGetKnownFolderPath(DownloadsFolder, 0, IntPtr.Zero, out var path) == 0) return path;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out string path);
}
