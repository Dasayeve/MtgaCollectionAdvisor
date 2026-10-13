using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.DependencyInjection;
using MtgaCollectionAdvisor.Core.Hosting;
using MtgaCollectionAdvisor.UI.Components;
using MtgaCollectionAdvisor.UI.Services;

namespace MtgaCollectionAdvisor.Desktop;

/// <summary>
/// The app's window: <see cref="Routes"/>, rendered in-process by WebView2. Closing it closes
/// the app. What makes a web view feel like a browser is taken out here: its right-click menu
/// and a title bar in the wrong theme.
/// </summary>
public sealed class MainWindow : Form
{
    public MainWindow(IServiceProvider services)
    {
        Text = "MTGA Deck Advisor";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        // The size the browser window had (--window-size, in logical pixels), scaled to the screen.
        ClientSize = LogicalToDeviceUnits(new Size(1500, 950));
        StartPosition = FormStartPosition.CenterScreen;
        // Behind the page until it paints, in the theme it will most likely paint in.
        BackColor = WindowsUsesDarkTheme() ? Color.FromArgb(0x16, 0x15, 0x14) : Color.FromArgb(0xfb, 0xfa, 0xf7);

        var view = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot/index.html",
            Services = services,
        };
        view.RootComponents.Add<Routes>("#app");
        view.RootComponents.Add<HeadOutlet>("head::after");
        // The page's own storage (the theme picked, What's new seen) next to the database, like
        // the logs: one folder holds the app's data, and a test copy on its own database keeps
        // its own. WebView2's default is a folder named after the exe, shared by every copy.
        var dataFolder = Path.GetDirectoryName(services.GetRequiredService<AdvisorSession>().DatabasePath)!;
        view.BlazorWebViewInitializing += (_, e) => e.UserDataFolder = Path.Combine(dataFolder, "WebView2");
        // BlazorWebView applies its own WebView2 settings right after this event (the Release
        // build turns the default menu off), so ours go in after them.
        view.BlazorWebViewInitialized += (_, e) => BeginInvoke(() => Configure(e.WebView.CoreWebView2));
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
        UseDarkTitleBar(WindowsUsesDarkTheme());
    }

    private void Configure(CoreWebView2 webView)
    {
        webView.Settings.AreDefaultContextMenusEnabled = true;
        webView.ContextMenuRequested += OnContextMenuRequested;
        webView.WebMessageReceived += OnWebMessageReceived;
    }

    /// <summary>Edit items on text being edited or selected; no menu anywhere else.</summary>
    private static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var target = e.ContextMenuTarget;
        if (!DesktopContextMenu.ShouldShow(target.IsEditable, target.HasSelection))
        {
            e.Handled = true;
            return;
        }

        var items = e.MenuItems;
        var kept = DesktopContextMenu.KeptItems(
            items.Select(item => (item.Name, item.Kind == CoreWebView2ContextMenuItemKind.Separator)).ToList()).ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(i)) items.RemoveAt(i);
        }

        if (items.Count == 0) e.Handled = true;
    }

    /// <summary>The page says which theme it shows (advisor.js), and the title bar follows it.</summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return; // not a string: not ours
        }

        if (message == "theme:dark") UseDarkTitleBar(true);
        else if (message == "theme:light") UseDarkTitleBar(false);
    }

    private void UseDarkTitleBar(bool dark)
    {
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static bool WindowsUsesDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
