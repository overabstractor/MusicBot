using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Data;

namespace MusicBot.Services.Platforms;

/// <summary>
/// On startup, auto-connects all platforms that have AutoConnect = true in the database.
/// </summary>
public class PlatformAutoConnectService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlatformConnectionManager _manager;
    private readonly TwitchAuthService _twitchAuth;
    private readonly TikTokAuthService _tiktokAuth;
    private readonly KickAuthService _kickAuth;
    private readonly TikTokSettings _tiktokSettings;
    private readonly ILogger<PlatformAutoConnectService> _logger;

    public PlatformAutoConnectService(
        IServiceScopeFactory scopeFactory,
        PlatformConnectionManager manager,
        TwitchAuthService twitchAuth,
        TikTokAuthService tiktokAuth,
        KickAuthService kickAuth,
        IOptions<TikTokSettings> tiktokSettings,
        ILogger<PlatformAutoConnectService> logger)
    {
        _scopeFactory    = scopeFactory;
        _manager         = manager;
        _twitchAuth      = twitchAuth;
        _tiktokAuth      = tiktokAuth;
        _kickAuth        = kickAuth;
        _tiktokSettings  = tiktokSettings.Value;
        _logger          = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(() => ConnectAllAsync(ct), ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ConnectAllAsync(CancellationToken ct)
    {
        // Brief delay so the rest of the app finishes starting
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var configs = await db.PlatformConfigs
                .Where(p => p.UserId == LocalUser.Id && p.AutoConnect)
                .ToListAsync(ct);

            if (configs.Count == 0) return;

            _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var cfg in configs)
            {
                try
                {
                    switch (cfg.Platform)
                    {
                        case "tiktok":
                        {
                            if (!_tiktokAuth.IsAuthenticated)
                            {
                                _logger.LogWarning("Auto-connect: TikTok account not linked — skipping");
                                break;
                            }
                            var c = JsonSerializer.Deserialize<TikTokJson>(cfg.ConfigJson, opts);
                            if (string.IsNullOrWhiteSpace(c?.Username)) break;
                            string? cookieStr = _tiktokAuth.CookieString;
                            if (cookieStr == null && _tiktokSettings.CanSendMessages)
                                cookieStr = !string.IsNullOrWhiteSpace(_tiktokSettings.CookieString)
                                    ? _tiktokSettings.CookieString
                                    : $"sessionid={_tiktokSettings.SessionId}";
                            _manager.ConnectTikTok(LocalUser.Id, new PlatformConnectionManager.TikTokPlatformConfig(
                                c.Username,
                                _tiktokSettings.SigningServerUrl,
                                _tiktokSettings.SigningServerApiKey,
                                CookieString: cookieStr));
                            _logger.LogInformation("Auto-connect: TikTok @{User}", c.Username);
                            break;
                        }
                        case "twitch":
                        {
                            if (!_twitchAuth.IsAuthenticated)
                            {
                                _logger.LogWarning("Auto-connect: Twitch OAuth not configured — skipping");
                                break;
                            }
                            var c = JsonSerializer.Deserialize<TwitchJson>(cfg.ConfigJson, opts);
                            if (string.IsNullOrWhiteSpace(c?.Channel)) break;
                            var botUser = _twitchAuth.BotUsername ?? c.BotUsername ?? "";
                            if (string.IsNullOrWhiteSpace(botUser))
                            {
                                _logger.LogWarning("Auto-connect: Twitch bot username not available — skipping");
                                break;
                            }
                            var token = await _twitchAuth.GetAccessTokenAsync();
                            _manager.ConnectTwitch(LocalUser.Id, new PlatformConnectionManager.TwitchPlatformConfig(
                                c.Channel, botUser, $"oauth:{token}"));
                            _logger.LogInformation("Auto-connect: Twitch #{Channel}", c.Channel);
                            break;
                        }
                        case "kick":
                        {
                            if (!_kickAuth.IsAuthenticated)
                            {
                                _logger.LogWarning("Auto-connect: Kick account not linked — skipping");
                                break;
                            }
                            var c = JsonSerializer.Deserialize<KickJson>(cfg.ConfigJson, opts);
                            if (string.IsNullOrWhiteSpace(c?.Channel)) break;
                            _manager.ConnectKick(LocalUser.Id, new PlatformConnectionManager.KickPlatformConfig(c.Channel));
                            _logger.LogInformation("Auto-connect: Kick #{Channel}", c.Channel);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-connect failed for {Platform}", cfg.Platform);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlatformAutoConnectService error");
        }
    }

    private sealed class TikTokJson { public string? Username { get; set; } }
    private sealed class TwitchJson { public string? Channel { get; set; } public string? BotUsername { get; set; } }
    private sealed class KickJson   { public string? Channel { get; set; } }
}
