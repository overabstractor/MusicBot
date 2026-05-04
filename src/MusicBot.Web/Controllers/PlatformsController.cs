using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Data;
using MusicBot.Services.Platforms;

namespace MusicBot.Controllers;

[ApiController]
[Route("api/platforms")]
[Tags("Platforms")]
public class PlatformsController : ControllerBase
{
    private readonly MusicBotDbContext         _db;
    private readonly PlatformConnectionManager _manager;
    private readonly TwitchAuthService         _twitchAuth;
    private readonly KickAuthService           _kickAuth;
    private readonly TikTokAuthService         _tiktokAuth;
    private readonly TikTokSettings            _tiktokSettings;

    public PlatformsController(
        MusicBotDbContext db,
        PlatformConnectionManager manager,
        TwitchAuthService twitchAuth,
        KickAuthService kickAuth,
        TikTokAuthService tiktokAuth,
        IOptions<TikTokSettings> tiktokSettings)
    {
        _db              = db;
        _manager         = manager;
        _twitchAuth      = twitchAuth;
        _kickAuth        = kickAuth;
        _tiktokAuth      = tiktokAuth;
        _tiktokSettings  = tiktokSettings.Value;
    }

    /// <summary>Get all platform configs and their current connection status</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var configs = await _db.PlatformConfigs
            .Where(p => p.UserId == LocalUser.Id)
            .ToListAsync();

        var result = new[] { "tiktok", "twitch", "kick" }.Select(platform =>
        {
            var cfg   = configs.FirstOrDefault(c => c.Platform == platform);
            var state = _manager.GetState(LocalUser.Id, platform);
            return new PlatformStateDto
            {
                Platform     = platform,
                Status       = state.Status.ToString().ToLower(),
                ErrorMessage = state.ErrorMessage,
                AutoConnect  = cfg?.AutoConnect ?? false,
                Config       = cfg?.ConfigJson != null
                    ? JsonSerializer.Deserialize<JsonElement>(cfg.ConfigJson)
                    : null
            };
        });

        return Ok(result);
    }

    /// <summary>Save TikTok configuration</summary>
    [HttpPut("tiktok")]
    public async Task<IActionResult> SaveTikTok([FromBody] SaveTikTokRequest req)
    {
        await UpsertConfig("tiktok",
            JsonSerializer.Serialize(new { username = req.Username, giftInterruptThreshold = req.GiftInterruptThreshold }),
            req.AutoConnect);

        _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
        return NoContent();
    }

    /// <summary>Save Twitch configuration (channel only — token via OAuth)</summary>
    [HttpPut("twitch")]
    public async Task<IActionResult> SaveTwitch([FromBody] SaveTwitchRequest req)
    {
        await UpsertConfig("twitch",
            JsonSerializer.Serialize(new { channel = req.Channel, botUsername = req.BotUsername }),
            req.AutoConnect);

        _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
        return NoContent();
    }

    /// <summary>Save Kick configuration</summary>
    [HttpPut("kick")]
    public async Task<IActionResult> SaveKick([FromBody] SaveKickRequest req)
    {
        await UpsertConfig("kick",
            JsonSerializer.Serialize(new { channel = req.Channel }),
            req.AutoConnect);

        _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);
        return NoContent();
    }

    /// <summary>Connect a platform</summary>
    [HttpPost("{platform}/connect")]
    public async Task<IActionResult> Connect(string platform)
    {
        var cfg = await _db.PlatformConfigs
            .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == platform);

        if (cfg == null)
            return BadRequest(new { error = $"No configuration saved for {platform}" });

        // Ensure slug is cached so RouteCommand can resolve user services
        _manager.SetUserSlug(LocalUser.Id, LocalUser.Slug);

        switch (platform.ToLower())
        {
            case "tiktok":
            {
                var c = JsonSerializer.Deserialize<TikTokJson>(cfg.ConfigJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                // Use username from in-app login if available, otherwise from saved config
                var username = _tiktokAuth.Username ?? c?.Username ?? "";
                if (string.IsNullOrWhiteSpace(username))
                    return BadRequest(new { error = "Inicia sesión en TikTok primero" });
                // Prefer cookies captured via in-app login window; fall back to appsettings
                string? cookieStr = _tiktokAuth.CookieString;
                if (cookieStr == null && _tiktokSettings.CanSendMessages)
                    cookieStr = !string.IsNullOrWhiteSpace(_tiktokSettings.CookieString)
                        ? _tiktokSettings.CookieString
                        : $"sessionid={_tiktokSettings.SessionId}";
                _manager.ConnectTikTok(LocalUser.Id, new PlatformConnectionManager.TikTokPlatformConfig(
                    username,
                    _tiktokSettings.SigningServerUrl,
                    _tiktokSettings.SigningServerApiKey,
                    CookieString: cookieStr,
                    GiftInterruptThreshold: c?.GiftInterruptThreshold ?? 100));
                break;
            }
            case "twitch":
            {
                var c = JsonSerializer.Deserialize<TwitchJson>(cfg.ConfigJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (string.IsNullOrWhiteSpace(c?.Channel))
                    return BadRequest(new { error = "Twitch channel is required" });
                if (!_twitchAuth.IsAuthenticated)
                    return BadRequest(new { error = "Twitch OAuth not connected — click 'Conectar con Twitch' first" });

                var token = await _twitchAuth.GetAccessTokenAsync();
                var botUser = _twitchAuth.BotUsername ?? c.BotUsername ?? "";
                if (string.IsNullOrWhiteSpace(botUser))
                    return BadRequest(new { error = "No se encontró el username del bot — reconecta la cuenta de Twitch." });
                _manager.ConnectTwitch(LocalUser.Id, new PlatformConnectionManager.TwitchPlatformConfig(c.Channel, botUser, $"oauth:{token}"));
                break;
            }
            case "kick":
            {
                if (!_kickAuth.IsAuthenticated)
                    return BadRequest(new { error = "Kick account not linked — click 'Conectar con Kick' first" });
                var c = JsonSerializer.Deserialize<KickJson>(cfg.ConfigJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var channel = _kickAuth.ChannelName ?? c?.Channel ?? "";
                if (string.IsNullOrWhiteSpace(channel))
                    return BadRequest(new { error = "Kick channel not available — connect Kick account first" });
                _manager.ConnectKick(LocalUser.Id, new PlatformConnectionManager.KickPlatformConfig(channel));
                break;
            }
            default:
                return BadRequest(new { error = "Unknown platform" });
        }

        return NoContent();
    }

    /// <summary>Disconnect a platform (stops chat, keeps auth tokens for quick reconnect)</summary>
    [HttpPost("{platform}/disconnect")]
    public IActionResult Disconnect(string platform)
    {
        _manager.Disconnect(LocalUser.Id, platform);
        return NoContent();
    }

    /// <summary>Forget a platform account — clears all auth tokens/sessions, stops chat, disables auto-connect</summary>
    [HttpPost("{platform}/forget")]
    public async Task<IActionResult> Forget(string platform)
    {
        _manager.Disconnect(LocalUser.Id, platform);

        // Disable auto-connect so the app doesn't try to reconnect on startup without credentials
        var cfg = await _db.PlatformConfigs
            .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == platform);
        if (cfg != null)
        {
            cfg.AutoConnect = false;
            await _db.SaveChangesAsync();
        }

        switch (platform.ToLower())
        {
            case "tiktok":  await _tiktokAuth.DisconnectAsync(); break;
            case "twitch":  await _twitchAuth.DisconnectAsync(); break;
            case "kick":    await _kickAuth.DisconnectAsync();   break;
        }

        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task UpsertConfig(string platform, string json, bool autoConnect)
    {
        var existing = await _db.PlatformConfigs
            .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == platform);

        if (existing != null)
        {
            existing.ConfigJson  = json;
            existing.AutoConnect = autoConnect;
        }
        else
        {
            _db.PlatformConfigs.Add(new PlatformConfig
            {
                UserId      = LocalUser.Id,
                Platform    = platform,
                ConfigJson  = json,
                AutoConnect = autoConnect
            });
        }

        await _db.SaveChangesAsync();
    }

    private sealed class TikTokJson { public string? Username { get; set; } public int GiftInterruptThreshold { get; set; } = 100; }
    private sealed class TwitchJson { public string? Channel { get; set; } public string? BotUsername { get; set; } public string? OAuthToken { get; set; } }
    private sealed class KickJson   { public string? Channel { get; set; } }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public class PlatformStateDto
{
    public string        Platform     { get; set; } = string.Empty;
    public string        Status       { get; set; } = "disconnected";
    public string?       ErrorMessage { get; set; }
    public bool          AutoConnect  { get; set; }
    public JsonElement?  Config       { get; set; }
}

public class SaveTikTokRequest
{
    public string Username               { get; set; } = string.Empty;
    public bool   AutoConnect            { get; set; }
    public int    GiftInterruptThreshold { get; set; } = 100;
}

public class SaveTwitchRequest
{
    public string Channel      { get; set; } = string.Empty;
    public string BotUsername  { get; set; } = string.Empty;
    public bool   AutoConnect  { get; set; }
}

public class SaveKickRequest
{
    public string Channel      { get; set; } = string.Empty;
    public bool   AutoConnect  { get; set; }
}
