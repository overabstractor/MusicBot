using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace MusicBot.Desktop;

/// <summary>
/// Opens a Google sign-in page in WebView2. Once the user logs into YouTube,
/// session cookies are exported to a Netscape-format cookies.txt that yt-dlp
/// reads with --cookies. Bypasses the "Sign in to confirm you're not a bot" /
/// HTTP 429 challenge.
/// The window can also do a silent-restore: load youtube.com invisibly and
/// rewrite the cookies.txt with fresh values from the persisted WebView2 profile.
/// </summary>
public partial class YouTubeLoginWindow : Window
{
    // Sign-in page that lands on YouTube after success (so cookies for the
    // .youtube.com domain are issued during the same flow).
    private const string LoginUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
    private const string YouTubeUrl = "https://www.youtube.com/";

    // Cookie names that signal a completed YouTube/Google session
    private static readonly string[] LoginIndicatorCookies =
        { "__Secure-3PSID", "__Secure-1PSID", "SID", "SAPISID", "LOGIN_INFO" };

    // Domains we export cookies from (in this order)
    private static readonly string[] ExportDomains =
    {
        "https://www.youtube.com",
        "https://youtube.com",
        "https://music.youtube.com",
        "https://accounts.google.com",
        "https://google.com",
        "https://www.google.com",
    };

    private enum State { WaitingForLogin, Done }
    private State _state = State.WaitingForLogin;

    private bool _silentRestore;
    private DispatcherTimer? _loginPollTimer;

    public string CookiesFilePath { get; }

    public YouTubeLoginWindow()
    {
        InitializeComponent();

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MusicBot");
        Directory.CreateDirectory(dir);
        CookiesFilePath = Path.Combine(dir, "youtube_cookies.txt");

        WebView.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(dir, "YouTubeWebView"),
        };

        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    public void SuppressAutoNavigation() => _silentRestore = true;

    public async void RestoreSession()
    {
        _silentRestore = true;
        Serilog.Log.Information("YouTube WebView: starting silent session restore…");
        await InitWebViewAsync();
        WebView.CoreWebView2.Navigate(YouTubeUrl);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_silentRestore) return;
        await InitWebViewAsync();
        WebView.CoreWebView2.Navigate(LoginUrl);
        StartSessionPolling();
    }

    private async Task InitWebViewAsync()
    {
        await WebView.EnsureCoreWebView2Async();
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = !_silentRestore;
        WebView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
        WebView.CoreWebView2.IsMuted                                = true;
        WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_silentRestore && _state == State.WaitingForLogin)
            SetStatus("Inicia sesión con tu cuenta de Google.", neutral: true);

        try { await TryCaptureSessionAsync(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "YouTube login window error");
            if (!_silentRestore)
                SetStatus($"Error: {ex.Message}", error: true);
        }
    }

    private async Task TryCaptureSessionAsync()
    {
        if (_state != State.WaitingForLogin) return;

        var cookies = await WebView.CoreWebView2.CookieManager
            .GetCookiesAsync("https://www.youtube.com");

        var hasSession = cookies.Any(c =>
            LoginIndicatorCookies.Contains(c.Name, StringComparer.Ordinal) &&
            !string.IsNullOrWhiteSpace(c.Value));

        if (!hasSession)
        {
            if (_silentRestore)
                Serilog.Log.Warning("YouTube WebView restore: no session cookies — re-login required");
            return;
        }

        StopSessionPolling();
        _state = State.Done;

        // Collect cookies from every relevant domain and write the Netscape file
        var allCookies = new List<CoreWebView2Cookie>();
        foreach (var uri in ExportDomains)
        {
            var list = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(uri);
            allCookies.AddRange(list);
        }

        WriteNetscapeCookieFile(CookiesFilePath, allCookies);
        Serilog.Log.Information("YouTube cookies exported → {Path} ({Count} cookies)",
            CookiesFilePath, allCookies.Count);

        var account = await TryGetAccountLabelAsync();
        MusicBot.AppEvents.NotifyYouTubeCookiesCaptured(CookiesFilePath, account);

        if (_silentRestore)
        {
            Serilog.Log.Information("YouTube WebView session restored silently — cookies refreshed");
            return;
        }

        SetStatus(account != null
            ? $"¡Listo! Conectado como {account}"
            : "¡Listo! Sesión capturada.", success: true);

        await Task.Delay(900);
        Dispatcher.Invoke(Hide);
    }

    /// <summary>
    /// Tries to read the active Google account email/name via in-page JS.
    /// Best-effort: returns null if the page doesn't expose it.
    /// </summary>
    private async Task<string?> TryGetAccountLabelAsync()
    {
        const string script = """
            (function() {
                try {
                    // YouTube exposes the email in the user menu avatar's aria-label
                    const a = document.querySelector('button#avatar-btn, ytd-topbar-menu-button-renderer button');
                    const lbl = a?.getAttribute('aria-label');
                    if (lbl) {
                        const m = lbl.match(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+/);
                        if (m) return m[0];
                    }
                    // Google account chooser meta
                    const m2 = document.querySelector('meta[name="google-signin-client_id"]');
                    if (window.__USER_EMAIL__) return String(window.__USER_EMAIL__);
                } catch(e) {}
                return null;
            })()
            """;
        try
        {
            var raw = await WebView.CoreWebView2.ExecuteScriptAsync(script);
            if (raw == "null" || string.IsNullOrWhiteSpace(raw)) return null;
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch { return null; }
    }

    /// <summary>
    /// Writes cookies in the Netscape HTTP Cookie File format consumed by yt-dlp.
    /// Each line: domain<TAB>flag<TAB>path<TAB>secure<TAB>expiry<TAB>name<TAB>value
    /// </summary>
    private static void WriteNetscapeCookieFile(string path, IEnumerable<CoreWebView2Cookie> cookies)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");
        sb.AppendLine("# Generated by MusicBot — do not edit manually.");
        sb.AppendLine();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cookies)
        {
            if (string.IsNullOrWhiteSpace(c.Value)) continue;

            // Dedupe by (domain|name|path) — multiple ExportDomains overlap
            var key = $"{c.Domain}|{c.Name}|{c.Path}";
            if (!seen.Add(key)) continue;

            // Domain that begins with "." gets includeSubdomains=TRUE in Netscape format.
            // WebView2 returns ".youtube.com" or "youtube.com" — normalize accordingly.
            var includeSub = c.Domain.StartsWith('.') ? "TRUE" : "FALSE";
            var secure     = c.IsSecure ? "TRUE" : "FALSE";

            // Expires: WebView2 gives DateTime.MinValue for session cookies — use a far-future
            // value so yt-dlp doesn't drop them as expired.
            long expires;
            if (c.Expires == DateTime.MinValue || c.Expires.Year < 2000)
                expires = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
            else
                expires = new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds();

            sb.Append(c.Domain).Append('\t')
              .Append(includeSub).Append('\t')
              .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t')
              .Append(secure).Append('\t')
              .Append(expires).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void SetStatus(string text, bool neutral = false, bool success = false, bool error = false)
    {
        if (_silentRestore) return;
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusText.Foreground = error   ? System.Windows.Media.Brushes.OrangeRed
                                  : success ? System.Windows.Media.Brushes.LightGreen
                                  : new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(0x8B, 0x94, 0x9E));
        });
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    private void StartSessionPolling()
    {
        StopSessionPolling();
        _loginPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _loginPollTimer.Tick += async (_, _) =>
        {
            if (_state != State.WaitingForLogin || _silentRestore || WebView.CoreWebView2 == null)
            {
                StopSessionPolling();
                return;
            }
            try { await TryCaptureSessionAsync(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "YouTube session poll error"); }
        };
        _loginPollTimer.Start();
    }

    private void StopSessionPolling()
    {
        _loginPollTimer?.Stop();
        _loginPollTimer = null;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_state == State.Done)
        {
            // Hide instead of close so the WebView2 profile stays warm for silent restore
            e.Cancel = true;
            Hide();
        }
        else
        {
            MusicBot.AppEvents.NotifyYouTubeLoginCancelled();
        }
    }

    public void ResetAndShowLogin()
    {
        StopSessionPolling();
        _state         = State.WaitingForLogin;
        _silentRestore = false;
        if (WebView.CoreWebView2 != null)
        {
            WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.Navigate(LoginUrl);
            StartSessionPolling();
        }
    }

    public void ForceClose()
    {
        StopSessionPolling();
        _state = State.WaitingForLogin;
        Dispatcher.Invoke(Close);
    }

    /// <summary>
    /// Clears the YouTube session by deleting all cookies from the WebView2 profile
    /// AND the cookies.txt file on disk.
    /// </summary>
    public async Task LogoutAsync()
    {
        StopSessionPolling();

        if (WebView.CoreWebView2 != null)
            WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;

        _state         = State.WaitingForLogin;
        _silentRestore = true;

        if (WebView.CoreWebView2 == null)
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
            WebView.CoreWebView2.IsMuted                                = true;
        }

        foreach (var uri in ExportDomains)
        {
            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(uri);
            foreach (var c in cookies)
                WebView.CoreWebView2.CookieManager.DeleteCookie(c);
        }

        try { if (File.Exists(CookiesFilePath)) File.Delete(CookiesFilePath); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "YouTube: could not delete cookies.txt"); }

        Serilog.Log.Information("YouTube WebView: session cleared");
    }
}
