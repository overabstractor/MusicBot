using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace MusicBot.Desktop;

/// <summary>
/// Opens TikTok's login page in an embedded WebView2.
/// After login it stays alive as a hidden window so it can send chat messages
/// by executing fetch() inside the authenticated browser context.
/// </summary>
public partial class TikTokLoginWindow : Window
{
    private const string LoginUrl   = "https://www.tiktok.com/login";
    private const string ProfileUrl = "https://www.tiktok.com/profile/";
    private const string HomeUrl    = "https://www.tiktok.com";

    // State machine: controls what OnNavigationCompleted does next
    private enum State { WaitingForLogin, WaitingForProfileRedirect, Done }
    private State _state = State.WaitingForLogin;

    private static readonly System.Windows.Media.SolidColorBrush _brushMuted =
        new(System.Windows.Media.Color.FromRgb(0x8B, 0x94, 0x9E));

    // Known non-username path segments that TikTok can redirect to after login
    private static readonly HashSet<string> _ignoredHandles = new(StringComparer.OrdinalIgnoreCase)
    {
        "explore", "creators", "live", "following", "foryou", "for-you",
        "friends", "messages", "notifications", "profile", "login",
        "home", "discover", "search", "upload", "fyp",
    };

    private bool _silentRestore;

    // Polls for sessionid cookie while on the login page — detects QR login completion
    private DispatcherTimer? _loginPollTimer;

    // Cookies captured right after login, held until username is resolved
    private string? _pendingCookieString;

    public TikTokLoginWindow()
    {
        InitializeComponent();

        // Stable user-data folder so TikTok cookies survive builds/restarts
        WebView.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MusicBot", "TikTokWebView"),
        };

        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_silentRestore) return;
        SetStatus("Iniciando navegador…", neutral: true);
        await InitWebViewAsync(contextMenus: true);
        ShowOverlay("Cargando TikTok…");
        WebView.CoreWebView2.Navigate(LoginUrl);
        StartSessionPolling();
    }

    /// <summary>Must be called BEFORE Show() to prevent OnLoaded from navigating to the login page.</summary>
    public void SuppressAutoNavigation() => _silentRestore = true;

    public async void RestoreSession()
    {
        _silentRestore = true;
        Serilog.Log.Information("TikTok WebView: starting silent session restore…");
        await InitWebViewAsync(contextMenus: false);
        WebView.CoreWebView2.Navigate(HomeUrl);
    }

    private async Task InitWebViewAsync(bool contextMenus)
    {
        await WebView.EnsureCoreWebView2Async();
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = contextMenus;
        WebView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
        WebView.CoreWebView2.IsMuted                                = true;
        WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
    }

    // ── Navigation state machine ──────────────────────────────────────────────

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Dispatcher.Invoke(() => LoadingOverlay.Visibility = Visibility.Collapsed);

        if (!_silentRestore && _state == State.WaitingForLogin)
            SetStatus("Inicia sesión con tu cuenta de TikTok.", neutral: true);

        try
        {
            switch (_state)
            {
                case State.WaitingForLogin:
                    await HandleWaitingForLogin();
                    break;

                case State.WaitingForProfileRedirect:
                    await HandleProfileRedirect();
                    break;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "TikTok login window error in state {State}", _state);
            if (!_silentRestore)
                SetStatus($"Error: {ex.Message}", error: true);
        }
    }

    // ── State: waiting for user to log in ─────────────────────────────────────

    private async Task HandleWaitingForLogin()
    {
        // Guard against re-entrancy (timer + NavigationCompleted can both call this)
        if (_state != State.WaitingForLogin) return;

        var cookies = await WebView.CoreWebView2.CookieManager
            .GetCookiesAsync("https://www.tiktok.com");

        var sessionId = cookies.FirstOrDefault(c =>
            c.Name.Equals("sessionid", StringComparison.OrdinalIgnoreCase));

        if (sessionId == null || string.IsNullOrWhiteSpace(sessionId.Value))
        {
            if (_silentRestore)
                Serilog.Log.Warning(
                    "TikTok WebView restore: no sessionid cookie — session expired, re-login required");
            return;
        }

        // Session found — stop polling and register services
        StopSessionPolling();

        // Session found — register chat sender, room ID resolver, and webcast fetcher
        MusicBot.AppEvents.RegisterTikTokWebViewSender(SendChatViaWebViewAsync);
        MusicBot.AppEvents.RegisterTikTokRoomIdResolver(ResolveRoomIdViaWebViewAsync);
        MusicBot.AppEvents.RegisterTikTokWebcastFetcher(GetWebcastDataViaWebViewAsync);

        if (_silentRestore)
        {
            _state = State.Done;
            Serilog.Log.Information("TikTok WebView session restored silently — chat sender ready");
            return;
        }

        // Capture cookies for later
        _pendingCookieString = string.Join("; ", cookies
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => $"{c.Name}={c.Value}"));

        SetStatus("Sesión detectada — identificando usuario…", neutral: true);

        // Fast path: extract username via JS — retry up to 3 times with short delays
        // (TikTok's SPA state may not be initialised the instant the session cookie appears)
        string? username = null;
        for (int attempt = 0; attempt < 3 && username == null; attempt++)
        {
            if (attempt > 0) await Task.Delay(600);
            username = await TryGetUsernameViaJsAsync(cookies);
        }

        if (username != null)
        {
            _state = State.Done;
            Serilog.Log.Information("TikTok username via JS: {Username}", username);
            MusicBot.AppEvents.NotifyTikTokCookiesCaptured(_pendingCookieString, username);
            SetStatus($"¡Listo! Sesión iniciada como @{username}", success: true);
            await Task.Delay(1000);
            Dispatcher.Invoke(Hide);
        }
        else
        {
            // Fallback: navigate to /profile/ and let the redirect reveal the handle
            _state = State.WaitingForProfileRedirect;
            Dispatcher.Invoke(() =>
            {
                ShowOverlay("Verificando perfil…");
                WebView.CoreWebView2.Navigate(ProfileUrl);
            });
        }
    }

    // ── State: reading username from /profile/ redirect ───────────────────────

    private async Task HandleProfileRedirect()
    {
        _state = State.Done;

        // The final URL after the redirect is /@username — extract it directly
        var source   = WebView.CoreWebView2.Source ?? "";
        var username = ExtractHandleFromUrl(source);

        if (username != null)
        {
            Serilog.Log.Information("TikTok username from profile redirect URL: {Username}", username);
        }
        else
        {
            // Fallback: re-read cookies now that we're on the main site
            // (TikTok may set more cookies after the session page loads)
            var cookies = await WebView.CoreWebView2.CookieManager
                .GetCookiesAsync("https://www.tiktok.com");

            foreach (var name in new[] { "username", "user_unique_id", "unique_id" })
            {
                var ck = cookies.FirstOrDefault(c =>
                    c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(ck?.Value))
                {
                    username = Uri.UnescapeDataString(ck.Value).TrimStart('@');
                    Serilog.Log.Information(
                        "TikTok username from cookie '{Name}': {Username}", name, username);
                    break;
                }
            }
        }

        Serilog.Log.Information("TikTok login complete — user={Username}",
            username ?? "(not detected)");

        MusicBot.AppEvents.NotifyTikTokCookiesCaptured(_pendingCookieString ?? "", username);

        SetStatus(username != null
            ? $"¡Listo! Sesión iniciada como @{username}"
            : "Sesión capturada. Cerrando…", success: true);

        await Task.Delay(1000);
        Dispatcher.Invoke(Hide);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to extract the logged-in TikTok username via JavaScript without navigating to another page.
    /// Checks (in order): current URL, TikTok's in-page state, passport API, and cookies.
    /// </summary>
    private async Task<string?> TryGetUsernameViaJsAsync(IReadOnlyList<CoreWebView2Cookie> cookies)
    {
        // 1. Fast: check cookies directly (some TikTok regions set a 'unique_id' cookie)
        foreach (var name in new[] { "unique_id", "user_unique_id", "username" })
        {
            var ck = cookies.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(ck?.Value))
            {
                var val = Uri.UnescapeDataString(ck.Value).TrimStart('@');
                if (!string.IsNullOrWhiteSpace(val) && !_ignoredHandles.Contains(val))
                {
                    Serilog.Log.Information("TikTok username from cookie '{Name}': {Username}", name, val);
                    return val;
                }
            }
        }

        // 2. Try JavaScript: current URL handle + in-page state + multiple APIs
        const string script = """
            (async function() {
                try {
                    // a. Current URL may already be /@username after login
                    const urlM = location.href.match(/tiktok\.com\/@([A-Za-z0-9._]+)/);
                    if (urlM?.[1]) return urlM[1];

                    // b. TikTok SPA global state (multiple known shapes)
                    const sigi = window._SIGI_STATE || window.SIGI_STATE;
                    if (sigi) {
                        const u = sigi?.LoginReducer?.loginUser?.uniqueId
                               || sigi?.UserModule?.loginUserInfo?.uniqueId
                               || sigi?.UserPage?.uniqueId;
                        if (u) return String(u);
                    }

                    // c. Next.js / SSR initial data (newer TikTok versions)
                    if (window.__NEXT_DATA__) {
                        const nd = window.__NEXT_DATA__;
                        const u = nd?.props?.pageProps?.userInfo?.user?.uniqueId
                               || nd?.props?.pageProps?.loginUser?.uniqueId;
                        if (u) return String(u);
                    }

                    // d. TikTok passport API (internal, available when logged in)
                    try {
                        const r = await fetch('/api/passport/account/info/v2/?aid=1988', { credentials: 'include' });
                        if (r.ok) {
                            const d = await r.json();
                            const u = d?.data?.user_info?.username || d?.data?.user_info?.unique_id;
                            if (u) return String(u);
                        }
                    } catch(e) {}

                    // e. Webcast user API (also authenticated)
                    try {
                        const r2 = await fetch('https://webcast.tiktok.com/webcast/user/me/?aid=1988', { credentials: 'include' });
                        if (r2.ok) {
                            const d2 = await r2.json();
                            const u2 = d2?.data?.user?.uniqueId || d2?.data?.uniqueId;
                            if (u2) return String(u2);
                        }
                    } catch(e) {}

                    // f. TikTok node/detail API used by the web app
                    try {
                        const r3 = await fetch('/api/user/detail/?aid=1988&secUid=&uniqueId=', { credentials: 'include' });
                        if (r3.ok) {
                            const d3 = await r3.json();
                            const u3 = d3?.userInfo?.user?.uniqueId;
                            if (u3) return String(u3);
                        }
                    } catch(e) {}
                } catch(e) {}
                return null;
            })()
            """;

        try
        {
            var raw = await WebView.CoreWebView2.ExecuteScriptAsync(script);
            if (raw == "null" || string.IsNullOrWhiteSpace(raw)) return null;
            var handle = JsonSerializer.Deserialize<string>(raw)?.TrimStart('@');
            if (!string.IsNullOrWhiteSpace(handle) && !_ignoredHandles.Contains(handle))
                return handle;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "TikTok JS username extraction failed (will fall back to /profile/)");
        }

        return null;
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
                                  :           _brushMuted;
        });
    }

    private void ShowOverlay(string label)
    {
        if (_silentRestore) return;
        Dispatcher.Invoke(() =>
        {
            OverlayLabel.Text  = label;
            LoadingOverlay.Visibility = Visibility.Visible;
        });
    }

    private static string? ExtractHandleFromUrl(string url)
    {
        // Matches https://www.tiktok.com/@handle or https://www.tiktok.com/@handle/...
        var m = Regex.Match(url, @"tiktok\.com/@([^/?&#]+)");
        if (!m.Success) return null;
        var handle = m.Groups[1].Value;
        return _ignoredHandles.Contains(handle) ? null : handle;
    }

    // ── Fetch raw webcast Protobuf bytes via authenticated WebView ────────────

    /// <summary>
    /// Calls TikTok's webcast/im/fetch API from inside the authenticated browser context.
    /// TikTok's own JS SDK adds X-Bogus and other anti-bot signing automatically,
    /// so this succeeds where server-side HTTP calls return empty body.
    /// Returns (Protobuf bytes, x-set-tt-cookie header value).
    /// </summary>
    private async Task<(byte[] Bytes, string CookieHeader)> GetWebcastDataViaWebViewAsync(string roomId)
    {
        var roomIdJson = JsonSerializer.Serialize(roomId);
        var script = $$"""
            (async function() {
                try {
                    const roomId = {{roomIdJson}};
                    const params = new URLSearchParams({
                        aid: '1988',
                        app_language: 'en-US',
                        app_name: 'tiktok_web',
                        browser_language: navigator.language || 'en-US',
                        browser_name: 'Mozilla',
                        browser_online: String(navigator.onLine),
                        browser_platform: navigator.platform || 'Win32',
                        browser_version: navigator.userAgent,
                        channel: 'tiktok_web',
                        cookie_enabled: 'true',
                        cursor: '',
                        device_platform: 'web',
                        from_page: 'user',
                        internal_ext: '',
                        live_id: '12',
                        resp_content_type: 'protobuf',
                        room_id: roomId,
                        client_time: String(Date.now()),
                    });

                    const r = await fetch(
                        'https://webcast.tiktok.com/webcast/im/fetch/?' + params.toString(),
                        { credentials: 'include' }
                    );

                    // Encode binary response as base64
                    const buffer = await r.arrayBuffer();
                    let binary = '';
                    const bytes = new Uint8Array(buffer);
                    for (let i = 0; i < bytes.byteLength; i++) binary += String.fromCharCode(bytes[i]);
                    const base64 = btoa(binary);

                    // x-set-tt-cookie — exposed by TikTok's CORS headers
                    let cookie = r.headers.get('x-set-tt-cookie');
                    if (!cookie) {
                        // Fallback: build from sessionid cookie so TikTokLiveSharp gets a valid header
                        const m = document.cookie.match(/(?:^|;\s*)sessionid=([^;]+)/);
                        cookie = m ? 'sessionid=' + m[1] : 'sessionid=';
                    }

                    return JSON.stringify({ ok: r.ok, status: r.status, base64, cookie });
                } catch(e) {
                    return JSON.stringify({ ok: false, error: String(e) });
                }
            })()
            """;

        try
        {
            var raw = await Dispatcher.InvokeAsync(async () =>
                await WebView.CoreWebView2.ExecuteScriptAsync(script)).Result;

            var inner = JsonSerializer.Deserialize<string>(raw);
            if (inner == null) return (Array.Empty<byte>(), "");

            using var doc = JsonDocument.Parse(inner);
            var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (!ok)
            {
                var errMsg = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetInt32() : -1;
                Serilog.Log.Warning("TikTok WebView webcast fetch failed: status={Status} error={Err}", status, errMsg);
                return (Array.Empty<byte>(), "");
            }

            var base64Str = doc.RootElement.GetProperty("base64").GetString() ?? "";
            var responseBytes = Convert.FromBase64String(base64Str);
            var cookieHeader  = doc.RootElement.TryGetProperty("cookie", out var ck) ? ck.GetString() ?? "" : "";

            Serilog.Log.Information("TikTok WebView webcast fetch → {Bytes} bytes", responseBytes.Length);
            return (responseBytes, cookieHeader);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "TikTok WebView webcast fetch error");
            return (Array.Empty<byte>(), "");
        }
    }

    // ── Resolve room ID via authenticated WebView ─────────────────────────────

    /// <summary>
    /// Calls TikTok's webcast API from inside the authenticated WebView2.
    /// Bypasses bot-detection because the request comes from a real browser with session cookies.
    /// </summary>
    private async Task<string?> ResolveRoomIdViaWebViewAsync(string username)
    {
        var usernameJson = JsonSerializer.Serialize(username);
        var script = $$"""
            (async function() {
                try {
                    const username = {{usernameJson}};

                    // Primary: webcast room/info API — returns roomId for live users
                    const r = await fetch(
                        'https://webcast.tiktok.com/webcast/room/info/?aid=1988&uniqueId=' + encodeURIComponent(username),
                        { credentials: 'include', headers: { 'Accept': 'application/json' } }
                    );
                    const d = await r.json();

                    // The API can nest roomId in several ways depending on TikTok version
                    const roomId =
                        (d?.data?.roomInfo?.roomId) ||
                        (d?.data?.room_id)          ||
                        (d?.roomId)                 ||
                        (d?.data?.id);

                    if (roomId) return String(roomId);

                    // Fallback: scrape the live page and extract roomId from page JSON
                    const page = await fetch(
                        'https://www.tiktok.com/@' + encodeURIComponent(username) + '/live',
                        { credentials: 'include' }
                    );
                    const html = await page.text();

                    // Match "roomId":"digits" or roomId:digits
                    const m = html.match(/"roomId"\s*:\s*"?(\d{15,25})"?/);
                    if (m) return m[1];
                } catch(e) {}
                return null;
            })()
            """;

        try
        {
            var raw = await Dispatcher.InvokeAsync(async () =>
                await WebView.CoreWebView2.ExecuteScriptAsync(script)).Result;

            if (raw == "null" || string.IsNullOrWhiteSpace(raw)) return null;
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch
        {
            return null;
        }
    }

    // ── Send chat via WebView2 JS ─────────────────────────────────────────────

    private async Task<bool> SendChatViaWebViewAsync(string roomId, string content)
    {
        var roomIdJson  = JsonSerializer.Serialize(roomId);
        var contentJson = JsonSerializer.Serialize(content);

        var script = $$"""
            (async function() {
                try {
                    const roomId  = {{roomIdJson}};
                    const content = {{contentJson}};
                    const ts = Date.now();

                    const qs = new URLSearchParams({
                        aid: '1988',
                        app_language: navigator.language || 'en-US',
                        app_name: 'tiktok_web',
                        browser_language: navigator.language || 'en-US',
                        browser_name: 'Mozilla',
                        browser_online: String(navigator.onLine),
                        browser_platform: navigator.platform || 'Win32',
                        browser_version: navigator.userAgent,
                        channel: 'tiktok_web',
                        client_start_timestamp_millisecond: String(ts),
                        cookie_enabled: 'true',
                        device_platform: 'web_pc',
                        focus_state: 'true',
                        from_page: 'live',
                        history_len: String(Math.min(window.history.length, 10)),
                        input_type: '0',
                        is_fullscreen: String(!!document.fullscreenElement),
                        is_page_visible: String(document.visibilityState === 'visible'),
                        os: 'windows',
                        room_id: roomId,
                        screen_height: String(screen.height),
                        screen_width: String(screen.width),
                        user_is_login: 'true',
                        webcast_language: 'en'
                    });

                    const r = await fetch(
                        'https://webcast.tiktok.com/webcast/room/chat/?' + qs.toString(),
                        {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json; charset=UTF-8' },
                            credentials: 'include',
                            body: JSON.stringify({
                                room_id: roomId,
                                content: content,
                                emotes_with_index: '',
                                input_type: 0,
                                client_start_timestamp_millisecond: ts
                            })
                        }
                    );

                    const body = await r.text();
                    let statusCode = -1;
                    try { statusCode = JSON.parse(body)?.status_code ?? -1; } catch(e) {}

                    // Success = HTTP 2xx. TikTok sometimes uses non-zero status_code
                    // even when the message was accepted (format varies by region/version).
                    const ok = r.ok || statusCode === 0;
                    return JSON.stringify({ ok, httpStatus: r.status, code: statusCode, body: body.substring(0, 200) });
                } catch(err) {
                    return JSON.stringify({ ok: false, error: String(err) });
                }
            })()
            """;

        try
        {
            var raw = await Dispatcher.InvokeAsync(async () =>
                await WebView.CoreWebView2.ExecuteScriptAsync(script)).Result;

            // ExecuteScriptAsync wraps string return values in an extra JSON layer (e.g. "\"...\"").
            // If the script somehow returned a bare object instead, Deserialize<string> would throw —
            // so fall back to parsing raw directly.
            string jsonToParse;
            try   { jsonToParse = JsonSerializer.Deserialize<string>(raw) ?? raw; }
            catch { jsonToParse = raw; }
            if (string.IsNullOrWhiteSpace(jsonToParse)) return false;

            using var doc = JsonDocument.Parse(jsonToParse);
            var ok         = doc.RootElement.TryGetProperty("ok",         out var okProp)         && okProp.GetBoolean();
            var httpStatus = doc.RootElement.TryGetProperty("httpStatus", out var httpStatusProp)  ? httpStatusProp.GetInt32() : -1;
            var code       = doc.RootElement.TryGetProperty("code",       out var codeProp)        ? codeProp.GetInt32() : -1;
            var body       = doc.RootElement.TryGetProperty("body",       out var bodyProp)        ? bodyProp.GetString() : null;
            var jsError    = doc.RootElement.TryGetProperty("error",      out var errProp)         ? errProp.GetString()  : null;

            Serilog.Log.Information(
                "TikTok WebView chat → ok={Ok} http={Http} status_code={Code} body={Body} jsError={JsError}",
                ok, httpStatus, code, body, jsError);

            return ok;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "TikTok WebView send error");
            return false;
        }
    }

    // ── Session polling (detects QR login / JS-only auth) ────────────────────

    /// <summary>
    /// Polls for a sessionid cookie every 2 seconds while the login page is open.
    /// Handles TikTok QR login, which completes via WebSocket/JS without a NavigationCompleted event.
    /// </summary>
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
            try { await HandleWaitingForLogin(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "TikTok session poll error"); }
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
            // Window is alive as chat sender — just hide it
            e.Cancel = true;
            Hide();
        }
        else
        {
            // Closed before login completed — notify frontend to reset
            MusicBot.AppEvents.RegisterTikTokWebViewSender(null);
            MusicBot.AppEvents.RegisterTikTokWebcastFetcher(null);
            MusicBot.AppEvents.NotifyTikTokLoginCancelled();
        }
    }

    /// <summary>Reset to login page (e.g. user wants to re-login after silent restore or logout).</summary>
    public void ResetAndShowLogin()
    {
        StopSessionPolling();
        _state         = State.WaitingForLogin;
        _silentRestore = false;
        MusicBot.AppEvents.RegisterTikTokWebViewSender(null);
        MusicBot.AppEvents.RegisterTikTokRoomIdResolver(null);
        MusicBot.AppEvents.RegisterTikTokWebcastFetcher(null);
        if (WebView.CoreWebView2 != null)
        {
            // Re-attach state machine — may have been detached during logout
            WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.Navigate(LoginUrl);
            StartSessionPolling();
        }
    }

    /// <summary>Permanently close — called on app exit or auth disconnect.</summary>
    public void ForceClose()
    {
        StopSessionPolling();
        _state = State.WaitingForLogin;
        MusicBot.AppEvents.RegisterTikTokWebViewSender(null);
        MusicBot.AppEvents.RegisterTikTokRoomIdResolver(null);
        MusicBot.AppEvents.RegisterTikTokWebcastFetcher(null);
        Dispatcher.Invoke(Close);
    }

    /// <summary>
    /// Clears the TikTok session by deleting all cookies from the WebView2 profile.
    /// More reliable than navigating to a logout URL because it bypasses any server-side redirect logic.
    /// After this returns the window is ready to be reused for a fresh login.
    /// Must be called from the UI thread.
    /// </summary>
    public async Task LogoutAsync()
    {
        StopSessionPolling();

        // Detach the login state machine — we are doing a session clear, not a login
        if (WebView.CoreWebView2 != null)
            WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;

        _state               = State.WaitingForLogin;
        _silentRestore       = true;
        _pendingCookieString = null;
        MusicBot.AppEvents.RegisterTikTokWebViewSender(null);
        MusicBot.AppEvents.RegisterTikTokRoomIdResolver(null);
        MusicBot.AppEvents.RegisterTikTokWebcastFetcher(null);

        // Initialize WebView2 if it hasn't been used yet (needs a window handle first)
        if (WebView.CoreWebView2 == null)
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
            WebView.CoreWebView2.IsMuted                                = true;
        }

        // Delete TikTok session cookies by domain — same pattern as Twitch/Kick
        string[] tiktokDomains = ["https://www.tiktok.com", "https://tiktok.com", "https://webcast.tiktok.com"];
        foreach (var uri in tiktokDomains)
        {
            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(uri);
            foreach (var c in cookies)
                WebView.CoreWebView2.CookieManager.DeleteCookie(c);
        }
        Serilog.Log.Information("TikTok WebView: session cookies deleted for tiktok.com domains");
    }
}
