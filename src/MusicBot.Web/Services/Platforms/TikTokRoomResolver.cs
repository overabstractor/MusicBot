using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MusicBot.Services.Library;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Resolves a TikTok LIVE room ID from a username.
/// Strategy: scrape the TikTok page with browser-like headers.
/// Fallback: use yt-dlp (already in the project) to extract the room ID.
/// This bypasses TikTokLiveSharp's built-in scraping which gets blocked by TikTok.
/// </summary>
public class TikTokRoomResolver
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TikTokAuthService _tikTokAuth;
    private readonly TikTokSettings _tikTokSettings;
    private readonly SigningKeyService _signingKeys;
    private readonly ILogger<TikTokRoomResolver> _logger;
    private readonly string _ytDlpPath;

    /// <summary>Euler Stream REST API base (room resolution, chat). Distinct from the WS signer.</summary>
    private const string EulerRestBase = "https://tiktok.eulerstream.com";

    // Latches true after Euler /webcast/room_id returns 401/402/403 once: the current plan does
    // not cover the endpoint, so there's no point calling it again every poll cycle (it just adds
    // latency + noise). Reset only on app restart.
    private volatile bool _eulerRoomDisabled;

    // Multiple patterns — TikTok changes page structure frequently
    private static readonly Regex[] RoomIdPatterns =
    [
        new(@"""roomId""\s*:\s*""(\d{15,25})""",   RegexOptions.Compiled), // "roomId":"digits"
        new(@"""roomId""\s*:\s*(\d{15,25})\b",     RegexOptions.Compiled), // "roomId":digits
        new(@"""room_id""\s*:\s*""(\d{15,25})""",  RegexOptions.Compiled), // "room_id":"digits"
        new(@"""room_id""\s*:\s*(\d{15,25})\b",    RegexOptions.Compiled), // "room_id":digits
        new(@"room_id[""=:]+\s*[""']?(\d{15,25})", RegexOptions.Compiled), // room_id=digits
        new(@"roomID[""=:]+\s*[""']?(\d{15,25})",  RegexOptions.Compiled), // roomID=digits
    ];

    // TikTok embeds "status":2 in the page JSON only when the room is actively live.
    // Offline/ended rooms have status 4. This guards against returning a stale roomId
    // that TikTok includes in the HTML even when the user is not streaming.
    private static readonly Regex LiveStatusPattern =
        new(@"""status""\s*:\s*2\b", RegexOptions.Compiled);

    public TikTokRoomResolver(
        IHttpClientFactory httpFactory,
        TikTokAuthService tikTokAuth,
        IOptions<TikTokSettings> tikTokSettings,
        SigningKeyService signingKeys,
        IOptions<MusicLibrarySettings> libSettings,
        ILogger<TikTokRoomResolver> logger)
    {
        _httpFactory    = httpFactory;
        _tikTokAuth     = tikTokAuth;
        _tikTokSettings = tikTokSettings.Value;
        _signingKeys    = signingKeys;
        _logger         = logger;
        _ytDlpPath      = libSettings.Value.YtDlpPath ?? "yt-dlp";
    }

    /// <summary>
    /// Attempts to resolve the room ID for a TikTok LIVE streamer.
    /// Returns null if the user is not live or the room ID cannot be found.
    /// </summary>
    public async Task<string?> ResolveRoomIdAsync(string username, CancellationToken ct = default)
    {
        // PRIMARY: TikTok's unauthenticated api-live endpoint (GET /api-live/user/room/).
        // Returns the roomId + live status as structured JSON with NO auth, cookies, or signing.
        // When it answers authoritatively (live → roomId, or not-live → null) we trust it and skip
        // every other resolver. Only an HTTP/parse error falls through.
        var (apiLiveHandled, apiLiveRoomId) = await TryApiLiveUserRoomAsync(username, ct);
        if (apiLiveHandled)
        {
            if (apiLiveRoomId != null)
                _logger.LogInformation("TikTok room ID for @{User} resolved via api-live/user/room: {RoomId}", username, apiLiveRoomId);
            else
                _logger.LogDebug("TikTok api-live: @{User} no está en vivo", username);
            return apiLiveRoomId;
        }

        // SECONDARY: Euler Stream REST API (GET /webcast/room_id) — only succeeds if the Euler
        // plan covers it (otherwise 401 → falls through). Kept so it activates automatically if
        // the plan is upgraded, but the unauthenticated endpoint above is the normal path.
        var (eulerHandled, eulerRoomId) = await TryEulerRoomIdAsync(username, ct);
        if (eulerHandled)
        {
            if (eulerRoomId != null)
                _logger.LogInformation("TikTok room ID for @{User} resolved via Euler Stream: {RoomId}", username, eulerRoomId);
            else
                _logger.LogDebug("TikTok Euler Stream: @{User} no está en vivo", username);
            return eulerRoomId;
        }

        _logger.LogDebug("TikTok @{User}: room_id no resuelto vía API — usando resolvers de respaldo", username);

        // ── Fallbacks (solo cuando los endpoints de API no respondieron) ──────────────────
        // Strategy 0: authenticated HTTP call to /api/live/detail/ with session cookies.
        // More reliable than HTML scraping because it uses TikTok's own API endpoint,
        // which returns structured JSON instead of embedded script data that can change.
        var apiRoomId = await TryAuthenticatedApiAsync(username, _tikTokAuth.CookieString, ct);
        if (apiRoomId != null)
        {
            _logger.LogInformation("TikTok room ID for @{User} resolved via authenticated API: {RoomId}", username, apiRoomId);
            return apiRoomId;
        }

        // Strategy 1: authenticated WebView2 — calls TikTok's webcast API from a real browser
        // context with session cookies, bypassing bot-detection entirely.
        if (AppEvents.HasTikTokRoomIdResolver)
        {
            var roomId = await AppEvents.ResolveRoomIdViaWebView(username);
            if (roomId != null)
            {
                _logger.LogInformation("TikTok room ID for @{User} resolved via WebView: {RoomId}", username, roomId);
                return roomId;
            }
            _logger.LogInformation("TikTok WebView resolver returned null for @{User} — trying HTTP fallbacks", username);
        }
        else
        {
            // Loud warning: HTTP fallbacks fail for self-query (your own account returns 404 from anchorinfo
            // and an HTML stub from /@user/live). Without the WebView resolver registered, the connection
            // cannot succeed for the logged-in user's own live. Usually means the TikTokLoginWindow silent
            // restore did not complete — check for "TikTok WebView session restored silently" at startup.
            _logger.LogWarning(
                "TikTok WebView resolver NOT registered for @{User} — bot-detection bypass disabled. " +
                "Look for 'TikTok WebView session restored silently' or 'no sessionid cookie' in startup logs.",
                username);
        }

        // Strategy 2: HTTP scrape with browser headers
        var httpRoomId = await TryHttpScrapeAsync(username, ct);
        if (httpRoomId != null)
        {
            _logger.LogInformation("TikTok room ID for @{User} resolved via HTTP: {RoomId}", username, httpRoomId);
            return httpRoomId;
        }

        // Strategy 3: yt-dlp extraction
        var ytRoomId = await TryYtDlpAsync(username, ct);
        if (ytRoomId != null)
        {
            _logger.LogInformation("TikTok room ID for @{User} resolved via yt-dlp: {RoomId}", username, ytRoomId);
            return ytRoomId;
        }

        _logger.LogWarning("TikTok: could not resolve room ID for @{User} — user may not be live", username);
        return null;
    }

    /// <summary>
    /// PRIMARY resolver: TikTok's unauthenticated <c>GET /api-live/user/room/</c> endpoint
    /// (<c>?aid=1988&amp;sourceType=54&amp;uniqueId=...</c>). Returns the roomId and live status as
    /// structured JSON without any auth, cookies, or anti-bot signing — the same endpoint the
    /// TikTok-Live-Connector libraries use. Returns <c>(Handled, RoomId)</c>: <c>Handled=true</c>
    /// when TikTok answered (RoomId if live, null if offline); <c>Handled=false</c> on HTTP/parse
    /// error so the caller falls back.
    /// </summary>
    private async Task<(bool Handled, string? RoomId)> TryApiLiveUserRoomAsync(string username, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("tiktok");
            var url = "https://www.tiktok.com/api-live/user/room/" +
                      $"?aid=1988&sourceType=54&uniqueId={Uri.EscapeDataString(username)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Referer", "https://www.tiktok.com/");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TikTok api-live/user/room HTTP {Status} para @{User} — fallback", (int)response.StatusCode, username);
                return (false, null);
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);

            // { data: { user: { roomId, status, … }, liveRoom: { status, … } }, status_code }
            // No "data" at all = malformed/error → fall back. "data" present but no "user" means
            // TikTok has no live profile for this uniqueId → authoritatively not live (don't scrape).
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                _logger.LogDebug("TikTok api-live/user/room sin data para @{User} — fallback", username);
                return (false, null);
            }
            if (!data.TryGetProperty("user", out var user))
                return (true, null); // authoritative: not live

            // status == 2 means actively live (4 = offline/ended). Same value TikTok embeds in
            // the live page JSON, so it is the authoritative "is this room live right now?" signal.
            // Note: TikTok keeps a STALE roomId on offline users (status 4) — the status check is
            // what prevents connecting to a dead room, so it must gate the roomId read below.
            var isLive = user.TryGetProperty("status", out var st)
                         && st.ValueKind == JsonValueKind.Number
                         && st.GetInt32() == 2;
            if (!isLive) return (true, null); // authoritative: not live

            if (user.TryGetProperty("roomId", out var rid))
            {
                var roomId = rid.ValueKind == JsonValueKind.String ? rid.GetString() : rid.GetRawText().Trim('"');
                if (!string.IsNullOrWhiteSpace(roomId) && roomId != "0")
                    return (true, roomId);
            }

            // status=2 but no usable roomId — odd; let the fallbacks try.
            _logger.LogDebug("TikTok api-live/user/room: live pero sin roomId para @{User} — fallback", username);
            return (false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Inner 10s timeout — surface it (it was silent at Debug before) so slow cycles are visible.
            _logger.LogWarning("TikTok api-live/user/room timeout (>10s) para @{User} — reintentando", username);
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TikTok api-live/user/room falló para @{User} — fallback", username);
            return (false, null);
        }
    }

    /// <summary>
    /// SECONDARY resolver: Euler Stream's <c>GET /webcast/room_id?uniqueId=...</c>.
    /// Returns <c>(Handled, RoomId)</c>: <c>Handled=true</c> when Euler answered authoritatively
    /// (RoomId set if live, null if not live); <c>Handled=false</c> on missing key, unsupported
    /// plan (403/402), or network error — so the caller falls back to the legacy resolvers.
    /// Requires a PRO/Business Euler plan; the API key is the config key or the shared relay key.
    /// </summary>
    private async Task<(bool Handled, string? RoomId)> TryEulerRoomIdAsync(string username, CancellationToken ct)
    {
        if (_eulerRoomDisabled) return (false, null); // plan doesn't cover it (latched on first 401)

        var apiKey = await _signingKeys.GetEffectiveKeyAsync(_tikTokSettings.SigningServerApiKey, ct);
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, null); // no key → fall back

        try
        {
            var client = _httpFactory.CreateClient("tiktok");
            var url = $"{EulerRestBase}/webcast/room_id?uniqueId={Uri.EscapeDataString(username)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));

            var response = await client.SendAsync(request, cts.Token);

            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden
                                    or System.Net.HttpStatusCode.PaymentRequired
                                    or System.Net.HttpStatusCode.Unauthorized)
            {
                _eulerRoomDisabled = true; // don't call it again this session
                _logger.LogWarning(
                    "TikTok Euler /webcast/room_id rechazado ({Status}) — el plan de Euler no cubre este endpoint (requiere PRO/Business). Deshabilitado por esta sesión; usando resolvers locales.",
                    (int)response.StatusCode);
                return (false, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TikTok Euler room_id HTTP {Status} para @{User} — fallback", (int)response.StatusCode, username);
                return (false, null);
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // { ok, code, is_live, room_id, message, routes_attempted }
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                _logger.LogDebug("TikTok Euler room_id ok=false para @{User} — fallback", username);
                return (false, null);
            }

            var isLive = root.TryGetProperty("is_live", out var liveEl) && liveEl.ValueKind == JsonValueKind.True;
            if (!isLive) return (true, null); // authoritative: not live

            if (root.TryGetProperty("room_id", out var ridEl))
            {
                var roomId = ridEl.ValueKind == JsonValueKind.String ? ridEl.GetString() : ridEl.GetRawText().Trim('"');
                if (!string.IsNullOrWhiteSpace(roomId)) return (true, roomId);
            }

            // ok + live but no room_id → odd; let the fallbacks try rather than returning null.
            _logger.LogDebug("TikTok Euler room_id: ok+live pero sin room_id para @{User} — fallback", username);
            return (false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TikTok Euler room_id falló para @{User} — fallback", username);
            return (false, null);
        }
    }

    /// <summary>
    /// Strategy 0: Calls TikTok's webcast anchor-info API with session cookies.
    /// Returns the room ID directly from structured JSON, skipping HTML parsing.
    /// Requires the user to be authenticated (session cookies stored in TikTokAuthService).
    /// </summary>
    private async Task<string?> TryAuthenticatedApiAsync(string username, string? cookieString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cookieString)) return null;

        try
        {
            var client = _httpFactory.CreateClient("tiktok");

            // webcast/live/anchorinfo/ accepts a uniqueId query parameter and returns
            // the room_id in JSON without needing to scrape HTML.
            var url = $"https://webcast.tiktok.com/webcast/live/anchorinfo/" +
                      $"?aid=1988&app_name=tiktok_web&device_platform=web_pc" +
                      $"&from_page=live&host_id={Uri.EscapeDataString("@" + username)}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Cookie", cookieString);
            request.Headers.Add("Origin", "https://www.tiktok.com");
            request.Headers.Add("Referer", "https://www.tiktok.com/");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));

            var response = await client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TikTok authenticated API: HTTP {Status} for @{User}", (int)response.StatusCode, username);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var doc = JsonDocument.Parse(json);

            // Response shape: { "status_code": 0, "data": { "room_id": "...", "status": 2 } }
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            // status 2 = live, status 4 = offline
            if (data.TryGetProperty("status", out var statusProp) && statusProp.GetInt32() != 2)
            {
                _logger.LogDebug("TikTok authenticated API: @{User} status={Status} (not live)", username, statusProp.GetInt32());
                return null;
            }

            if (data.TryGetProperty("room_id", out var roomIdProp))
            {
                var roomId = roomIdProp.ValueKind == JsonValueKind.String
                    ? roomIdProp.GetString()
                    : roomIdProp.GetRawText().Trim('"');
                return string.IsNullOrWhiteSpace(roomId) ? null : roomId;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("TikTok authenticated API: timeout for @{User}", username);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TikTok authenticated API failed for @{User}", username);
            return null;
        }
    }

    private async Task<string?> TryHttpScrapeAsync(string username, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("tiktok");
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.tiktok.com/@{username}/live");
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Sec-Fetch-Site", "none");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Sec-Fetch-Dest", "document");

            // 8s cap — TikTok slow-walls this scrape (it once blocked a cycle for 20s). Keeping it
            // short means a failed resolve cycle retries quickly instead of stalling.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var response = await client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TikTok HTTP scrape failed: {Status}", response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token);

            string? roomId = null;
            foreach (var pattern in RoomIdPatterns)
            {
                var match = pattern.Match(html);
                if (match.Success) { roomId = match.Groups[1].Value; break; }
            }

            if (roomId == null)
            {
                _logger.LogDebug("TikTok HTTP scrape: no roomId pattern matched for @{User} (html length={Len})",
                    username, html.Length);
                return null;
            }

            // Confirm the room is live — TikTok includes a roomId in the HTML even for offline
            // rooms (e.g. profile redirect, cached room data). Only proceed when status=2 is present.
            if (!LiveStatusPattern.IsMatch(html))
            {
                _logger.LogDebug("TikTok HTTP scrape: roomId found but status≠2 for @{User} — not live", username);
                return null;
            }

            return roomId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TikTok HTTP scrape failed for @{User}", username);
            return null;
        }
    }

    private async Task<string?> TryYtDlpAsync(string username, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = $"--dump-json --no-download \"https://www.tiktok.com/@{username}/live\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            // 12s cap — yt-dlp on a TikTok live URL was observed taking ~21s before failing.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));

            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                _logger.LogDebug("yt-dlp timeout (>12s) for TikTok @{User} — proceso terminado", username);
                return null;
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("yt-dlp failed for TikTok @{User} (exit code {Code})", username, process.ExitCode);
                return null;
            }

            // yt-dlp outputs JSON with an "id" field that is the room ID
            var json = JsonDocument.Parse(output);

            // is_live=false means yt-dlp found page data but the stream is offline
            if (json.RootElement.TryGetProperty("is_live", out var isLiveProp) && !isLiveProp.GetBoolean())
            {
                _logger.LogDebug("yt-dlp: is_live=false for TikTok @{User} — not live", username);
                return null;
            }

            if (json.RootElement.TryGetProperty("id", out var idProp))
                return idProp.GetString();

            // Also try "room_id"
            if (json.RootElement.TryGetProperty("room_id", out var roomProp))
                return roomProp.GetString();

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "yt-dlp room ID extraction failed for @{User}", username);
            return null;
        }
    }
}
