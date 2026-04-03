namespace MusicBot;

/// <summary>
/// Static event bus for cross-layer communication (Web → Desktop).
/// The WPF App subscribes to these events.
/// </summary>
public static class AppEvents
{
    public static event Action? OnOpenLogRequested;
    public static void RequestOpenLog() => OnOpenLogRequested?.Invoke();

    /// <summary>
    /// Raised when the user wants to log in to TikTok.
    /// The Desktop layer opens a WebView2 window, captures session cookies,
    /// and posts them back to <see cref="OnTikTokCookiesCaptured"/>.
    /// </summary>
    public static event Action? OnTikTokLoginRequested;
    public static void RequestTikTokLogin() => OnTikTokLoginRequested?.Invoke();

    /// <summary>
    /// Raised by the Desktop layer when the TikTok login window is closed by the user
    /// without completing login. Lets the frontend know to reset its busy state.
    /// </summary>
    public static event Action? OnTikTokLoginCancelled;
    public static void NotifyTikTokLoginCancelled() => OnTikTokLoginCancelled?.Invoke();

    /// <summary>
    /// Raised on startup when saved TikTok cookies are found in the DB.
    /// The Desktop layer silently restores the WebView2 session in the background
    /// (no window shown to the user) so the WebView chat sender is available.
    /// </summary>
    public static event Action? OnTikTokSessionRestoreRequested;
    public static void RequestTikTokSessionRestore() => OnTikTokSessionRestoreRequested?.Invoke();

    /// <summary>
    /// Raised by the Desktop layer after successfully capturing TikTok cookies.
    /// cookieString: full cookie header value.
    /// username: TikTok @handle (without @), or null if it could not be detected.
    /// </summary>
    public static event Action<string, string?>? OnTikTokCookiesCaptured;
    public static void NotifyTikTokCookiesCaptured(string cookieString, string? username)
        => OnTikTokCookiesCaptured?.Invoke(cookieString, username);

    /// <summary>
    /// Registered by the Desktop WebView2 window after TikTok login.
    /// Sends a chat message by executing fetch() inside the authenticated WebView2,
    /// so TikTok's own JS SDK adds X-Bogus, ticket-guard headers, etc. automatically.
    /// Returns true if the message was sent successfully.
    /// </summary>
    private static Func<string, string, Task<bool>>? _tiktokWebViewSender;

    public static void RegisterTikTokWebViewSender(Func<string, string, Task<bool>>? sender)
        => _tiktokWebViewSender = sender;

    public static Task<bool> SendViaTikTokWebView(string roomId, string content)
        => _tiktokWebViewSender?.Invoke(roomId, content) ?? Task.FromResult(false);

    public static bool HasTikTokWebViewSender => _tiktokWebViewSender != null;

    /// <summary>
    /// Registered by the Desktop WebView2 window after TikTok login.
    /// Resolves a room ID for a given username by calling TikTok's webcast API
    /// from inside the authenticated browser context (bypasses bot-detection).
    /// </summary>
    private static Func<string, Task<string?>>? _tiktokRoomIdResolver;
    public static void RegisterTikTokRoomIdResolver(Func<string, Task<string?>>? resolver)
        => _tiktokRoomIdResolver = resolver;
    public static Task<string?> ResolveRoomIdViaWebView(string username)
        => _tiktokRoomIdResolver?.Invoke(username) ?? Task.FromResult<string?>(null);
    public static bool HasTikTokRoomIdResolver => _tiktokRoomIdResolver != null;

    /// <summary>
    /// Registered by the Desktop WebView2 window after TikTok login.
    /// Fetches the raw Protobuf WebcastResponse bytes for a room ID by calling
    /// TikTok's webcast API from inside the authenticated browser context.
    /// TikTok's own JS adds X-Bogus and other anti-bot signing automatically,
    /// bypassing the empty-body responses we get from server-side HTTP calls.
    /// Returns (bytes, x-set-tt-cookie value).
    /// </summary>
    private static Func<string, Task<(byte[] Bytes, string CookieHeader)>>? _tiktokWebcastFetcher;
    public static void RegisterTikTokWebcastFetcher(Func<string, Task<(byte[] Bytes, string CookieHeader)>>? fetcher)
        => _tiktokWebcastFetcher = fetcher;
    public static Task<(byte[] Bytes, string CookieHeader)> FetchWebcastData(string roomId)
        => _tiktokWebcastFetcher?.Invoke(roomId) ?? Task.FromResult<(byte[], string)>((Array.Empty<byte>(), ""));
    public static bool HasTikTokWebcastFetcher => _tiktokWebcastFetcher != null;

    /// <summary>
    /// Raised when the user forgets a platform account.
    /// The Desktop layer must clear any WebView2 session cookies for that platform.
    /// platform: "tiktok" | "twitch" | "kick"
    /// </summary>
    public static event Func<string, Task>? OnPlatformAuthForgotten;
    public static Task NotifyPlatformAuthForgotten(string platform)
        => OnPlatformAuthForgotten?.Invoke(platform) ?? Task.CompletedTask;

    /// <summary>
    /// Raised by the API when the user requests a full application shutdown.
    /// The Desktop layer performs a clean exit (stops playback, closes tray, terminates process).
    /// </summary>
    public static event Action? OnShutdownRequested;
    public static void RequestShutdown() => OnShutdownRequested?.Invoke();
}
