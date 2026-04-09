using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using MusicBot.Core.Models;
using MusicBot.Hubs;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Listens to a Twitch channel's chat and routes commands to MusicBot.
/// Gets OAuth token from TwitchAuthService (no manual token needed).
/// Supports sending chat responses back via TwitchLib.
/// </summary>
public class TwitchService : BackgroundService
{
    private readonly TwitchSettings _settings;
    private readonly TwitchAuthService _auth;
    private readonly CommandRouterService _router;
    private readonly UserContextManager _userContext;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly IntegrationStatusTracker _tracker;
    private readonly ChatResponseService _chat;
    private readonly ILogger<TwitchService> _logger;

    public TwitchService(
        IOptions<TwitchSettings> settings,
        TwitchAuthService auth,
        CommandRouterService router,
        UserContextManager userContext,
        IHubContext<OverlayHub> hub,
        IntegrationStatusTracker tracker,
        ChatResponseService chat,
        ILogger<TwitchService> logger)
    {
        _settings    = settings.Value;
        _auth        = auth;
        _router      = router;
        _userContext  = userContext;
        _hub         = hub;
        _tracker     = tracker;
        _chat        = chat;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Twitch integration disabled — configure Twitch:Channel and Twitch:UserSlug in appsettings.json");
            return;
        }

        if (!_auth.IsAuthenticated)
        {
            _logger.LogInformation("Twitch: no OAuth token — connect via the dashboard first");
            return;
        }

        _logger.LogInformation("Twitch service starting for channel #{Channel}", _settings.Channel);

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
                _logger.LogWarning("Twitch error: {Message}. Reconnecting in 15s…", ex.Message);
                await BroadcastStatusAsync("disconnected");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }

        await BroadcastStatusAsync("disconnected");
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync();
        var botUser = _auth.BotUsername ?? "musicbot";
        var credentials = new ConnectionCredentials(botUser, $"oauth:{token}");
        var client      = new TwitchClient();
        client.Initialize(credentials, _settings.Channel);

        var tcs = new TaskCompletionSource();
        ct.Register(() => tcs.TrySetCanceled());

        await BroadcastStatusAsync("connecting");

        client.OnConnected += async (_, _) =>
        {
            _logger.LogInformation("Twitch connected to #{Channel}", _settings.Channel);
            await BroadcastStatusAsync("connected");
        };

        client.OnDisconnected += async (_, _) =>
        {
            _logger.LogWarning("Twitch disconnected from #{Channel}", _settings.Channel);
            tcs.TrySetResult();
            await Task.CompletedTask;
        };

        client.OnMessageReceived += async (_, e) =>
        {
            await OnMessage(e, client);
        };

        await client.ConnectAsync();

        try { await tcs.Task; }
        catch (TaskCanceledException) { }

        await client.DisconnectAsync();
    }

    private async Task OnMessage(OnMessageReceivedArgs e, TwitchClient client)
    {
        var message = e.ChatMessage.Message?.Trim();
        if (string.IsNullOrEmpty(message) || message[0] is not ('!' or '.' or '/')) return;

        var username = e.ChatMessage.Username;
        var parts    = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd      = parts[0][1..].ToLowerInvariant(); // strip prefix char
        var args     = parts.Length > 1 ? parts[1].Trim() : "";

        BotCommand? command = cmd switch
        {
            "play" or "sr" when !string.IsNullOrEmpty(args) =>
                new BotCommand { Type = "play",     Query = args, RequestedBy = username, Platform = "twitch" },
            "skip" =>
                new BotCommand { Type = "selfskip", RequestedBy = username, Platform = "twitch" },
            "si" or "yes" =>
                new BotCommand { Type = "si",       RequestedBy = username, Platform = "twitch" },
            "no" =>
                new BotCommand { Type = "no",       RequestedBy = username, Platform = "twitch" },
            "revoke" or "quitar" =>
                new BotCommand { Type = "revoke",   RequestedBy = username, Platform = "twitch" },
            "bump" =>
                new BotCommand { Type = "bump",     RequestedBy = username, Platform = "twitch" },
            "song" or "cancion" or "current" =>
                new BotCommand { Type = "song",     RequestedBy = username, Platform = "twitch" },
            "like" or "love" =>
                new BotCommand { Type = "like",     RequestedBy = username, Platform = "twitch" },
            "queue" or "cola" =>
                new BotCommand { Type = "queue",    RequestedBy = username, Platform = "twitch" },
            "pos" or "position" =>
                new BotCommand { Type = "pos",      RequestedBy = username, Platform = "twitch" },
            "history" or "historial" =>
                new BotCommand { Type = "history",  RequestedBy = username, Platform = "twitch" },
            "info" =>
                new BotCommand { Type = "info",     RequestedBy = username, Platform = "twitch" },
            "aqui" or "here" =>
                new BotCommand { Type = "aqui",     RequestedBy = username, Platform = "twitch" },
            "keep" =>
                new BotCommand { Type = "keep",     RequestedBy = username, Platform = "twitch" },
            _ => null
        };

        if (command == null) return;

        var services = await _userContext.GetBySlugAsync(_settings.UserSlug);
        if (services == null)
        {
            _logger.LogWarning("Twitch: no MusicBot user found for slug '{Slug}'", _settings.UserSlug);
            return;
        }

        var result = await _router.HandleAsync(command, services);
        _logger.LogInformation("Twitch [{Cmd}] @{User}: {Result}", cmd, username, result.Message);

        await BroadcastEventAsync(new
        {
            source   = "twitch",
            type     = "play",
            platform = "Twitch",
            user     = username,
            query    = command.Query ?? "",
            success  = result.Success,
            message  = result.Message,
        });

        // Send response to Twitch chat
        try
        {
            if (client.IsConnected)
                await client.SendMessageAsync(_settings.Channel, $"@{username}: {result.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twitch: failed to send chat response");
        }
    }

    private Task BroadcastStatusAsync(string status)
    {
        _tracker.TwitchStatus = status;
        return _hub.Clients.Group($"user:{LocalUser.Id}")
                           .SendAsync("integration:status", new { source = "twitch", status });
    }

    private Task BroadcastEventAsync(object payload)
        => _hub.Clients.Group($"user:{LocalUser.Id}")
                       .SendAsync("integration:event", payload);
}
