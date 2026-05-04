using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MusicBot.Core.Models;
using MusicBot.Hubs;
using TikTokLiveSharp.Client;
using TikTokLiveSharp.Events;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Listens to a TikTok LIVE chat and routes commands to MusicBot.
/// Handles both chat commands (!play, !sr, !skip, etc.) and gift events
/// (coins → bump/interrupt). Broadcasts events to the frontend via SignalR.
/// </summary>
public class TikTokService : BackgroundService
{
    private readonly TikTokSettings _settings;
    private readonly TikTokRoomResolver _roomResolver;
    private readonly CommandRouterService _router;
    private readonly UserContextManager _userContext;
    private readonly PlaybackSyncService _sync;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly IntegrationStatusTracker _tracker;
    private readonly ChatResponseService _chat;
    private readonly ILogger<TikTokService> _logger;

    public TikTokService(
        IOptions<TikTokSettings> settings,
        TikTokRoomResolver roomResolver,
        CommandRouterService router,
        UserContextManager userContext,
        PlaybackSyncService sync,
        IHubContext<OverlayHub> hub,
        IntegrationStatusTracker tracker,
        ChatResponseService chat,
        ILogger<TikTokService> logger)
    {
        _settings     = settings.Value;
        _roomResolver = roomResolver;
        _router       = router;
        _userContext   = userContext;
        _sync         = sync;
        _hub          = hub;
        _tracker      = tracker;
        _chat         = chat;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("TikTok integration disabled — set TikTok:Username and TikTok:UserSlug in appsettings.json");
            return;
        }

        _logger.LogInformation("TikTok service starting for @{Username}", _settings.Username);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("TikTok error: {Message}. Retrying in 30s…", ex.Message);
                await BroadcastStatusAsync("disconnected");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        await BroadcastStatusAsync("disconnected");
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        await BroadcastStatusAsync("connecting");

        var hasSigningServer = !string.IsNullOrWhiteSpace(_settings.SigningServerUrl);
        TikTokLiveClient client;

        try
        {
            // Try normal connection first (works if IP is not blocked)
            client = new TikTokLiveClient(
                _settings.Username,
                processInitialData: false,
                customSigningServer: hasSigningServer ? _settings.SigningServerUrl : null,
                signingServerApiKey: hasSigningServer ? _settings.SigningServerApiKey : null);
        }
        catch (Exception ex) when (ex.Message.Contains("RoomId", StringComparison.OrdinalIgnoreCase) ||
                                    ex.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback: resolve room ID ourselves
            _logger.LogWarning("TikTok blocked direct connection, resolving room ID via fallback…");
            var roomId = await _roomResolver.ResolveRoomIdAsync(_settings.Username, ct);
            if (roomId == null)
                throw new Exception($"Could not resolve TikTok room ID for @{_settings.Username} — user may not be live");

            client = new TikTokLiveClient(
                _settings.Username,
                roomId: roomId,
                skipRoomInfo: true,
                processInitialData: false,
                customSigningServer: hasSigningServer ? _settings.SigningServerUrl : null,
                signingServerApiKey: hasSigningServer ? _settings.SigningServerApiKey : null);
        }

        client.OnConnected    += (_, _) =>
        {
            _logger.LogInformation("TikTok connected to @{User}", _settings.Username);
            _ = BroadcastStatusAsync("connected");
        };
        client.OnDisconnected += (_, _) =>
        {
            _logger.LogWarning("TikTok disconnected from @{User}", _settings.Username);
            _ = BroadcastStatusAsync("disconnected");
        };
        client.OnChatMessage  += OnChat;
        client.OnGiftMessage  += OnGift;

        await client.RunAsync(ct);
    }

    // ── Chat commands ──────────────────────────────────────────────────────────

    private async void OnChat(TikTokLiveClient sender, Chat e)
    {
        var username = e.Sender?.UniqueId
            ?? e.Sender?.NickName;

        var message = e.Message?.Trim();
        if (string.IsNullOrEmpty(message) || message[0] is not ('!' or '.' or '/')) return;

        if (string.IsNullOrEmpty(username))
        {
            _logger.LogWarning("TikTok: comando ignorado de usuario anónimo (sin UniqueId ni NickName) — mensaje: {Message}", message);
            return;
        }

        var parts = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd   = parts[0][1..].ToLowerInvariant(); // strip prefix char
        var args  = parts.Length > 1 ? parts[1].Trim() : "";

        BotCommand? command = cmd switch
        {
            "play" or "sr" when !string.IsNullOrEmpty(args) =>
                new BotCommand { Type = "play",     Query = args, RequestedBy = username, Platform = "tiktok" },
            "skip" =>
                new BotCommand { Type = "selfskip", RequestedBy = username, Platform = "tiktok" },
            "si" or "yes" =>
                new BotCommand { Type = "si",       RequestedBy = username, Platform = "tiktok" },
            "no" =>
                new BotCommand { Type = "no",       RequestedBy = username, Platform = "tiktok" },
            "revoke" or "quitar" =>
                new BotCommand { Type = "revoke",   RequestedBy = username, Platform = "tiktok" },
            "bump" =>
                new BotCommand { Type = "bump",     RequestedBy = username, Platform = "tiktok" },
            "song" or "cancion" or "current" =>
                new BotCommand { Type = "song",     RequestedBy = username, Platform = "tiktok" },
            "like" or "love" =>
                new BotCommand { Type = "like",     RequestedBy = username, Platform = "tiktok" },
            "queue" or "cola" =>
                new BotCommand { Type = "queue",    RequestedBy = username, Platform = "tiktok" },
            "pos" or "position" =>
                new BotCommand { Type = "pos",      RequestedBy = username, Platform = "tiktok" },
            "history" or "historial" =>
                new BotCommand { Type = "history",  RequestedBy = username, Platform = "tiktok" },
            "info" =>
                new BotCommand { Type = "info",     RequestedBy = username, Platform = "tiktok" },
            "aqui" or "here" =>
                new BotCommand { Type = "aqui",     RequestedBy = username, Platform = "tiktok" },
            "keep" =>
                new BotCommand { Type = "keep",     RequestedBy = username, Platform = "tiktok" },
            _ => null
        };

        if (command == null) return;

        var result = await DispatchAsync(command);
        if (result != null)
        {
            await BroadcastEventAsync(new
            {
                source  = "tiktok",
                type    = "play",
                user    = username,
                query   = command.Query ?? "",
                success = result.Success,
                message = result.Message,
            });

            await _chat.SendChatMessageAsync(username, result.Message, "tiktok");
        }
    }

    // ── Gift handling ──────────────────────────────────────────────────────────

    private async void OnGift(TikTokLiveClient sender, GiftMessage e)
    {
        try
        {
            var username  = e.User?.UniqueId ?? e.User?.NickName;
            var giftName  = e.Gift?.Name ?? "regalo";
            var diamonds  = e.Gift?.DiamondCost ?? 0;
            var repeat    = (int)(e.RepeatCount > 0 ? e.RepeatCount : 1);
            var coins     = diamonds * repeat;

            if (string.IsNullOrEmpty(username))
            {
                _logger.LogWarning("TikTok: regalo ignorado de usuario anónimo (sin UniqueId ni NickName) — regalo: {Gift}", e.Gift?.Name);
                return;
            }

            if (coins <= 0) return;

            var services = await _userContext.GetBySlugAsync(_settings.UserSlug);
            if (services == null) return;

            CommandResult result;

            if (coins >= 100)
            {
                var ok = services.Queue.InterruptForUser(username);
                if (!ok) return;

                await _sync.StartCurrentTrackAsync(services);
                result = CommandResult.Ok($"@{username} interrumpió con {coins} monedas!");
            }
            else
            {
                if (!services.Queue.Bump(username)) return;
                for (int i = 1; i < coins; i++)
                    if (!services.Queue.Bump(username)) break;
                result = CommandResult.Ok($"@{username} subió su canción {coins} posición(es)");
            }

            await BroadcastEventAsync(new
            {
                source  = "tiktok",
                type    = "gift",
                user    = username,
                query   = $"{giftName} ×{repeat} ({coins} diamonds)",
                success = result.Success,
                message = result.Message,
            });

            _logger.LogInformation("TikTok gift @{User}: {Gift} ×{Repeat} = {Coins} diamonds → {Result}",
                username, giftName, repeat, coins, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TikTok: error handling gift");
        }
    }

    // ── Dispatch & broadcast ───────────────────────────────────────────────────

    private async Task<CommandResult?> DispatchAsync(BotCommand command)
    {
        try
        {
            var services = await _userContext.GetBySlugAsync(_settings.UserSlug);
            if (services == null)
            {
                _logger.LogWarning("TikTok: no MusicBot user found for slug '{Slug}'", _settings.UserSlug);
                return null;
            }

            var result = await _router.HandleAsync(command, services);
            _logger.LogInformation("TikTok [{Cmd}] @{User}: {Result}",
                command.Type, command.RequestedBy, result.Message);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TikTok: error dispatching {Type}", command.Type);
            return null;
        }
    }

    private Task BroadcastStatusAsync(string status)
    {
        _tracker.TikTokStatus = status;
        return _hub.Clients.Group($"user:{LocalUser.Id}")
                           .SendAsync("integration:status", new { source = "tiktok", status });
    }

    private Task BroadcastEventAsync(object payload)
        => _hub.Clients.Group($"user:{LocalUser.Id}")
                       .SendAsync("integration:event", payload);
}
