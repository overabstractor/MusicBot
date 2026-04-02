using System.Collections.Concurrent;
using MusicBot.Core.Models;
using TikTokLiveSharp.Client;
using TikTokLiveSharp.Events;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using KickChatSpy;
using KickChatSpy.Models;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;

namespace MusicBot.Services.Platforms;

public class PlatformConnectionManager
{
    public record TikTokPlatformConfig(string Username, string? SigningServerUrl = null, string? SigningServerApiKey = null, string? SessionId = null, string? CookieString = null);
    public record TwitchPlatformConfig(string Channel, string BotUsername, string OAuthToken);
    public record KickPlatformConfig(string Channel);

    public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

    public class ConnectionState
    {
        public ConnectionStatus Status  { get; set; } = ConnectionStatus.Disconnected;
        public string? ErrorMessage     { get; set; }
        public CancellationTokenSource? Cts { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConnectionState> _states = new();
    private readonly CommandRouterService _router;
    private readonly UserContextManager _userContext;
    private readonly PlaybackSyncService _sync;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly IntegrationStatusTracker _tracker;
    private readonly ChatResponseService _chat;
    private readonly TikTokRoomResolver _roomResolver;
    private readonly KickAuthService _kickAuth;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PlatformConnectionManager> _logger;

    // Active Twitch client for sending messages
    private TwitchClient? _activeTwitchClient;
    private string? _activeTwitchChannel;

    public PlatformConnectionManager(
        CommandRouterService router,
        UserContextManager userContext,
        PlaybackSyncService sync,
        IHubContext<OverlayHub> hub,
        IntegrationStatusTracker tracker,
        ChatResponseService chat,
        TikTokRoomResolver roomResolver,
        KickAuthService kickAuth,
        IHttpClientFactory httpFactory,
        ILogger<PlatformConnectionManager> logger)
    {
        _router       = router;
        _userContext  = userContext;
        _sync         = sync;
        _hub          = hub;
        _tracker      = tracker;
        _chat         = chat;
        _roomResolver = roomResolver;
        _kickAuth     = kickAuth;
        _httpFactory  = httpFactory;
        _logger       = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public ConnectionState GetState(Guid userId, string platform)
        => _states.GetOrAdd(Key(userId, platform), _ => new ConnectionState());

    public void ConnectTikTok(Guid userId, TikTokPlatformConfig config)
    {
        var key = Key(userId, "tiktok");
        Disconnect(userId, "tiktok");

        var cts = new CancellationTokenSource();
        var state = _states.AddOrUpdate(key, _ => new ConnectionState { Cts = cts }, (_, s) => { s.Cts = cts; return s; });
        state.Status = ConnectionStatus.Connecting;
        state.ErrorMessage = null;
        SetStatus(key, ConnectionStatus.Connecting);

        _ = Task.Run(() => RunWithReconnect(key, ct => TikTokLoop(userId, config, ct), cts.Token));
    }

    public void ConnectTwitch(Guid userId, TwitchPlatformConfig config)
    {
        var key = Key(userId, "twitch");
        Disconnect(userId, "twitch");

        var cts = new CancellationTokenSource();
        var state = _states.AddOrUpdate(key, _ => new ConnectionState { Cts = cts }, (_, s) => { s.Cts = cts; return s; });
        state.Status = ConnectionStatus.Connecting;
        state.ErrorMessage = null;
        SetStatus(key, ConnectionStatus.Connecting);

        _ = Task.Run(() => RunWithReconnect(key, ct => TwitchLoop(userId, config, ct), cts.Token));
    }

    public void ConnectKick(Guid userId, KickPlatformConfig config)
    {
        var key = Key(userId, "kick");
        Disconnect(userId, "kick");

        var cts = new CancellationTokenSource();
        var state = _states.AddOrUpdate(key, _ => new ConnectionState { Cts = cts }, (_, s) => { s.Cts = cts; return s; });
        state.Status = ConnectionStatus.Connecting;
        state.ErrorMessage = null;
        SetStatus(key, ConnectionStatus.Connecting);

        _ = Task.Run(() => RunWithReconnect(key, ct => KickLoop(userId, config, ct), cts.Token));
    }

    public void Disconnect(Guid userId, string platform)
    {
        var key = Key(userId, platform);
        if (_states.TryGetValue(key, out var state))
        {
            state.Cts?.Cancel();
            state.Status = ConnectionStatus.Disconnected;
            state.ErrorMessage = null;
        }

        SetStatus(key, ConnectionStatus.Disconnected);

        switch (platform)
        {
            case "twitch":
                _activeTwitchClient  = null;
                _activeTwitchChannel = null;
                _chat.RegisterSender("twitch", null);
                break;
            case "tiktok":
                _chat.RegisterSender("tiktok", null);
                break;
            case "kick":
                _chat.RegisterSender("kick", null);
                break;
        }
    }

    // ── Reconnect loop ────────────────────────────────────────────────────────

    private static readonly int[] ReconnectDelays = [5, 10, 20, 45, 60];

    private async Task RunWithReconnect(string key, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        // Brief startup delay: lets the previous loop detect its own cancellation before
        // this new loop begins, preventing two loops from running in parallel.
        try { await Task.Delay(150, ct); } catch (OperationCanceledException) { return; }

        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await run(ct);
                attempt = 0; // clean disconnect — reset backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // If TikTokLiveSharp hit a signing rate limit, respect the stated wait time.
                // Message format: "[429] Signing Rate Limit Reached. Try again in 00:21."
                int delaySec;
                var msg = ex.Message;
                if (msg.Contains("429") || msg.Contains("Rate Limit", StringComparison.OrdinalIgnoreCase))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(msg, @"(\d{1,2}):(\d{2})");
                    delaySec = m.Success
                        ? int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + 5
                        : 90;
                }
                else
                {
                    delaySec = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
                }

                attempt++;

                // Keep "Connecting" status — not "Error" — so the UI keeps showing
                // "Conectando..." and the user doesn't see the Connect button again.
                var retryMsg = $"{msg} — reintentando en {delaySec}s…";
                SetStatus(key, ConnectionStatus.Connecting, retryMsg);

                _logger.LogWarning("[{Key}] Error: {Msg}. Reconnecting in {Delay}s…",
                    key, msg, delaySec);

                try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        if (_states.TryGetValue(key, out var state))
        {
            state.Status       = ConnectionStatus.Disconnected;
            state.ErrorMessage = null;
        }
        SetStatus(key, ConnectionStatus.Disconnected);
    }

    // ── TikTok ────────────────────────────────────────────────────────────────

    private async Task TikTokLoop(Guid userId, TikTokPlatformConfig config, CancellationToken ct)
    {
        var key = Key(userId, "tiktok");
        var hasSign = !string.IsNullOrWhiteSpace(config.SigningServerUrl);

        // ── Wait for the streamer to go live ──────────────────────────────────
        // Polls every 60 s instead of throwing and letting RunWithReconnect retry with
        // short backoff — avoids hammering the signing server and keeps status "Connecting".
        const int LiveCheckSec = 60;
        string? roomId = null;
        while (!ct.IsCancellationRequested)
        {
            roomId = await _roomResolver.ResolveRoomIdAsync(config.Username, ct);
            if (roomId != null) break;

            _logger.LogInformation("TikTok @{User} no está en vivo — verificando en {Sec}s",
                config.Username, LiveCheckSec);
            SetStatus(key, ConnectionStatus.Connecting,
                $"@{config.Username} no está en vivo — verificando en {LiveCheckSec}s…");

            try { await Task.Delay(TimeSpan.FromSeconds(LiveCheckSec), ct); }
            catch (OperationCanceledException) { return; }
        }

        if (ct.IsCancellationRequested) return;

        _logger.LogDebug("TikTok room ID: {RoomId}", roomId);

        var client = new TikTokLiveClient(
            config.Username,
            roomId: roomId,
            skipRoomInfo: true,          // skip TikTok's own scraping entirely
            processInitialData: false,
            customSigningServer: hasSign ? config.SigningServerUrl : null,
            signingServerApiKey: hasSign ? config.SigningServerApiKey : null);

        var canSendChat = !string.IsNullOrWhiteSpace(config.CookieString)
                          || AppEvents.HasTikTokWebViewSender;

        string? tiktokRoomId = null;
        client.OnConnected += (_, _) =>
        {
            _logger.LogInformation("TikTok connected to @{User}", config.Username);
            SetStatus(key, ConnectionStatus.Connected);

            if (canSendChat || AppEvents.HasTikTokWebViewSender)
            {
                _chat.RegisterSender("tiktok", async msg =>
                    await SendTikTokChatAsync(config.CookieString, tiktokRoomId, msg));
                _logger.LogInformation("TikTok chat sender registered (webview={WebView}, http={Http})",
                    AppEvents.HasTikTokWebViewSender, !string.IsNullOrWhiteSpace(config.CookieString));
            }
            else
            {
                _logger.LogInformation("TikTok chat sending disabled — log in to TikTok from the Platforms panel");
            }
        };
        client.OnDisconnected += (_, _) =>
        {
            _logger.LogWarning("TikTok disconnected from @{User}", config.Username);
            _chat.RegisterSender("tiktok", null);
            SetStatus(key, ConnectionStatus.Disconnected);
        };
        client.OnChatMessage += (_, e) =>
        {
            // Capture roomId from first incoming message for sending
            if (tiktokRoomId == null && e.RoomId > 0)
                tiktokRoomId = e.RoomId.ToString();
            HandleTikTokChat(userId, e);
        };
        client.OnGiftMessage  += (_, e) => HandleTikTokGift(userId, e);

        _logger.LogInformation("TikTok connecting to @{User} (signing: {Signing}, chat-send: {CanSend})",
            config.Username, hasSign ? config.SigningServerUrl : "none", canSendChat);
        await client.RunAsync(ct);
    }

    private async void HandleTikTokChat(Guid userId, Chat e)
    {
        try
        {
            var username = e.Sender?.UniqueId ?? "viewer";
            var message  = e.Message?.Trim();
            if (string.IsNullOrEmpty(message)) return;

            _logger.LogDebug("TikTok chat @{User}: {Message}", username, message);

            if (!message.StartsWith('!')) return;
            await RouteCommand(userId, username, message, "tiktok");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TikTok chat handler error");
        }
    }

    private async void HandleTikTokGift(Guid userId, GiftMessage e)
    {
        try
        {
            var username = e.User?.UniqueId ?? "viewer";
            var giftName = e.Gift?.Name ?? "regalo";
            var diamonds = e.Gift?.DiamondCost ?? 0;
            var repeat   = (int)(e.RepeatCount > 0 ? e.RepeatCount : 1);
            var coins    = diamonds * repeat;

            if (coins <= 0) return;

            var slug = await GetUserSlugAsync(userId);
            if (slug == null) return;

            var services = await _userContext.GetBySlugAsync(slug);
            if (services == null) return;

            CommandResult result;
            if (coins >= 100)
            {
                var ok = services.Queue.InterruptForUser(username);
                if (!ok)
                    result = CommandResult.Fail($"@{username} no tiene canciones en la cola");
                else
                {
                    await _sync.StartCurrentTrackAsync(services);
                    result = CommandResult.Ok($"@{username} interrumpió con {coins} monedas!");
                }
            }
            else
            {
                for (int i = 0; i < coins; i++)
                    if (!services.Queue.Bump(username)) break;
                result = CommandResult.Ok($"@{username} subió su canción {coins} posición(es)");
            }

            await _hub.Clients.Group($"user:{userId}")
                .SendAsync("integration:event", new
                {
                    source  = "tiktok",
                    type    = "gift",
                    user    = username,
                    query   = $"{giftName} ×{repeat} ({coins} diamonds)",
                    success = result.Success,
                    message = result.Message,
                });

            await _chat.SendChatMessageAsync(username, result.Message, "tiktok");

            _logger.LogInformation("TikTok gift @{User}: {Gift} ×{Repeat} = {Coins} → {Result}",
                username, giftName, repeat, coins, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TikTok gift error");
        }
    }

    // ── Twitch ────────────────────────────────────────────────────────────────

    private async Task TwitchLoop(Guid userId, TwitchPlatformConfig config, CancellationToken ct)
    {
        var key = Key(userId, "twitch");
        var credentials = new ConnectionCredentials(config.BotUsername, config.OAuthToken);
        var client = new TwitchClient();
        client.Initialize(credentials, config.Channel);

        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetCanceled());

        client.OnConnected += async (_, _) =>
        {
            SetStatus(key, ConnectionStatus.Connected);
            _activeTwitchClient = client;
            _activeTwitchChannel = config.Channel;
            _chat.RegisterSender("twitch", async msg =>
            {
                if (client.IsConnected)
                {
                    await client.SendMessageAsync(config.Channel, msg);
                    return true;
                }
                return false;
            });
            _logger.LogInformation("Twitch connected to #{Channel}", config.Channel);
            await Task.CompletedTask;
        };

        client.OnDisconnected += async (_, _) =>
        {
            _activeTwitchClient = null;
            _activeTwitchChannel = null;
            _chat.RegisterSender("twitch", null);
            tcs.TrySetResult();
            await Task.CompletedTask;
        };

        client.OnMessageReceived += async (_, e) =>
        {
            var msg = e.ChatMessage.Message?.Trim();
            if (!string.IsNullOrEmpty(msg) && msg.StartsWith('!'))
                await RouteCommand(userId, e.ChatMessage.Username, msg, "twitch");
        };

        _logger.LogInformation("Twitch connecting to #{Channel}", config.Channel);
        await client.ConnectAsync();

        try { await tcs.Task; }
        catch (TaskCanceledException) { }

        _activeTwitchClient = null;
        _activeTwitchChannel = null;
        _chat.RegisterSender("twitch", null);
        await client.DisconnectAsync();
    }

    // ── Kick ──────────────────────────────────────────────────────────────────

    private async Task KickLoop(Guid userId, KickPlatformConfig config, CancellationToken ct)
    {
        var key = Key(userId, "kick");
        var client = new KickChatClient();

        client.OnMessageReceived += msg =>
        {
            var content = msg.Content?.Trim();
            if (!string.IsNullOrEmpty(content) && content.StartsWith('!'))
                _ = RouteCommand(userId, msg.Sender?.Username ?? "viewer", content, "kick");
        };

        _logger.LogInformation("Kick connecting to channel {Channel}", config.Channel);
        await client.ConnectToChatroomAsync(config.Channel);
        SetStatus(key, ConnectionStatus.Connected);
        _logger.LogInformation("Kick connected to channel {Channel}", config.Channel);

        if (_kickAuth.IsAuthenticated)
        {
            _chat.RegisterSender("kick", async msg => await _kickAuth.SendChatMessageAsync(msg));
            _logger.LogInformation("Kick chat sender registered");
        }

        try
        {
            while (!ct.IsCancellationRequested && client.IsConnected)
                await Task.Delay(1000, ct);
        }
        finally
        {
            _chat.RegisterSender("kick", null);
            await client.DisconnectAsync();
        }
    }

    // ── Shared command routing ────────────────────────────────────────────────

    private async Task<CommandResult?> RouteCommand(Guid userId, string username, string message, string platform)
    {
        var parts = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd   = parts[0].ToLowerInvariant();
        var args  = parts.Length > 1 ? parts[1].Trim() : "";

        BotCommand? command = cmd switch
        {
            "!play" or "!sr" when !string.IsNullOrEmpty(args) =>
                new BotCommand { Type = "play",     Query = args, RequestedBy = username, Platform = platform },
            "!skip" =>
                new BotCommand { Type = "selfskip", RequestedBy = username, Platform = platform },
            "!si" or "!yes" =>
                new BotCommand { Type = "si",       RequestedBy = username, Platform = platform },
            "!no" =>
                new BotCommand { Type = "no",       RequestedBy = username, Platform = platform },
            "!revoke" or "!quitar" =>
                new BotCommand { Type = "revoke",   RequestedBy = username, Platform = platform },
            "!info" =>
                new BotCommand { Type = "info",     RequestedBy = username, Platform = platform },
            "!aqui" or "!here" =>
                new BotCommand { Type = "aqui",     RequestedBy = username, Platform = platform },
            "!keep" =>
                new BotCommand { Type = "keep",     RequestedBy = username, Platform = platform },
            _ => null
        };

        if (command == null) return null;

        var slug = await GetUserSlugAsync(userId);
        if (slug == null)
        {
            _logger.LogWarning("[{Platform}] No slug cached for user {UserId} — call SetUserSlug first", platform, userId);
            return null;
        }

        var services = await _userContext.GetBySlugAsync(slug);
        if (services == null)
        {
            _logger.LogWarning("[{Platform}] No services found for slug '{Slug}'", platform, slug);
            return null;
        }

        var result = await _router.HandleAsync(command, services);
        _logger.LogInformation("[{Platform}] [{Cmd}] @{User}: {Result}", platform, cmd, username, result.Message);

        // Broadcast event to frontend
        await _hub.Clients.Group($"user:{userId}")
            .SendAsync("integration:event", new
            {
                source  = platform,
                type    = "play",
                platform = platform[0..1].ToUpper() + platform[1..],
                user    = username,
                query   = command.Query ?? "",
                success = result.Success,
                message = result.Message,
            });

        // Send response back to platform chat
        await _chat.SendChatMessageAsync(username, result.Message, platform);

        return result;
    }

    // ── TikTok chat sender ─────────────────────────────────────────────────────

    private async Task<bool> SendTikTokChatAsync(string? cookieString, string? roomId, string message)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            _logger.LogWarning("TikTok send: roomId not captured yet — waiting for first chat message to arrive");
            return false;
        }

        // Primary: execute fetch() inside the authenticated WebView2 window.
        // TikTok's own JS SDK then adds X-Bogus, X-Gnarly, tt-ticket-guard-* automatically.
        _logger.LogDebug("TikTok send attempt — webview={WebView} hasCookie={HasCookie}",
            AppEvents.HasTikTokWebViewSender, !string.IsNullOrWhiteSpace(cookieString));
        if (AppEvents.HasTikTokWebViewSender)
        {
            try
            {
                var ok = await AppEvents.SendViaTikTokWebView(roomId, message);
                if (ok)
                {
                    _logger.LogInformation("TikTok chat sent via WebView: {Message}", message);
                    return true;
                }
                _logger.LogWarning("TikTok WebView send failed — falling back to HTTP");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TikTok WebView send error — falling back to HTTP");
            }
        }

        // Fallback: direct HTTP (requires valid CookieString with tt-csrf-token)
        if (string.IsNullOrWhiteSpace(cookieString))
        {
            _logger.LogWarning("TikTok send: no WebView sender and no cookie string — log in to TikTok from the Platforms panel");
            return false;
        }

        try
        {
            var http = _httpFactory.CreateClient();

            // Full query string matching what a real Chrome browser sends to this endpoint.
            // TikTok's WAF validates browser-fingerprint fields in the QS — missing them → 403.
            var qs = "?aid=1988" +
                     "&app_language=en-US" +
                     "&app_name=tiktok_web" +
                     "&browser_language=en-US" +
                     "&browser_name=Mozilla" +
                     "&browser_online=true" +
                     "&browser_platform=Win32" +
                     "&browser_version=5.0%20(Windows%20NT%2010.0%3B%20Win64%3B%20x64)" +
                     "&cookie_enabled=true" +
                     "&device_platform=web_pc" +
                     "&focus_state=true" +
                     "&from_page=live" +
                     "&history_len=5" +
                     "&is_fullscreen=false" +
                     "&is_page_visible=true" +
                     $"&room_id={roomId}";

            // POST body: JSON (as seen in real browser requests)
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var jsonBody = System.Text.Json.JsonSerializer.Serialize(new
            {
                room_id = roomId,
                content = message,
                emotes_with_index = "",
                input_type = 0,
                client_start_timestamp_millisecond = ts,
            });

            var request = new HttpRequestMessage(HttpMethod.Post,
                "https://webcast.tiktok.com/webcast/room/chat/" + qs + $"&client_start_timestamp_millisecond={ts}");

            // Full browser-like headers — Cloudflare checks Sec-Fetch-* and Sec-Ch-Ua-*
            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Cookie", cookieString);
            request.Headers.Add("Origin", "https://www.tiktok.com");
            request.Headers.Add("Referer", "https://www.tiktok.com/");
            request.Headers.Add("Sec-Ch-Ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"131\", \"Google Chrome\";v=\"131\"");
            request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
            request.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
            request.Headers.Add("Sec-Fetch-Dest", "empty");
            request.Headers.Add("Sec-Fetch-Mode", "cors");
            request.Headers.Add("Sec-Fetch-Site", "same-site");
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            var csrfToken      = ExtractCookieValue(cookieString, "tt-csrf-token");
            var csrfSessionId  = ExtractCookieValue(cookieString, "csrf_session_id");
            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                // x-secsdk-csrf-token = "<computed_token>,<csrf_session_id>"
                var csrfHeader = string.IsNullOrWhiteSpace(csrfSessionId)
                    ? csrfToken
                    : $"{csrfToken},{csrfSessionId}";
                request.Headers.Add("X-Secsdk-Csrf-Token", csrfHeader);
            }

            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("TikTok chat POST → {Status} | csrf={HasCsrf} | body={Body}",
                (int)response.StatusCode, csrfToken != null, body.Length > 300 ? body[..300] : body);

            if (response.IsSuccessStatusCode && body.Contains("\"status_code\":0"))
            {
                _logger.LogInformation("TikTok chat sent OK: {Message}", message);
                return true;
            }

            // TikTok status_code values:
            // 20003 = sessionId expired       → hacer login de nuevo en el panel de Plataformas
            // 20001 = X-Bogus signing req.    → la cuenta/región requiere signing server
            // 4003001 = no autenticado        → sessionid incorrecto o expirado
            // 3 = rate limited                → enviando demasiado rápido
            // 403 empty = WAF block           → headers de browser incorrectos o IP bloqueada
            _logger.LogWarning("TikTok chat send failed — status={Status} body={Body}",
                (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TikTok chat send error");
            return false;
        }
    }

    /// <summary>Extracts a single cookie value from a browser cookie string (key=value; key2=value2).</summary>
    private static string? ExtractCookieValue(string cookieString, string name)
    {
        foreach (var part in cookieString.Split(';'))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && trimmed[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(trimmed[(eq + 1)..].Trim());
        }
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Key(Guid userId, string platform) => $"{userId}:{platform}";

    private void SetStatus(string key, ConnectionStatus status, string? message = null)
    {
        if (_states.TryGetValue(key, out var s))
        {
            s.Status       = status;
            s.ErrorMessage = message;
        }

        // Sync with IntegrationStatusTracker + broadcast to frontend
        var parts = key.Split(':', 2);
        if (parts.Length == 2)
        {
            var platform = parts[1];
            var statusStr = status.ToString().ToLower();

            switch (platform)
            {
                case "tiktok": _tracker.TikTokStatus = statusStr; break;
                case "twitch": _tracker.TwitchStatus = statusStr; break;
                case "kick":   _tracker.KickStatus   = statusStr; break;
            }

            _ = _hub.Clients.Group($"user:{parts[0]}")
                .SendAsync("integration:status", new { source = platform, status = statusStr });
        }
    }

    // Cache user slug per userId (avoids repeated DB lookups on every chat message)
    private readonly ConcurrentDictionary<Guid, string> _slugCache = new();

    public void SetUserSlug(Guid userId, string slug) => _slugCache[userId] = slug;

    private Task<string?> GetUserSlugAsync(Guid userId)
        => Task.FromResult(_slugCache.TryGetValue(userId, out var slug) ? slug : null);
}
