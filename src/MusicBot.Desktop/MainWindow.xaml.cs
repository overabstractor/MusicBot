using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using SysWinMsg = System.Windows.MessageBox;

namespace MusicBot.Desktop;

/// <summary>
/// Main browser window. Embeds WebView2 pointing to the local Kestrel server.
/// A loading overlay hides until the first navigation completes.
/// </summary>
public partial class MainWindow : Window
{
    private const string DashboardUrl = "http://localhost:3050";

    public MainWindow()
    {
        InitializeComponent();

        // Convert the GDI+ tray icon to a WPF BitmapSource for the title bar and taskbar
        using var gdiIcon = App.CreateIcon();
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            gdiIcon.Handle,
            System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();

            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled            = true;

            // Clear HTTP disk cache on every startup so updated assets are always fetched fresh.
            await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.CacheStorage);

            // Remove the loading overlay once the first page finishes loading
            WebView.CoreWebView2.NavigationCompleted += (_, _) =>
                Dispatcher.Invoke(() => LoadingOverlay.Visibility = Visibility.Collapsed);

            WebView.CoreWebView2.Navigate(DashboardUrl);
        }
        catch (Exception ex)
        {
            SysWinMsg.Show(
                $"No se pudo inicializar el navegador integrado.\n\n" +
                $"Asegúrate de tener instalado el runtime de WebView2.\n\n{ex.Message}",
                "MusicBot — Error de WebView2",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Deletes all cookies for the given URIs from the main WebView2 profile.
    /// Used to clear platform OAuth sessions (Twitch, Kick) when the user forgets their account.
    /// Must be called from the UI thread.
    /// </summary>
    public async Task DeleteCookiesForDomainsAsync(string[] uris)
    {
        if (WebView.CoreWebView2 == null) return;
        foreach (var uri in uris)
        {
            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(uri);
            foreach (var c in cookies)
                WebView.CoreWebView2.CookieManager.DeleteCookie(c);
        }
        Serilog.Log.Information("MainWindow WebView2: cookies deleted for {Uris}", string.Join(", ", uris));
    }
}
