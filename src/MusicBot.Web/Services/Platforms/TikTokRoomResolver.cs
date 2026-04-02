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
    private readonly ILogger<TikTokRoomResolver> _logger;
    private readonly string _ytDlpPath;

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

    public TikTokRoomResolver(
        IHttpClientFactory httpFactory,
        IOptions<MusicLibrarySettings> libSettings,
        ILogger<TikTokRoomResolver> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
        _ytDlpPath   = libSettings.Value.YtDlpPath ?? "yt-dlp";
    }

    /// <summary>
    /// Attempts to resolve the room ID for a TikTok LIVE streamer.
    /// Returns null if the user is not live or the room ID cannot be found.
    /// </summary>
    public async Task<string?> ResolveRoomIdAsync(string username, CancellationToken ct = default)
    {
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
            _logger.LogDebug("TikTok WebView resolver returned null for @{User} — trying fallbacks", username);
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

    private async Task<string?> TryHttpScrapeAsync(string username, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.tiktok.com/@{username}/live");
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Sec-Fetch-Site", "none");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Sec-Fetch-Dest", "document");

            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("TikTok HTTP scrape failed: {Status}", response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);

            foreach (var pattern in RoomIdPatterns)
            {
                var match = pattern.Match(html);
                if (match.Success) return match.Groups[1].Value;
            }

            _logger.LogDebug("TikTok HTTP scrape: no roomId pattern matched for @{User} (html length={Len})",
                username, html.Length);
            return null;
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

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("yt-dlp failed for TikTok @{User} (exit code {Code})", username, process.ExitCode);
                return null;
            }

            // yt-dlp outputs JSON with an "id" field that is the room ID
            var json = JsonDocument.Parse(output);
            if (json.RootElement.TryGetProperty("id", out var idProp))
                return idProp.GetString();

            // Also try "room_id" or "webpage_url" containing the room ID
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
