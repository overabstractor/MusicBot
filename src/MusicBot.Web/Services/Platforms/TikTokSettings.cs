namespace MusicBot.Services.Platforms;

public class TikTokSettings
{
    /// <summary>TikTok @username of the streamer (e.g. "myusername")</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>MusicBot user slug that owns this TikTok channel</summary>
    public string UserSlug { get; set; } = string.Empty;

    /// <summary>
    /// Optional signing server URL to bypass TikTok IP blocks.
    /// EulerStream provides a free tier: https://www.eulerstream.com
    /// Example: "https://tiktok.eulerstream.com"
    /// </summary>
    public string SigningServerUrl { get; set; } = string.Empty;

    /// <summary>API key for the signing server (if required)</summary>
    public string SigningServerApiKey { get; set; } = string.Empty;

    /// <summary>
    /// TikTok session ID cookie for sending chat messages.
    /// Extract from browser: DevTools → Application → Cookies → sessionid
    /// WARNING: Keep this secret — it grants access to your TikTok account.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Full browser cookie string for sending chat messages.
    /// Provides all cookies TikTok needs (tt-csrf-token, msToken, etc.) without X-Bogus signing.
    /// How to extract: Open TikTok in browser → DevTools (F12) → Network tab →
    ///   send any request to tiktok.com → right-click → Copy → Copy as cURL →
    ///   grab the full -H 'cookie: ...' value.
    /// If set, this takes precedence over SessionId for chat sending.
    /// WARNING: Keep this secret — it grants full access to your TikTok account.
    /// </summary>
    public string CookieString { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(UserSlug);
    public bool CanSendMessages => !string.IsNullOrWhiteSpace(CookieString) || !string.IsNullOrWhiteSpace(SessionId);
}
