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

        while (!ct.IsCancellationRequested)
        {
            // ── Wait for the streamer to go live ──────────────────────────────
            // Polls every 60 s to avoid hammering the signing server.
            // Stays in "Connecting" status so the UI never shows the Connect button.
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
                // Keep "Connecting" if stream ended naturally (will poll for next live).
                // Only switch to "Disconnected" when the user explicitly cancelled.
                SetStatus(key, ct.IsCancellationRequested
                    ? ConnectionStatus.Disconnected
                    : ConnectionStatus.Connecting);
            };
            client.OnChatMessage += (_, e) =>
            {
                // Capture roomId from first incoming message for sending
                if (tiktokRoomId == null && e.RoomId > 0)
                    tiktokRoomId = e.RoomId.ToString();
                HandleTikTokChat(userId, config, e);
            };
            client.OnGiftMessage  += (_, e) => HandleTikTokGift(userId, config, e);

            _logger.LogInformation("TikTok connecting to @{User} (signing: {Signing}, chat-send: {CanSend})",
                config.Username, hasSign ? config.SigningServerUrl : "none", canSendChat);

            // Run the WebSocket client and a live-status watchdog in parallel.
            // TikTok doesn't always send a disconnect event when a stream ends, so the
            // watchdog polls every 90 s and cancels the client when the room is no longer live.
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var runTask = client.RunAsync(streamCts.Token);
            var watchdogTask = Task.Run(async () =>
            {
                const int WatchdogSec = 90;
                try
                {
                    while (!streamCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(WatchdogSec), streamCts.Token);
                        var stillLive = await _roomResolver.ResolveRoomIdAsync(config.Username, streamCts.Token);
                        if (stillLive == null)
                        {
                            _logger.LogInformation(
                                "TikTok watchdog: @{User} ya no está en vivo — cerrando conexión WebSocket",
                                config.Username);
                            streamCts.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, streamCts.Token);

            try { await runTask; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Cancelled by the watchdog (stream ended), not by the user — treat as natural end
            }
            await watchdogTask; // let it finish cleanly

            // Stream ended naturally — brief cooldown before re-checking live status
            // to avoid reconnecting to a stale room ID that TikTok hasn't invalidated yet.
            if (!ct.IsCancellationRequested)
            {
                const int EndedCooldownSec = 30;
                _logger.LogInformation("TikTok @{User} stream ended — checking live again in {Sec}s",
                    config.Username, EndedCooldownSec);
                SetStatus(key, ConnectionStatus.Connecting,
                    $"@{config.Username} terminó el vivo — verificando en {EndedCooldownSec}s…");
                try { await Task.Delay(TimeSpan.FromSeconds(EndedCooldownSec), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async void HandleTikTokChat(Guid userId, TikTokPlatformConfig config, Chat e)
    {
        try
        {
            var username = e.Sender?.UniqueId ?? "viewer";
            var message  = e.Message?.Trim();
            if (string.IsNullOrEmpty(message)) return;

            _activity.RecordMessage(username);
            _logger.LogDebug("TikTok chat @{User}: {Message}", username, message);

            if (message[0] is not ('!' or '.' or '/')) return;
            if (e.Sender != null && !IsTikTokRoleAllowed(e.Sender, config.CommandRoles, config.TeamMinLevel, config.AllowedUsers)) return;
            await RouteCommand(userId, username, message, "tiktok");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TikTok chat handler error");
        }
    }

    private async void HandleTikTokGift(Guid userId, TikTokPlatformConfig config, GiftMessage e)
    {
        try
        {
            // Ignore intermediate combo events; only process when the streak ends
            if (!e.StreakEnd) return;

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
                if (await IsTwitchRoleAllowedAsync(e.ChatMessage, config.CommandRoles, broadcasterId, config.AllowedUsers))
                    await RouteCommand(userId, e.ChatMessage.Username, msg, "twitch");
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
            if (content[0] is ('!' or '.' or '/') && IsKickRoleAllowed(msg, config.CommandRoles, config.AllowedUsers))
                _ = RouteCommand(userId, sender, content, "kick");
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

    private static bool IsTikTokRoleAllowed(TikTokLiveSharp.Events.Objects.User sender, string[]? roles, int teamMinLevel, string[]? allowedUsers)
    {
        if (roles == null || roles.Length == 0 || roles.Contains("all")) return true;
        if (roles.Contains("list") && IsInAllowList(sender.UniqueId, allowedUsers)) return true;
        if (roles.Contains("moderator") && (sender.User_Attr?.IsAdmin == true || sender.User_Attr?.IsSuperAdmin == true)) return true;
        if (roles.Contains("subscriber") && sender.Subscribe_Info?.IsSubscribe == true) return true;
        if (roles.Contains("follower") && sender.IsFollower) return true;
        if (roles.Contains("teamMember"))
        {
            var lvl = sender.Fans_Club?.Data?.Level ?? 0;
            if (lvl >= Math.Max(1, teamMinLevel)) return true;
        }
        return false;
    }

    private async Task<bool> IsTwitchRoleAllowedAsync(TwitchLib.Client.Models.ChatMessage msg, string[]? roles, string? broadcasterId, string[]? allowedUsers)
    {
        if (roles == null || roles.Length == 0 || roles.Contains("all")) return true;
        if (roles.Contains("list") && IsInAllowList(msg.Username, allowedUsers)) return true;
        if (roles.Contains("moderator") && (msg.IsBroadcaster || msg.UserDetail.IsModerator)) return true;
        if (roles.Contains("subscriber") && msg.UserDetail.IsSubscriber) return true;
        if (roles.Contains("vip") && msg.UserDetail.IsVip) return true;
        if (roles.Contains("follower") && !string.IsNullOrEmpty(broadcasterId) && !string.IsNullOrEmpty(msg.UserId))
        {
            if (await _twitchFollowers.IsFollowerAsync(broadcasterId, msg.UserId)) return true;
        }
        return false;
    }

    private static bool IsKickRoleAllowed(KickChatSpy.Models.ChatMessage msg, string[]? roles, string[]? allowedUsers)
    {
        if (roles == null || roles.Length == 0 || roles.Contains("all")) return true;
        if (roles.Contains("list") && IsInAllowList(msg.Sender?.Username, allowedUsers)) return true;
        var badges = msg.Sender?.Identity?.Badges;
        if (badges != null)
        {
            if (roles.Contains("moderator") && badges.Any(b => b.Type is "moderator" or "broadcaster")) return true;
            if (roles.Contains("subscriber") && badges.Any(b => b.Type is "subscriber" or "founder")) return true;
            if (roles.Contains("vip")        && badges.Any(b => b.Type == "vip")) return true;
            if (roles.Contains("og")         && badges.Any(b => b.Type == "og")) return true;
        }
        // Kick API doesn't expose follower status reliably — see thread; best effort treats any chatter
        // as a follower since the API has no per-user follower query without webhook infrastructure.
        if (roles.Contains("follower")) return true;
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
