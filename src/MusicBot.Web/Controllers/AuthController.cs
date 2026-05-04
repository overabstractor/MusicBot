using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Services.Platforms;
using MusicBot.Services.Spotify;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/auth")]
[Tags("Auth")]
public class AuthController : ControllerBase
{
    private readonly MusicBotDbContext _db;
    private readonly UserContextManager _userContext;
    private readonly TwitchAuthService _twitchAuth;
    private readonly KickAuthService _kickAuth;
    private readonly TikTokAuthService _tiktokAuth;
    private readonly YouTubeAuthService _youtubeAuth;
    private readonly IHubContext<OverlayHub> _hub;

    public AuthController(MusicBotDbContext db, UserContextManager userContext, TwitchAuthService twitchAuth, KickAuthService kickAuth, TikTokAuthService tiktokAuth, YouTubeAuthService youtubeAuth, IHubContext<OverlayHub> hub)
    {
        _db          = db;
        _userContext = userContext;
        _twitchAuth  = twitchAuth;
        _kickAuth    = kickAuth;
        _tiktokAuth  = tiktokAuth;
        _youtubeAuth = youtubeAuth;
        _hub         = hub;
    }

    /// <summary>Get local user info and Spotify connection status</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(MeResponse), 200)]
    public IActionResult Me()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        return Ok(new MeResponse
        {
            Id               = LocalUser.Id,
            Username         = LocalUser.Username,
            OverlayToken     = LocalUser.OverlayToken,
            SpotifyConnected = services.Spotify.IsAuthenticated
        });
    }

    // ── Spotify OAuth ────────────────────────────────────────────────────────

    /// <summary>Get Spotify OAuth URL</summary>
    [HttpGet("spotify")]
    [ProducesResponseType(typeof(SpotifyAuthUrlResponse), 200)]
    public IActionResult SpotifyLogin()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        var url = ((SpotifyService)services.Spotify).GetAuthUrl(LocalUser.Id.ToString());
        return Ok(new SpotifyAuthUrlResponse { Url = url });
    }

    /// <summary>Spotify OAuth callback — handles redirect from Spotify</summary>
    [HttpGet("spotify/callback")]
    public async Task<IActionResult> SpotifyCallback(
        [FromQuery] string? code,
        [FromQuery] string? error,
        [FromQuery] string? state)
    {
        if (!string.IsNullOrEmpty(error))
            return BadRequest($"Authorization failed: {error}");

        if (string.IsNullOrEmpty(code))
            return BadRequest("Missing authorization code");

        var services = _userContext.GetOrCreate(LocalUser.Id);
        try
        {
            await services.Spotify.HandleCallbackAsync(code);
            return Content("""
                <html>
                <body style='background:#1a1a2e;color:#e0e0e0;font-family:sans-serif;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;'>
                    <div style='text-align:center;'>
                        <h1 style='color:#1DB954;'>&#10003; Spotify Connected!</h1>
                        <p>You can close this window and return to MusicBot.</p>
                    </div>
                </body>
                </html>
                """, "text/html");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Authentication error: {ex.Message}");
        }
    }

    /// <summary>Check Spotify connection status</summary>
    [HttpGet("spotify/status")]
    [ProducesResponseType(typeof(SpotifyStatusResponse), 200)]
    public IActionResult SpotifyStatus()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        return Ok(new SpotifyStatusResponse { Authenticated = services.Spotify.IsAuthenticated });
    }

    /// <summary>Disconnect Spotify — removes stored token</summary>
    [HttpDelete("spotify")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> SpotifyDisconnect()
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);
        await services.Spotify.DisconnectAsync();
        return NoContent();
    }
    // ── System browser helper ─────────────────────────────────────────────────

    /// <summary>
    /// Opens a URL in the user's default system browser.
    /// Used for OAuth flows (Twitch, Kick) so the user can leverage their existing sessions.
    /// </summary>
    [HttpPost("open-in-browser")]
    [ProducesResponseType(200)]
    public IActionResult OpenInBrowser([FromQuery] string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return BadRequest("URL inválida");

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
        return Ok();
    }

    // ── Twitch OAuth ─────────────────────────────────────────────────────────

    /// <summary>Get Twitch OAuth URL</summary>
    [HttpGet("twitch")]
    public IActionResult TwitchLogin()
    {
        var url = _twitchAuth.GetAuthUrl(LocalUser.Id.ToString());
        return Ok(new { url });
    }

    /// <summary>Twitch OAuth callback — handles redirect from Twitch</summary>
    [HttpGet("twitch/callback")]
    public async Task<IActionResult> TwitchCallback(
        [FromQuery] string? code,
        [FromQuery] string? error,
        [FromQuery] string? state)
    {
        if (!string.IsNullOrEmpty(error))
            return BadRequest($"Authorization failed: {error}");

        if (string.IsNullOrEmpty(code))
            return BadRequest("Missing authorization code");

        try
        {
            await _twitchAuth.HandleCallbackAsync(code);
            await _hub.Clients.Group($"user:{LocalUser.Id}")
                .SendAsync("auth:updated", new { platform = "twitch", authenticated = true, username = _twitchAuth.BotUsername });
            return Content("""
                <html>
                <body style='background:#1a1a2e;color:#e0e0e0;font-family:sans-serif;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;'>
                    <div style='text-align:center;'>
                        <h1 style='color:#9146ff;'>&#10003; Twitch Connected!</h1>
                        <p>You can close this window and return to MusicBot.</p>
                    </div>
                </body>
                </html>
                """, "text/html");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Authentication error: {ex.Message}");
        }
    }

    /// <summary>Check Twitch OAuth status</summary>
    [HttpGet("twitch/status")]
    public IActionResult TwitchStatus()
    {
        return Ok(new { authenticated = _twitchAuth.IsAuthenticated, username = _twitchAuth.BotUsername });
    }

    /// <summary>Disconnect Twitch — removes stored token</summary>
    [HttpDelete("twitch")]
    public async Task<IActionResult> TwitchDisconnect()
    {
        await _twitchAuth.DisconnectAsync();
        return NoContent();
    }

    // ── TikTok cookie login ───────────────────────────────────────────────────

    /// <summary>
    /// Opens the in-app TikTok login window (WebView2).
    /// The Desktop layer will capture session cookies automatically after the user logs in.
    /// </summary>
    [HttpPost("tiktok/start")]
    [ProducesResponseType(200)]
    public IActionResult TikTokStartLogin()
    {
        _tiktokAuth.ResetCancelledFlag();
        AppEvents.RequestTikTokLogin();
        return Ok(new { message = "TikTok login window opening…" });
    }

    /// <summary>Check TikTok auth status — also signals when the login window was closed without logging in</summary>
    [HttpGet("tiktok/status")]
    [ProducesResponseType(200)]
    public IActionResult TikTokStatus()
        => Ok(new { authenticated = _tiktokAuth.IsAuthenticated, username = _tiktokAuth.Username, cancelled = _tiktokAuth.LoginCancelled });

    /// <summary>Disconnect TikTok — removes stored cookies</summary>
    [HttpDelete("tiktok")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> TikTokDisconnect()
    {
        await _tiktokAuth.DisconnectAsync();
        return NoContent();
    }

    // ── YouTube cookie login (yt-dlp bot detection bypass) ────────────────────

    /// <summary>
    /// Opens the in-app YouTube/Google login window (WebView2). After login the Desktop
    /// layer captures cookies and writes them to a Netscape cookies.txt that yt-dlp uses.
    /// </summary>
    [HttpPost("youtube/start")]
    [ProducesResponseType(200)]
    public IActionResult YouTubeStartLogin()
    {
        _youtubeAuth.ResetCancelledFlag();
        AppEvents.RequestYouTubeLogin();
        return Ok(new { message = "YouTube login window opening…" });
    }

    /// <summary>Get YouTube auth state — toggle on/off + connection status.</summary>
    [HttpGet("youtube/status")]
    [ProducesResponseType(200)]
    public IActionResult YouTubeStatus() => Ok(new
    {
        enabled       = _youtubeAuth.IsEnabled,
        authenticated = _youtubeAuth.IsConnected,
        account       = _youtubeAuth.AccountLabel,
        savedAt       = _youtubeAuth.SavedAt == default ? (DateTimeOffset?)null : _youtubeAuth.SavedAt,
        cancelled     = _youtubeAuth.LoginCancelled,
    });

    /// <summary>Enable the use of YouTube cookies in yt-dlp.</summary>
    [HttpPost("youtube/enable")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> YouTubeEnable()
    {
        await _youtubeAuth.EnableAsync();
        return NoContent();
    }

    /// <summary>Disable the use of YouTube cookies (cookies file is preserved on disk).</summary>
    [HttpPost("youtube/disable")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> YouTubeDisable()
    {
        await _youtubeAuth.DisableAsync();
        return NoContent();
    }

    /// <summary>Disconnect YouTube — deletes the cookies file and clears the WebView2 session.</summary>
    [HttpDelete("youtube")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> YouTubeDisconnect()
    {
        await _youtubeAuth.DisconnectAsync();
        return NoContent();
    }

    // ── Kick OAuth ────────────────────────────────────────────────────────────

    /// <summary>Get Kick OAuth URL</summary>
    [HttpGet("kick")]
    public IActionResult KickLogin()
    {
        var url = _kickAuth.GetAuthUrl(LocalUser.Id.ToString());
        return Ok(new { url });
    }

    /// <summary>Kick OAuth callback — handles redirect from Kick</summary>
    [HttpGet("kick/callback")]
    public async Task<IActionResult> KickCallback(
        [FromQuery] string? code,
        [FromQuery] string? error,
        [FromQuery] string? state)
    {
        if (!string.IsNullOrEmpty(error))
            return BadRequest($"Authorization failed: {error}");

        if (string.IsNullOrEmpty(code))
            return BadRequest("Missing authorization code");

        try
        {
            await _kickAuth.HandleCallbackAsync(code);
            await _hub.Clients.Group($"user:{LocalUser.Id}")
                .SendAsync("auth:updated", new { platform = "kick", authenticated = true, channel = _kickAuth.ChannelName });
            return Content("""
                <html>
                <body style='background:#1a1a2e;color:#e0e0e0;font-family:sans-serif;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;'>
                    <div style='text-align:center;'>
                        <h1 style='color:#53fc18;'>&#10003; Kick Connected!</h1>
                        <p>You can close this window and return to MusicBot.</p>
                    </div>
                </body>
                </html>
                """, "text/html");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Authentication error: {ex.Message}");
        }
    }

    /// <summary>Check Kick OAuth status</summary>
    [HttpGet("kick/status")]
    public IActionResult KickStatus()
    {
        return Ok(new { authenticated = _kickAuth.IsAuthenticated, channel = _kickAuth.ChannelName });
    }

    /// <summary>Disconnect Kick — removes stored token</summary>
    [HttpDelete("kick")]
    public async Task<IActionResult> KickDisconnect()
    {
        await _kickAuth.DisconnectAsync();
        return NoContent();
    }

}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class MeResponse
{
    public Guid   Id               { get; set; }
    public string Username         { get; set; } = string.Empty;
    public string OverlayToken     { get; set; } = string.Empty;
    public bool   SpotifyConnected { get; set; }
}

public class SpotifyAuthUrlResponse { public string Url { get; set; } = string.Empty; }
public class SpotifyStatusResponse  { public bool Authenticated { get; set; } }
public class ErrorResponse          { public string Error { get; set; } = string.Empty; }
