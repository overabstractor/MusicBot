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

    public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

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
        var hasSign = !string.IsNullOrWhiteSpace(config.SigningServerUrl);

        // ── Tunable timings (kept low for fast recovery from transient drops) ─
        const int LiveCheckSec       = 20;   // poll for "is live" while waiting for stream
        const int WatchdogSec        = 45;   // how often watchdog checks live status
        const int MaxConsecFails     = 2;    // tolerate this many failed checks before disconnect
        const int MaxSilenceMinutes  = 3;    // force reconnect if WS shows connected but no events
        const int EndedCooldownSec   = 5;    // cooldown after natural stream end
        int[] wsRetryDelays          = [1, 2, 5, 10, 20]; // fast local backoff for WS-level errors
        int wsFailCount = 0;

        while (!ct.IsCancellationRequested)
        {
            // ── Wait for the streamer to go live ──────────────────────────────
            string? roomId = null;
            while (!ct.IsCancellationRequested)
            {
                roomId = await SafeResolveRoomIdAsync(config.Username, ct);
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
                skipRoomInfo: true,
                processInitialData: false,
                customSigningServer: hasSign ? config.SigningServerUrl : null,
                signingServerApiKey: hasSign ? config.SigningServerApiKey : null);

            var canSendChat = !string.IsNullOrWhiteSpace(config.CookieString)
                              || AppEvents.HasTikTokWebViewSender;

            string? tiktokRoomId = null;
            var lastEventAt = DateTime.UtcNow;
            var connectedSuccessfully = false;

            client.OnConnected += (_, _) =>
            {
                connectedSuccessfully = true;
                wsFailCount = 0; // reset fast-retry backoff once we're actually connected
                lastEventAt = DateTime.UtcNow;
                _logger.LogInformation("TikTok connected to @{User}", config.Username);
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

            _logger.LogInformation("TikTok connecting to @{User} (signing: {Signing}, chat-send: {CanSend})",
                config.Username, hasSign ? config.SigningServerUrl : "none", canSendChat);

            // Watchdog: tolerates transient failures (2 consecutive needed) + detects silent WS.
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var streamEndedByWatchdog = false;
            var runTask = client.RunAsync(streamCts.Token);
            var watchdogTask = Task.Run(async () =>
            {
                int consecutiveFails = 0;
                try
                {
                    while (!streamCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(WatchdogSec), streamCts.Token);

                        // Heartbeat: if WS claims connected but no events flowing, force reconnect
                        if (connectedSuccessfully &&
                            (DateTime.UtcNow - lastEventAt).TotalMinutes > MaxSilenceMinutes)
                        {
                            _logger.LogWarning(
                                "TikTok @{User} sin eventos por >{Min}min — forzando reconexión",
                                config.Username, MaxSilenceMinutes);
                            streamEndedByWatchdog = true;
                            streamCts.Cancel();
                            return;
                        }

                        var stillLive = await SafeResolveRoomIdAsync(config.Username, streamCts.Token);
                        if (stillLive == null)
                        {
                            consecutiveFails++;
                            _logger.LogDebug(
                                "TikTok watchdog: @{User} chequeo fallido ({N}/{Max})",
                                config.Username, consecutiveFails, MaxConsecFails);
                            if (consecutiveFails >= MaxConsecFails)
                            {
                                _logger.LogInformation(
                                    "TikTok watchdog: @{User} ya no está en vivo — cerrando conexión",
                                    config.Username);
                                streamEndedByWatchdog = true;
                                streamCts.Cancel();
                                return;
                            }
                        }
                        else
                        {
                            consecutiveFails = 0;
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
