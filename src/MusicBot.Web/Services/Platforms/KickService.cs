using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MusicBot.Core.Models;
using MusicBot.Hubs;
using KickChatSpy;
using KickChatSpy.Models;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Listens to a Kick channel's chat via Pusher WebSocket (no auth required)
/// and routes commands to MusicBot. Broadcasts events to the frontend via SignalR.
/// Note: Kick doesn't support sending messages without OAuth, so responses
/// are only logged (not sent back to chat).
/// </summary>
public class KickService : BackgroundService
{
    private readonly KickSettings _settings;
    private readonly CommandRouterService _router;
    private readonly UserContextManager _userContext;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly IntegrationStatusTracker _tracker;
    private readonly ILogger<KickService> _logger;

    public KickService(
        IOptions<KickSettings> settings,
        CommandRouterService router,
        UserContextManager userContext,
        IHubContext<OverlayHub> hub,
        IntegrationStatusTracker tracker,
        ILogger<KickService> logger)
    {
        _settings    = settings.Value;
        _router      = router;
        _userContext  = userContext;
        _hub         = hub;
        _tracker     = tracker;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Kick integration disabled — configure Kick section in appsettings.json");
            return;
        }

        _logger.LogInformation("Kick service starting for channel {Channel}", _settings.Channel);

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
                _logger.LogWarning("Kick error: {Message}. Reconnecting in 15s…", ex.Message);
                await BroadcastStatusAsync("disconnected");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }

        await BroadcastStatusAsync("disconnected");
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        var client = new KickChatClient();

        await BroadcastStatusAsync("connecting");

        client.OnMessageReceived += msg => OnMessage(msg);

        _logger.LogInformation("Kick connecting to channel {Channel}", _settings.Channel);
        await client.ConnectToChatroomAsync(_settings.Channel);

        _logger.LogInformation("Kick connected to channel {Channel}", _settings.Channel);
        await BroadcastStatusAsync("connected");

        // Keep alive until cancelled or disconnected
        try
        {
            while (!ct.IsCancellationRequested && client.IsConnected)
                await Task.Delay(1000, ct);
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    private async void OnMessage(ChatMessage msg)
    {
        try
        {
            var content = msg.Content?.Trim();
            if (string.IsNullOrEmpty(content) || content[0] is not ('!' or '.' or '/')) return;

            var username = msg.Sender?.Username ?? "viewer";
            var parts    = content.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd      = parts[0][1..].ToLowerInvariant(); // strip prefix char
            var args     = parts.Length > 1 ? parts[1].Trim() : "";

            BotCommand? command = cmd switch
            {
                "play" or "sr" when !string.IsNullOrEmpty(args) =>
                    new BotCommand { Type = "play",     Query = args, RequestedBy = username, Platform = "kick" },
                "skip" =>
                    new BotCommand { Type = "selfskip", RequestedBy = username, Platform = "kick" },
                "si" or "yes" =>
                    new BotCommand { Type = "si",       RequestedBy = username, Platform = "kick" },
                "no" =>
                    new BotCommand { Type = "no",       RequestedBy = username, Platform = "kick" },
                "revoke" or "quitar" =>
                    new BotCommand { Type = "revoke",   RequestedBy = username, Platform = "kick" },
                "bump" =>
                    new BotCommand { Type = "bump",     RequestedBy = username, Platform = "kick" },
                "song" or "cancion" or "current" =>
                    new BotCommand { Type = "song",     RequestedBy = username, Platform = "kick" },
                "like" or "love" =>
                    new BotCommand { Type = "like",     RequestedBy = username, Platform = "kick" },
                "queue" or "cola" =>
                    new BotCommand { Type = "queue",    RequestedBy = username, Platform = "kick" },
                "pos" or "position" =>
                    new BotCommand { Type = "pos",      RequestedBy = username, Platform = "kick" },
                "history" or "historial" =>
                    new BotCommand { Type = "history",  RequestedBy = username, Platform = "kick" },
                "info" =>
                    new BotCommand { Type = "info",     RequestedBy = username, Platform = "kick" },
                "aqui" or "here" =>
                    new BotCommand { Type = "aqui",     RequestedBy = username, Platform = "kick" },
                "keep" =>
                    new BotCommand { Type = "keep",     RequestedBy = username, Platform = "kick" },
                _ => null
            };

            if (command == null) return;

            var services = await _userContext.GetBySlugAsync(_settings.UserSlug);
            if (services == null)
            {
                _logger.LogWarning("Kick: no MusicBot user found for slug '{Slug}'", _settings.UserSlug);
                return;
            }

            var result = await _router.HandleAsync(command, services);
            _logger.LogInformation("Kick [{Cmd}] @{User}: {Result}", cmd, username, result.Message);

            await BroadcastEventAsync(new
            {
                source   = "kick",
                type     = "play",
                platform = "Kick",
                user     = username,
                query    = command.Query ?? "",
                success  = result.Success,
                message  = result.Message,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kick: error processing message");
        }
    }

    private Task BroadcastStatusAsync(string status)
    {
        _tracker.KickStatus = status;
        return _hub.Clients.Group($"user:{LocalUser.Id}")
                           .SendAsync("integration:status", new { source = "kick", status });
    }

    private Task BroadcastEventAsync(object payload)
        => _hub.Clients.Group($"user:{LocalUser.Id}")
                       .SendAsync("integration:event", payload);
}
