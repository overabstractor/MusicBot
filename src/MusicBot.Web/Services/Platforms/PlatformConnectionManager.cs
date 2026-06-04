using System.Collections.Concurrent;
using System.Text.Json;
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
    public record TikTokPlatformConfig(string Username, string? SigningServerUrl = null, string? SigningServerApiKey = null, string? SessionId = null, string? CookieString = null, int GiftInterruptThreshold = 100, bool GiftBumpEnabled = true, bool GiftInterruptEnabled = true, int CoinsPerBump = 1, string[]? CommandRoles = null, int TeamMinLevel = 1, string[]? AllowedUsers = null);
    public record TwitchPlatformConfig(string Channel, string BotUsername, string OAuthToken, string[]? CommandRoles = null, string[]? AllowedUsers = null);
    public record KickPlatformConfig(string Channel, string[]? CommandRoles = null, string[]? AllowedUsers = null);

    // WaitingLive is TikTok-specific: the bot is armed and polling, but the host is not
    // streaming yet, so there is no chat to join. It is deliberately distinct from Connecting
    // (which means "actively opening the WebSocket") so the UI can say "Esperando tu Live"
    // instead of implying an active connection. ToString().ToLower() => "waitinglive".
    public enum ConnectionStatus { Disconnected, Connecting, WaitingLive, Connected, Error }

    public class ConnectionState
    {
        public ConnectionStatus Status  { get; set; } = ConnectionStatus.Disconnected;
        public string? ErrorMessage     { get; set; }
        public CancellationTokenSource? Cts { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConnectionState> _states = new();

    // Live configs — kept in memory so settings changes apply mid-stream
    // without needing to disconnect/reconnect.
    private readonly ConcurrentDictionary<Guid, TikTokPlatformConfig> _tikTokConfigs = new();
    private readonly ConcurrentDictionary<Guid, TwitchPlatformConfig> _twitchConfigs = new();
    private readonly ConcurrentDictionary<Guid, KickPlatformConfig>   _kickConfigs   = new();

    // Per-(platform,user) cooldown for "no permission" messages to avoid spam
    private readonly ConcurrentDictionary<string, DateTime> _deniedNotifiedAt = new();
    private static readonly TimeSpan DeniedCooldown = TimeSpan.FromSeconds(60);

    private readonly CommandRouterService _router;
    private readonly UserContextManager _userContext;
    private readonly PlaybackSyncService _sync;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly IntegrationStatusTracker _tracker;
    private readonly ChatResponseService _chat;
    private readonly ChatActivityTracker _activity;
    private readonly TikTokRoomResolver _roomResolver;
    private readonly TikTokAuthService _tikTokAuth;
    private readonly KickAuthService _kickAuth;
    private readonly TwitchFollowerCache _twitchFollowers;
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
        ChatActivityTracker activity,
        TikTokRoomResolver roomResolver,
        TikTokAuthService tikTokAuth,
        KickAuthService kickAuth,
        TwitchFollowerCache twitchFollowers,
        IHttpClientFactory httpFactory,
        ILogger<PlatformConnectionManager> logger)
    {
        _router          = router;
        _userContext     = userContext;
        _sync            = sync;
        _hub             = hub;
        _tracker         = tracker;
        _chat            = chat;
        _activity        = activity;
        _roomResolver    = roomResolver;
        _tikTokAuth      = tikTokAuth;
        _kickAuth        = kickAuth;
        _twitchFollowers = twitchFollowers;
        _httpFactory     = httpFactory;
        _logger          = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public ConnectionState GetState(Guid userId, string platform)
        => _states.GetOrAdd(Key(userId, platform), _ => new ConnectionState());

    public void ConnectTikTok(Guid userId, TikTokPlatformConfig config)
    {
        var key = Key(userId, "tiktok");
        Disconnect(userId, "tiktok");

        _tikTokConfigs[userId] = config;

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

        _twitchConfigs[userId] = config;

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

        _kickConfigs[userId] = config;

        var cts = new CancellationTokenSource();
        var state = _states.AddOrUpdate(key, _ => new ConnectionState { Cts = cts }, (_, s) => { s.Cts = cts; return s; });
        state.Status = ConnectionStatus.Connecting;
        state.ErrorMessage = null;
        SetStatus(key, ConnectionStatus.Connecting);

        _ = Task.Run(() => RunWithReconnect(key, ct => KickLoop(userId, config, ct), cts.Token));
    }

    // ── Live config updates (applied mid-stream without reconnecting) ────────

    public void UpdateTikTokSettings(Guid userId,
        int giftInterruptThreshold, bool giftBumpEnabled, bool giftInterruptEnabled,
        int coinsPerBump, string[] commandRoles, int teamMinLevel, string[] allowedUsers)
    {
        if (!_tikTokConfigs.TryGetValue(userId, out var current))
        {
            _logger.LogDebug("UpdateTikTokSettings: no active TikTok connection for user {UserId}", userId);
            return;
        }
        _tikTokConfigs[userId] = current with
        {
            GiftInterruptThreshold = giftInterruptThreshold,
            GiftBumpEnabled        = giftBumpEnabled,
            GiftInterruptEnabled   = giftInterruptEnabled,
            CoinsPerBump           = coinsPerBump,
            CommandRoles           = commandRoles,
            TeamMinLevel           = teamMinLevel,
            AllowedUsers           = allowedUsers,
        };
        ClearDeniedCooldownsForPlatform("tiktok");
        _logger.LogInformation("TikTok live config updated: roles=[{Roles}] gifts={Gifts}",
            string.Join(",", commandRoles),
            $"bumpEn={giftBumpEnabled},intEn={giftInterruptEnabled},thr={giftInterruptThreshold},cpb={coinsPerBump}");
    }

    public void UpdateTwitchSettings(Guid userId, string[] commandRoles, string[] allowedUsers)
    {
        if (!_twitchConfigs.TryGetValue(userId, out var current))
        {
            _logger.LogDebug("UpdateTwitchSettings: no active Twitch connection for user {UserId}", userId);
            return;
        }
        _twitchConfigs[userId] = current with
        {
            CommandRoles = commandRoles,
            AllowedUsers = allowedUsers,
        };
        ClearDeniedCooldownsForPlatform("twitch");
        _logger.LogInformation("Twitch live config updated: roles=[{Roles}]", string.Join(",", commandRoles));
    }

    public void UpdateKickSettings(Guid userId, string[] commandRoles, string[] allowedUsers)
    {
        if (!_kickConfigs.TryGetValue(userId, out var current))
        {
            _logger.LogDebug("UpdateKickSettings: no active Kick connection for user {UserId}", userId);
            return;
        }
        _kickConfigs[userId] = current with
        {
            CommandRoles = commandRoles,
            AllowedUsers = allowedUsers,
        };
        ClearDeniedCooldownsForPlatform("kick");
        _logger.LogInformation("Kick live config updated: roles=[{Roles}]", string.Join(",", commandRoles));
    }

    /// <summary>Clears anti-spam cooldowns so users get fresh feedback after a permission change.</summary>
    private void ClearDeniedCooldownsForPlatform(string platform)
    {
        var prefix = $"{platform}:";
        foreach (var k in _deniedNotifiedAt.Keys.ToList())
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _deniedNotifiedAt.TryRemove(k, out _);
    }

    // ── Permission denied feedback (rate-limited per user/platform) ──────────

    private async Task NotifyPermissionDeniedAsync(string platform, string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var key = $"{platform}:{username.ToLowerInvariant()}";
        var now = DateTime.UtcNow;
        if (_deniedNotifiedAt.TryGetValue(key, out var last) && now - last < DeniedCooldown) return;
        _deniedNotifiedAt[key] = now;
        try
        {
            await _chat.SendChatMessageAsync(username, "no tienes permiso para usar comandos", platform);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send permission-denied feedback");
        }
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
                _twitchConfigs.TryRemove(userId, out _);
                break;
            case "tiktok":
                _chat.RegisterSender("tiktok", null);
                _tikTokConfigs.TryRemove(userId, out _);
                break;
            case "kick":
                _chat.RegisterSender("kick", null);
                _kickConfigs.TryRemove(userId, out _);
                break;
        }
    }

    // ── Reconnect loop ────────────────────────────────────────────────────────

    private static readonly int[] ReconnectDelays = [2, 5, 10, 20, 45];

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

        // Resolve the Euler Stream signing tier ONCE per connection (not per WS retry).
        // Never silently fall back to the anonymous/rate-limited tier if a key is available.
        var signing = ResolveSigningConfig(config);

        // ── Tunable timings (kept low for fast recovery from transient drops) ─
        // Two backoff profiles depending on which resolve path is available:
        //
        //  • webViewLiveCheckDelaysSec — used when the authenticated WebView resolver is
        //    registered (self-query of the logged-in host's OWN live). That path runs the
        //    fetch from a real browser with session cookies, bypassing TikTok's bot-detection,
        //    so we can poll frequently and pick up "just went live" within seconds. This is
        //    the common case for the host and the one users notice: without it, a live that
        //    starts long after the bot booted wasn't detected until the next 5-min poll, so
        //    chat commands were ignored until a manual reconnect.
        //
        //  • httpLiveCheckDelaysSec — used for the pure-HTTP fallback (other creators, or the
        //    WebView resolver not yet registered). TikTok rate-limits the /@user/live scrape
        //    after ~6 fetches (returns a 1155-byte stub with no roomId for several minutes),
        //    so this profile spaces out aggressively to stay under the threshold.
        int[] webViewLiveCheckDelaysSec = [10, 10, 15, 15, 20, 20, 30];
        int[] httpLiveCheckDelaysSec    = [20, 20, 20, 60, 60, 60, 300];
        const int WatchdogSec        = 60;   // how often watchdog checks WS liveness
        const int MaxSilenceMinutes  = 10;   // force reconnect if WS shows connected but no events
        const int EndedCooldownSec   = 5;    // cooldown after natural stream end
        int[] wsRetryDelays          = [1, 2, 5, 10, 20]; // fast local backoff for WS-level errors
        int wsFailCount = 0;

        // Startup gate: if we have a saved TikTok session, wait for the WebView2 resolver to
        // register before polling. The resolver takes several seconds to come up (it has to
        // navigate to tiktok.com and read the sessionid cookie). Without it, the very first
        // resolve attempt falls back to HTTP — which returns 404 on /anchorinfo/ for self-query
        // and a rate-limited HTML stub from /@user/live. This race burned the first 20s+ of
        // every cold start. Observed register times: 5s on warm net, 36s on slow net.
        if (_tikTokAuth.IsAuthenticated && !AppEvents.HasTikTokRoomIdResolver)
        {
            SetStatus(key, ConnectionStatus.Connecting, "Esperando sesión WebView de TikTok…");
            _logger.LogInformation("TikTok @{User}: esperando que el resolver WebView se registre…", config.Username);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var maxWait = TimeSpan.FromSeconds(90);
            while (sw.Elapsed < maxWait && !AppEvents.HasTikTokRoomIdResolver && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(250, ct); }
                catch (OperationCanceledException) { return; }
            }
            if (AppEvents.HasTikTokRoomIdResolver)
                _logger.LogInformation("TikTok WebView resolver listo tras {Sec:F1}s — conectando", sw.Elapsed.TotalSeconds);
            else
                _logger.LogWarning("TikTok WebView resolver no se registró en {Sec}s — continuando con HTTP fallbacks", maxWait.TotalSeconds);
        }

        // Persist across reconnects so we can reuse the roomId after a watchdog kill
        // without re-querying TikTok (which triggers the bot-detection rate-limit and
        // returns 1155-byte stubs for ~30 min, breaking all subsequent attempts).
        string? lastKnownRoomId = null;
        bool reuseLastRoomId = false;

        while (!ct.IsCancellationRequested)
        {
            // ── Wait for the streamer to go live ──────────────────────────────
            string? roomId = null;

            if (reuseLastRoomId && lastKnownRoomId != null)
            {
                _logger.LogInformation(
                    "TikTok @{User}: reutilizando roomId {RoomId} tras watchdog kill (evitar rate-limit)",
                    config.Username, lastKnownRoomId);
                roomId = lastKnownRoomId;
                reuseLastRoomId = false; // only one shot; if it fails we re-resolve next round
            }
            else
            {
                int consecutiveNotLive = 0;
                while (!ct.IsCancellationRequested)
                {
                    roomId = await SafeResolveRoomIdAsync(config.Username, ct);
                    if (roomId != null) break;

                    // Pick the backoff profile dynamically: the WebView resolver may register a
                    // few seconds after this loop starts (it has to navigate to tiktok.com and
                    // read the sessionid cookie), so re-evaluate each iteration instead of caching.
                    var delays = AppEvents.HasTikTokRoomIdResolver
                        ? webViewLiveCheckDelaysSec
                        : httpLiveCheckDelaysSec;
                    var idx = Math.Min(consecutiveNotLive, delays.Length - 1);
                    var delaySec = delays[idx];

                    _logger.LogInformation("TikTok @{User} no está en vivo — verificando en {Sec}s (intento #{N})",
                        config.Username, delaySec, consecutiveNotLive + 1);
                    SetStatus(key, ConnectionStatus.WaitingLive,
                        $"@{config.Username} no está en vivo — esperando que inicies tu Live (revisando cada {delaySec}s)");

                    try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
                    catch (OperationCanceledException) { return; }
                    consecutiveNotLive++;
                }
            }

            if (ct.IsCancellationRequested) return;
            if (roomId == null) continue;
            lastKnownRoomId = roomId;

            // Live found — now opening the WebSocket. Flip to Connecting so the UI moves from
            // "Esperando tu Live" to "Conectando…" during the (brief) handshake before OnConnected.
            SetStatus(key, ConnectionStatus.Connecting, $"@{config.Username} está en vivo — conectando al chat…");

            _logger.LogDebug("TikTok room ID: {RoomId}", roomId);

            var client = new TikTokLiveClient(
                config.Username,
                roomId: roomId,
                skipRoomInfo: true,
                processInitialData: false,
                customSigningServer: signing.Server,   // null → Euler Stream default (tiktok.eulerstream.com)
                signingServerApiKey: signing.ApiKey);   // key → authenticated tier; null → anonymous (rate-limited)

            var canSendChat = !string.IsNullOrWhiteSpace(config.CookieString)
                              || AppEvents.HasTikTokWebViewSender;

            string? tiktokRoomId = null;
            var lastEventAt = DateTime.UtcNow;
            var connectedSuccessfully = false;
            var connectSw = System.Diagnostics.Stopwatch.StartNew(); // tiktok_connect_latency_ms

            client.OnConnected += (_, _) =>
            {
                connectedSuccessfully = true;
                wsFailCount = 0; // reset fast-retry backoff once we're actually connected
                lastEventAt = DateTime.UtcNow;
                connectSw.Stop();
                _logger.LogInformation("TikTok connected to @{User} (signing_mode={Mode}, connect_latency_ms={Latency})",
                    config.Username, signing.Mode, connectSw.ElapsedMilliseconds);
                _activity.SetIgnored(config.Username, true);
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
                _logger.LogInformation("TikTok disconnected from @{User}", config.Username);
                _activity.SetIgnored(config.Username, false);
                _chat.RegisterSender("tiktok", null);
                SetStatus(key, ct.IsCancellationRequested
                    ? ConnectionStatus.Disconnected
                    : ConnectionStatus.Connecting);
            };
            // OnMessage dispara para CUALQUIER mensaje del WebSocket — likes, joins, follows,
            // gifts, control messages, etc. Es la única forma confiable de detectar que el WS
            // sigue vivo. Subscribirse solo a OnChatMessage/OnGiftMessage perdía actividad de
            // lives sin chat (resultaba en false-positives del watchdog tras 10min "sin eventos").
            client.OnMessage += (_, _) => lastEventAt = DateTime.UtcNow;
            client.OnChatMessage += (_, e) =>
            {
                lastEventAt = DateTime.UtcNow;
                if (tiktokRoomId == null && e.RoomId > 0)
                    tiktokRoomId = e.RoomId.ToString();
                HandleTikTokChat(userId, e);
            };
            client.OnGiftMessage += (_, e) =>
            {
                lastEventAt = DateTime.UtcNow;
                HandleTikTokGift(userId, e);
            };

            _logger.LogInformation("TikTok connecting to @{User} (signing_mode={Mode}, server={Server}, chat-send={CanSend})",
                config.Username, signing.Mode, signing.Server ?? "eulerstream-default", canSendChat);

            // Watchdog. Only role now is detecting a dead WebSocket — natural stream-end
            // is handled by the Stream_Ended ControlMessage handler above (fires within
            // seconds). The previous version also re-resolved the room ID mid-stream to
            // catch "still live?" — that consumed TikTok's per-session rate limit and
            // eventually returned the 1155-byte bot-detection stub, breaking reconnects.
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var streamEndedByWatchdog = false;

            // TikTok emits a ControlMessage with Action=Stream_Ended the moment the
            // creator closes the live. Cancel immediately instead of waiting for the
            // watchdog. Any other control message still counts as liveness — it means
            // TikTok's WS is alive even if no human is chatting.
            client.OnControlMessage += (_, e) =>
            {
                lastEventAt = DateTime.UtcNow;
                if (e.Action == TikTokLiveSharp.Events.Enums.ControlAction.Stream_Ended)
                {
                    _logger.LogInformation("TikTok @{User}: Stream_Ended recibido — cerrando", config.Username);
                    streamEndedByWatchdog = true;
                    streamCts.Cancel();
                }
            };

            var runTask = client.RunAsync(streamCts.Token);
            var watchdogTask = Task.Run(async () =>
            {
                try
                {
                    while (!streamCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(WatchdogSec), streamCts.Token);

                        if (!connectedSuccessfully) continue;

                        var silenceSec = (DateTime.UtcNow - lastEventAt).TotalSeconds;
                        if (silenceSec / 60.0 > MaxSilenceMinutes)
                        {
                            _logger.LogWarning(
                                "TikTok @{User} sin eventos por >{Min}min — WS aparenta muerto, forzando reconexión",
                                config.Username, MaxSilenceMinutes);
                            streamEndedByWatchdog = true;
                            streamCts.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, streamCts.Token);

            bool wsError = false;
            try { await runTask; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // watchdog cancelled — natural stream end or heartbeat timeout
            }
            catch (Exception ex)
            {
                // WS-level error → handle locally with fast retry (don't bubble to slow RunWithReconnect)
                wsError = true;
                wsFailCount++;
                var delay = wsRetryDelays[Math.Min(wsFailCount - 1, wsRetryDelays.Length - 1)];
                _logger.LogWarning(ex,
                    "TikTok @{User} WS error — reintentando en {Delay}s (intento {N})",
                    config.Username, delay, wsFailCount);
                SetStatus(key, ConnectionStatus.Connecting,
                    $"Error temporal — reintentando en {delay}s…");
                streamCts.Cancel();
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException) { return; }
            }
            try { await watchdogTask; } catch { /* swallow */ }

            // If the watchdog killed us (not a clean stream end), the live MIGHT still be on —
            // TikTok just stopped sending events for 10+ min. Try reconnecting with the same
            // roomId next iteration to avoid hammering the resolver (which would get rate-limited).
            // If the live actually ended, the WS will fail and we'll re-resolve normally.
            if (streamEndedByWatchdog && !ct.IsCancellationRequested)
                reuseLastRoomId = true;

            // After natural stream-end or watchdog cancel, do brief cooldown then poll again.
            // After WS error, we already waited — go straight to live check (skip cooldown).
            if (!wsError && !ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    streamEndedByWatchdog
                        ? "TikTok @{User} stream ended — checking live again in {Sec}s"
                        : "TikTok @{User} disconnected — checking live again in {Sec}s",
                    config.Username, EndedCooldownSec);
                SetStatus(key, ConnectionStatus.Connecting,
                    $"@{config.Username} — verificando en {EndedCooldownSec}s…");
                try { await Task.Delay(TimeSpan.FromSeconds(EndedCooldownSec), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Resolves the TikTok signing configuration for one connection, choosing the Euler Stream
    /// tier ONCE (mirrors OverInteractive's BuildSigningConfig). TikTokLiveSharp's built-in
    /// default signer is https://tiktok.eulerstream.com/webcast/fetch, so a null server means
    /// "use Euler's default"; the API key (when present) promotes the request from the anonymous,
    /// rate-limited tier to the authenticated, higher-quota tier.
    ///
    /// Priority (high → low):
    ///   1. Explicit signing-server URL (config) — escape hatch for a self-hosted signer
    ///      (e.g. the in-app WebView2 proxy at /webcast/fetch). Mode "custom".
    ///   2. Euler Stream API key (config/env) — authenticated tier. Mode "eulerstream-config".
    ///   3. Shared product key (Cloudflare relay/Worker) — Mode "eulerstream-shared".
    ///      Not yet wired: pass it via <paramref name="sharedKey"/> once the relay exposes one.
    ///   4. Nothing — anonymous Euler default, rate-limited. Mode "eulerstream-free".
    /// </summary>
    private static (string? Server, string? ApiKey, string Mode) ResolveSigningConfig(
        TikTokPlatformConfig config, string? sharedKey = null)
    {
        var explicitUrl = config.SigningServerUrl;
        var configKey   = config.SigningServerApiKey;

        string? apiKey; string mode;
        if      (!string.IsNullOrWhiteSpace(configKey)) { apiKey = configKey; mode = "eulerstream-config"; }
        else if (!string.IsNullOrWhiteSpace(sharedKey)) { apiKey = sharedKey; mode = "eulerstream-shared"; }
        else                                            { apiKey = null;      mode = "eulerstream-free";   }

        // An explicit signing-server URL wins on the server slot but keeps whatever key was
        // resolved above (a custom Euler-compatible server may still want it; the local
        // WebView2 proxy simply ignores it).
        return string.IsNullOrWhiteSpace(explicitUrl)
            ? (null, apiKey, mode)
            : (explicitUrl, apiKey, "custom");
    }

    /// <summary>ResolveRoomId wrapper that never throws on transient errors — only OCE bubbles up.</summary>
    private async Task<string?> SafeResolveRoomIdAsync(string username, CancellationToken ct)
    {
        try { return await _roomResolver.ResolveRoomIdAsync(username, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TikTok ResolveRoomId transient failure for @{User}", username);
            return null;
        }
    }

    private async void HandleTikTokChat(Guid userId, Chat e)
    {
        try
        {
            var username = e.Sender?.UniqueId ?? "viewer";
            var message  = e.Message?.Trim();
            if (string.IsNullOrEmpty(message)) return;

            _activity.RecordMessage(username);
            _logger.LogDebug("TikTok chat @{User}: {Message}", username, message);

            if (message[0] is not ('!' or '.' or '/')) return;
            if (!_tikTokConfigs.TryGetValue(userId, out var config)) return;
            if (e.Sender != null && !IsTikTokRoleAllowed(e.Sender, config.CommandRoles, config.TeamMinLevel, config.AllowedUsers))
            {
                await NotifyPermissionDeniedAsync("tiktok", username);
                return;
            }
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
            // Ignore intermediate combo events; only process when the streak ends
            if (!e.StreakEnd) return;
            if (!_tikTokConfigs.TryGetValue(userId, out var config)) return;

            var username = e.User?.UniqueId ?? "viewer";
            var giftName = e.Gift?.Name ?? "regalo";
            var diamonds = e.Gift?.DiamondCost ?? 0;
            var repeat   = (int)(e.RepeatCount > 0 ? e.RepeatCount : 1);
            var coins    = diamonds * repeat;

            if (coins <= 0) return;
            if (!config.GiftBumpEnabled && !config.GiftInterruptEnabled) return;

            var slug = await GetUserSlugAsync(userId);
            if (slug == null) return;

            var services = await _userContext.GetBySlugAsync(slug);
            if (services == null) return;

            CommandResult result;
            if (coins >= config.GiftInterruptThreshold && config.GiftInterruptEnabled)
            {
                var ok = services.Queue.InterruptForUser(username);
                if (!ok) return;

                await _sync.StartCurrentTrackAsync(services);
                result = CommandResult.Ok($"@{username} interrumpió con {coins} monedas!");
            }
            else if (coins < config.GiftInterruptThreshold && config.GiftBumpEnabled)
            {
                var bumps = Math.Max(1, coins / Math.Max(1, config.CoinsPerBump));
                if (!services.Queue.Bump(username)) return;
                for (int i = 1; i < bumps; i++)
                    if (!services.Queue.Bump(username)) break;
                result = CommandResult.Ok($"@{username} subió su canción {bumps} posición(es)");
            }
            else return;

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
            _activity.SetIgnored(config.Channel,      true);
            _activity.SetIgnored(config.BotUsername,  true);
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
            _activity.SetIgnored(config.Channel,     false);
            _activity.SetIgnored(config.BotUsername, false);
            _activeTwitchClient = null;
            _activeTwitchChannel = null;
            _chat.RegisterSender("twitch", null);
            tcs.TrySetResult();
            await Task.CompletedTask;
        };

        // Resolve broadcaster_id once at connect time for Helix follower lookups
        var broadcasterId = await _twitchFollowers.ResolveBroadcasterIdAsync(config.Channel);

        client.OnMessageReceived += async (_, e) =>
        {
            var msg = e.ChatMessage.Message?.Trim();
            if (string.IsNullOrEmpty(msg)) return;
            _activity.RecordMessage(e.ChatMessage.Username);
            if (msg[0] is ('!' or '.' or '/'))
            {
                // Read the latest config so settings changes apply mid-stream
                if (!_twitchConfigs.TryGetValue(userId, out var liveCfg)) return;
                if (await IsTwitchRoleAllowedAsync(e.ChatMessage, liveCfg.CommandRoles, broadcasterId, liveCfg.AllowedUsers))
                    await RouteCommand(userId, e.ChatMessage.Username, msg, "twitch");
                else
                    await NotifyPermissionDeniedAsync("twitch", e.ChatMessage.Username);
            }
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
            if (string.IsNullOrEmpty(content)) return;
            var sender = msg.Sender?.Username ?? "viewer";
            _activity.RecordMessage(sender);
            if (content[0] is ('!' or '.' or '/'))
            {
                // Read the latest config so settings changes apply mid-stream
                if (!_kickConfigs.TryGetValue(userId, out var liveCfg)) return;
                if (IsKickRoleAllowed(msg, liveCfg.CommandRoles, liveCfg.AllowedUsers))
                    _ = RouteCommand(userId, sender, content, "kick");
                else
                    _ = NotifyPermissionDeniedAsync("kick", sender);
            }
        };

        _logger.LogInformation("Kick connecting to channel {Channel}", config.Channel);
        await client.ConnectToChatroomAsync(config.Channel);
        _activity.SetIgnored(config.Channel, true);
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
            _activity.SetIgnored(config.Channel, false);
            _chat.RegisterSender("kick", null);
            await client.DisconnectAsync();
        }
    }

    // ── Shared command routing ────────────────────────────────────────────────

    // ── Role helpers ─────────────────────────────────────────────────────────────

    private static bool IsInAllowList(string? username, string[]? allowedUsers)
        => !string.IsNullOrEmpty(username)
           && allowedUsers != null
           && allowedUsers.Any(u => string.Equals(u.TrimStart('@'), username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks if a TikTok user is following the host. TikTok's SDK doesn't always
    /// populate <c>IsFollower</c> on every chat message, so we also check
    /// <c>FollowStatus</c> (≥1 = following, 2 = mutual) and <c>Follow_Info.FollowStatus</c>.
    /// </summary>
    private static bool IsTikTokFollowing(TikTokLiveSharp.Events.Objects.User sender)
    {
        if (sender.IsFollower) return true;
        if (sender.FollowStatus >= 1) return true;
        if (sender.Follow_Info?.FollowStatus >= 1) return true;
        return false;
    }

    /// <summary>
    /// Gets the user's fan-club (Team Member) level. TikTok has two parallel fields:
    /// <c>Fans_Club.Data.Level</c> (populated only when status=Active) and
    /// <c>FansClub_Info.FansLevel</c> (more reliable across event types). Returns max.
    /// </summary>
    private static int GetTikTokTeamLevel(TikTokLiveSharp.Events.Objects.User sender)
    {
        var dataLvl = sender.Fans_Club?.Data?.Level ?? 0;
        var infoLvl = (int)(sender.FansClub_Info?.FansLevel ?? 0);
        return Math.Max(dataLvl, infoLvl);
    }

    /// <summary>Checks moderator status across both the IsAdmin flag and the UserRole field.</summary>
    private static bool IsTikTokModerator(TikTokLiveSharp.Events.Objects.User sender)
    {
        if (sender.User_Attr?.IsAdmin == true) return true;
        if (sender.User_Attr?.IsSuperAdmin == true) return true;
        // UserRole == 3 = moderator in TikTok protocol (1=anchor, 2=fan, 3=mod, ...)
        if (sender.UserRole == 3) return true;
        return false;
    }

    private bool IsTikTokRoleAllowed(TikTokLiveSharp.Events.Objects.User sender, string[]? roles, int teamMinLevel, string[]? allowedUsers)
    {
        if (roles == null || roles.Length == 0 || roles.Contains("all")) return true;

        var listMatch = roles.Contains("list") && IsInAllowList(sender.UniqueId, allowedUsers);
        if (listMatch) return true;

        if (roles.Contains("moderator") && IsTikTokModerator(sender)) return true;
        if (roles.Contains("subscriber") && sender.Subscribe_Info?.IsSubscribe == true) return true;
        if (roles.Contains("follower") && IsTikTokFollowing(sender)) return true;
        if (roles.Contains("teamMember"))
        {
            var lvl = GetTikTokTeamLevel(sender);
            if (lvl >= Math.Max(1, teamMinLevel)) return true;
        }

        // Diagnostic logging — shows every signal the SDK provided for this user
        _logger.LogInformation(
            "TikTok role DENIED for @{User} | roles=[{Roles}] minTeam={MinTeam} | " +
            "IsFollower={IsF} FollowStatus={FS} FollowInfoFS={FIFS} | " +
            "Sub={Sub} | IsAdmin={Adm} IsSuperAdm={Sup} UserRole={UR} | " +
            "FanData.Lvl={FDL} FanData.Status={FDS} FanInfo.Lvl={FIL} | " +
            "ListMatch={LM}",
            sender.UniqueId, string.Join(",", roles ?? []), teamMinLevel,
            sender.IsFollower, sender.FollowStatus, sender.Follow_Info?.FollowStatus,
            sender.Subscribe_Info?.IsSubscribe == true,
            sender.User_Attr?.IsAdmin == true, sender.User_Attr?.IsSuperAdmin == true, sender.UserRole,
            sender.Fans_Club?.Data?.Level ?? 0,
            sender.Fans_Club?.Data?.FansClubStatus,
            sender.FansClub_Info?.FansLevel ?? 0,
            listMatch);
        return false;
    }

    private async Task<bool> IsTwitchRoleAllowedAsync(TwitchLib.Client.Models.ChatMessage msg, string[]? roles, string? broadcasterId, string[]? allowedUsers)
    {
        if (roles == null || roles.Length == 0 || roles.Contains("all")) return true;

        var listMatch = roles.Contains("list") && IsInAllowList(msg.Username, allowedUsers);
        if (listMatch) return true;

        if (roles.Contains("moderator") && (msg.IsBroadcaster || msg.UserDetail.IsModerator)) return true;
        if (roles.Contains("subscriber") && msg.UserDetail.IsSubscriber) return true;
        if (roles.Contains("vip") && msg.UserDetail.IsVip) return true;

        var followerChecked = false;
        var followerMatch   = false;
        if (roles.Contains("follower") && !string.IsNullOrEmpty(broadcasterId) && !string.IsNullOrEmpty(msg.UserId))
        {
            followerChecked = true;
            followerMatch = await _twitchFollowers.IsFollowerAsync(broadcasterId, msg.UserId);
            if (followerMatch) return true;
        }

        _logger.LogInformation(
            "Twitch role DENIED for @{User} | roles=[{Roles}] | IsBroadcaster={B} IsMod={M} IsSub={S} IsVip={V} | " +
            "FollowerChecked={FC} FollowerMatch={FM} | ListMatch={LM} | UserId={Uid} BroadcasterId={Bid}",
            msg.Username, string.Join(",", roles ?? []),
            msg.IsBroadcaster, msg.UserDetail.IsModerator, msg.UserDetail.IsSubscriber, msg.UserDetail.IsVip,
            followerChecked, followerMatch, listMatch,
            msg.UserId ?? "(empty)", broadcasterId ?? "(unresolved)");

        return false;
    }

    private bool IsKickRoleAllowed(KickChatSpy.Models.ChatMessage msg, string[]? roles, string[]? allowedUsers)
    {
        if (roles == null || roles.Length == 0 || roles.Contains("all")) return true;

        var listMatch = roles.Contains("list") && IsInAllowList(msg.Sender?.Username, allowedUsers);
        if (listMatch) return true;

        var badges = msg.Sender?.Identity?.Badges;
        if (badges != null)
        {
            if (roles.Contains("moderator") && badges.Any(b => b.Type is "moderator" or "broadcaster")) return true;
            if (roles.Contains("subscriber") && badges.Any(b => b.Type is "subscriber" or "founder")) return true;
            if (roles.Contains("vip")        && badges.Any(b => b.Type == "vip")) return true;
            if (roles.Contains("og")         && badges.Any(b => b.Type == "og")) return true;
        }

        var badgeList = badges == null ? "(null)" : string.Join(",", badges.Select(b => b.Type));
        _logger.LogInformation(
            "Kick role DENIED for @{User} | roles=[{Roles}] | badges=[{Badges}] | ListMatch={LM}",
            msg.Sender?.Username, string.Join(",", roles ?? []), badgeList, listMatch);

        // Note: Kick does not expose follower status without webhook infrastructure
        // (channel.followed event + public endpoint). Follower role intentionally omitted.
        return false;
    }

    private async Task<CommandResult?> RouteCommand(Guid userId, string username, string message, string platform)
    {
        var parts = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd   = parts[0][1..].ToLowerInvariant(); // strip prefix char (! . /)
        var args  = parts.Length > 1 ? parts[1].Trim() : "";

        BotCommand? command = cmd switch
        {
            "play" or "sr" when !string.IsNullOrEmpty(args) =>
                new BotCommand { Type = "play",     Query = args, RequestedBy = username, Platform = platform },
            "skip" =>
                new BotCommand { Type = "selfskip", RequestedBy = username, Platform = platform },
            "si" or "yes" =>
                new BotCommand { Type = "si",       RequestedBy = username, Platform = platform },
            "no" =>
                new BotCommand { Type = "no",       RequestedBy = username, Platform = platform },
            "revoke" or "quitar" =>
                new BotCommand { Type = "revoke",   RequestedBy = username, Platform = platform },
            "bump" =>
                new BotCommand { Type = "bump",     RequestedBy = username, Platform = platform },
            "song" or "cancion" or "current" =>
                new BotCommand { Type = "song",     RequestedBy = username, Platform = platform },
            "like" or "love" =>
                new BotCommand { Type = "like",     RequestedBy = username, Platform = platform },
            "queue" or "cola" =>
                new BotCommand { Type = "queue",    RequestedBy = username, Platform = platform },
            "pos" or "position" =>
                new BotCommand { Type = "pos",      RequestedBy = username, Platform = platform },
            "history" or "historial" =>
                new BotCommand { Type = "history",  RequestedBy = username, Platform = platform },
            "info" =>
                new BotCommand { Type = "info",     RequestedBy = username, Platform = platform },
            "aqui" or "here" =>
                new BotCommand { Type = "aqui",     RequestedBy = username, Platform = platform },
            "keep" =>
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
                    // Refresh cookies to capture any rotated tokens (e.g. msToken) that
                    // TikTok's JS SDK updated during the fetch() call.
                    if (AppEvents.HasTikTokCookieRefresher)
                    {
                        var fresh = await AppEvents.RefreshTikTokCookiesViaWebView();
                        if (!string.IsNullOrWhiteSpace(fresh))
                            _tikTokAuth.UpdateCookiesFromWebView(fresh);
                    }
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

            // Warn if region-specific session cookies are absent — may fail in some markets
            if (ExtractCookieValue(cookieString, "sessionid_ss") == null)
                _logger.LogDebug("TikTok send: sessionid_ss absent in cookie string — may fail in some regions");
            if (ExtractCookieValue(cookieString, "sid_guard") == null)
                _logger.LogDebug("TikTok send: sid_guard absent in cookie string — may fail in some regions");

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
            // 10007 = sesión expirada (alias) → mismo tratamiento que 20003
            // 20001 = X-Bogus signing req.    → la cuenta/región requiere signing server
            // 4003001 = no autenticado        → sessionid incorrecto o expirado
            // 3 = rate limited                → enviando demasiado rápido
            // 403 empty = WAF block           → headers de browser incorrectos o IP bloqueada
            _logger.LogWarning("TikTok chat send failed — status={Status} body={Body}",
                (int)response.StatusCode, body.Length > 500 ? body[..500] : body);

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("status_code", out var sc))
                {
                    var code = sc.GetInt32();
                    if (code is 20003 or 10007 or 4003001)
                    {
                        _logger.LogWarning("TikTok auth expired (status_code={Code}) — notifying user", code);
                        await _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("auth:expired", new
                        {
                            platform = "tiktok",
                            message  = "Las cookies de TikTok expiraron. Ve a Plataformas y vuelve a iniciar sesión en TikTok."
                        });
                    }
                }
            }
            catch { /* body may not be JSON — ignore */ }

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
