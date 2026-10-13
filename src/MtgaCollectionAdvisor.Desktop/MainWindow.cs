using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MtgaCollectionAdvisor.Core.Export;
using MtgaCollectionAdvisor.Web.Components;
using MtgaCollectionAdvisor.Web.Services;

namespace MtgaCollectionAdvisor.Desktop;

/// <summary>
/// The app's own window: the Web project's <see cref="Routes"/>, rendered in-process by a
/// WebView2. Closing it closes the app.
/// </summary>
public sealed class MainWindow : Form
{
    // Where BlazorWebView serves the app from inside WebView2 (0.0.0.1 since .NET 9, not 0.0.0.0).
    private const string AppOrigin = "https://0.0.0.1";

    private readonly AdvisorSession _session;

    public MainWindow(IServiceProvider services)
    {
        _session = services.GetRequiredService<AdvisorSession>();

        Text = "MTGA Deck Advisor";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        // The size the browser window had (--window-size, in logical pixels), scaled to the screen.
        ClientSize = LogicalToDeviceUnits(new Size(1500, 950));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = WindowsUsesDarkTheme() ? Color.FromArgb(0x16, 0x15, 0x14) : Color.FromArgb(0xfb, 0xfa, 0xf7);

        var view = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot/index.html",
            Services = services,
        };
        view.RootComponents.Add<Routes>("#app");
        view.RootComponents.Add<HeadOutlet>("head::after");
        // BlazorWebView adds its own request handler right after this event, and it answers every
        // request under the app's origin (404 for export/*). Handlers run in the order added, and
        // the last answer wins, so ours goes in after it.
        view.BlazorWebViewInitialized += (_, e) => BeginInvoke(() => ServeExports(e.WebView.CoreWebView2));
        Controls.Add(view);
    }

    /// <summary>A second launch asked for this window.</summary>
    public void ShowToFront()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The title bar follows Windows' theme, like the page does until the player picks one.
        var dark = WindowsUsesDarkTheme() ? 1 : 0;
        DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    /// <summary>
    /// The Export menu's links (<c>export/*</c>, with a download attribute) are answered here,
    /// so the components stay as they are; WebView2 saves the file like a browser would.
    /// </summary>
    private void ServeExports(CoreWebView2 webView)
    {
        webView.AddWebResourceRequestedFilter($"{AppOrigin}/export/*", CoreWebView2WebResourceContext.All);
        webView.WebResourceRequested += async (_, e) =>
        {
            var uri = new Uri(e.Request.Uri);
            if (!uri.AbsolutePath.StartsWith("/export/", StringComparison.Ordinal)) return;

            using var deferral = e.GetDeferral();
            var file = await ExportAsync(uri);
            e.Response = file is null
                ? webView.Environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                : webView.Environment.CreateWebResourceResponse(
                    new MemoryStream(file.Content), 200, "OK",
                    $"Content-Type: {file.ContentType}\r\nContent-Disposition: attachment; filename=\"{file.FileName}\"");
        };
    }

    private async Task<ExportFile?> ExportAsync(Uri uri)
    {
        if (_session.DataExport is not { } export) return null;
        var all = uri.Query.Contains("all=true", StringComparison.OrdinalIgnoreCase);
        return uri.AbsolutePath switch
        {
            "/export/collection.txt" => await export.CollectionTextAsync(CancellationToken.None),
            "/export/collection.json" => await export.CollectionJsonAsync(CancellationToken.None),
            "/export/user-decks.zip" => await export.UserDecksAsync(CancellationToken.None),
            "/export/arena-decks.zip" => await export.ArenaDecksAsync(all, CancellationToken.None),
            _ => null,
        };
    }

    private static bool WindowsUsesDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
