using Microsoft.AspNetCore.Mvc;
using MusicBot;
using MusicBot.Services.Platforms;

namespace MusicBot.Controllers;

/// <summary>
/// Local signing-server proxy for TikTokLiveSharp.
///
/// TikTokLiveSharp calls  GET /webcast/fetch?room_id=...&amp;client=ttlive-net&amp;uuc=1
/// expecting a Protobuf-encoded WebcastResponse (TikTok's native format) plus the
/// x-set-tt-cookie response header.
///
/// We proxy the request straight to TikTok's webcast API using the session cookies
/// captured during in-app login.  Because the request originates from a real
/// authenticated session, TikTok returns a valid response with a signed WebSocket URL
/// (PushServer field) — no external signing service needed.
/// </summary>
[ApiController]
[Route("webcast")]
[Tags("TikTok")]
public class TikTokSigningController : ControllerBase
{
    private const string TikTokFetchUrl = "https://webcast.tiktok.com/webcast/im/fetch/";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    private readonly TikTokAuthService      _auth;
    private readonly IHttpClientFactory     _httpFactory;
    private readonly ILogger<TikTokSigningController> _logger;

    public TikTokSigningController(
        TikTokAuthService auth,
        IHttpClientFactory httpFactory,
        ILogger<TikTokSigningController> logger)
    {
        _auth        = auth;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    /// <summary>
    /// Handles TikTokLiveSharp's signing-server request.
    ///
    /// Primary path: delegates to the authenticated WebView2 window, which calls
    /// TikTok's webcast API from inside a real browser context.  TikTok's own JS
    /// adds X-Bogus and other anti-bot signing automatically, so the response
    /// contains valid Protobuf data (unlike server-side calls that return 0 bytes).
    ///
    /// Fallback: direct HTTP with session cookies (may still return 0 bytes on
    /// bot-protected endpoints, but retained as a safety net).
    /// </summary>
    [HttpGet("fetch")]
    public async Task<IActionResult> Fetch(
        [FromQuery] string? room_id,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(room_id))
            return BadRequest("room_id is required");

        // ── Primary: WebView2 fetch (authenticated browser context) ─────────
        if (AppEvents.HasTikTokWebcastFetcher)
        {
            try
            {
                var (bytes, cookieHeader) = await AppEvents.FetchWebcastData(room_id);

                _logger.LogInformation(
                    "TikTok signing proxy (WebView) → {Bytes} bytes for room {RoomId}",
                    bytes.Length, room_id);

                if (bytes.Length > 0)
                {
                    if (!string.IsNullOrWhiteSpace(cookieHeader))
                        Response.Headers["x-set-tt-cookie"] = cookieHeader;
                    return File(bytes, "application/octet-stream");
                }

                _logger.LogWarning("TikTok WebView fetch returned 0 bytes for room {RoomId} — trying HTTP fallback", room_id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TikTok WebView signing proxy error for room {RoomId} — trying HTTP fallback", room_id);
            }
        }

        // ── Fallback: server-side HTTP with session cookies ──────────────────
        if (!_auth.IsAuthenticated || string.IsNullOrWhiteSpace(_auth.CookieString))
        {
            _logger.LogWarning("TikTok signing proxy: no session available — re-login required");
            return StatusCode(503, "TikTok session not available");
        }

        try
        {
            var qs = BuildQueryString(room_id);
            var request = new HttpRequestMessage(HttpMethod.Get, TikTokFetchUrl + qs);

            request.Headers.Add("Cookie",          _auth.CookieString);
            request.Headers.Add("User-Agent",      UserAgent);
            request.Headers.Add("Accept",          "application/json, text/plain, */*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Origin",          "https://www.tiktok.com");
            request.Headers.Add("Referer",         "https://www.tiktok.com/");
            request.Headers.Add("Sec-Fetch-Dest",  "empty");
            request.Headers.Add("Sec-Fetch-Mode",  "cors");
            request.Headers.Add("Sec-Fetch-Site",  "same-site");

            var http     = _httpFactory.CreateClient();
            var response = await http.SendAsync(request, ct);
            var bytes    = await response.Content.ReadAsByteArrayAsync(ct);

            _logger.LogInformation(
                "TikTok signing proxy (HTTP) → {Status} ({Bytes} bytes) for room {RoomId}",
                (int)response.StatusCode, bytes.Length, room_id);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TikTok signing proxy: upstream returned {Status}", response.StatusCode);
                return StatusCode((int)response.StatusCode);
            }

            if (response.Headers.TryGetValues("x-set-tt-cookie", out var ttCookies))
                Response.Headers["x-set-tt-cookie"] = string.Join(";", ttCookies);

            var contentType = response.Content.Headers.ContentType?.ToString()
                              ?? "application/octet-stream";

            return File(bytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TikTok signing proxy error for room {RoomId}", room_id);
            return StatusCode(502, "Signing proxy error");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildQueryString(string roomId)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return "?aid=1988" +
               "&app_language=en-US" +
               "&app_name=tiktok_web" +
               "&browser_language=en-US" +
               "&browser_name=Mozilla" +
               "&browser_online=true" +
               "&browser_platform=Win32" +
               $"&browser_version={Uri.EscapeDataString(UserAgent)}" +
               "&channel=tiktok_web" +
               "&cookie_enabled=true" +
               "&cursor=" +
               "&device_platform=web_pc" +
               "&fetch_interval=2000" +
               "&from_page=user" +
               "&history_comment_count=20" +
               "&internal_ext=" +
               "&live_id=12" +
               "&os=windows" +
               $"&room_id={Uri.EscapeDataString(roomId)}" +
               "&resp_content_type=protobuf" +
               "&screen_height=1080" +
               "&screen_width=1920" +
               $"&client_time={ts}" +
               "&tz_name=America%2FNew_York" +
               "&version_code=180800";
    }
}
