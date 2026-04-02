using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Data;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Manages Twitch OAuth 2.0 Authorization Code flow.
/// Stores tokens in the PlatformConfig table (ConfigJson).
/// Singleton — uses IServiceScopeFactory for DB access.
/// </summary>
public class TwitchAuthService
{
    private readonly TwitchSettings _settings;
    private readonly RelaySettings _relay;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TwitchAuthService> _logger;

    private string? _accessToken;
    private string? _refreshToken;
    private string? _botUsername;
    private DateTimeOffset _expiresAt;

    private static readonly string[] Scopes = { "chat:read", "chat:edit" };

    public bool IsAuthenticated => _accessToken != null;
    public string? BotUsername => _botUsername;

    public TwitchAuthService(
        IOptions<TwitchSettings> settings,
        IOptions<RelaySettings> relay,
        IHttpClientFactory httpFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<TwitchAuthService> logger)
    {
        _settings     = settings.Value;
        _relay        = relay.Value;
        _httpFactory  = httpFactory;
        _scopeFactory = scopeFactory;
        _logger       = logger;
        LoadTokenFromDb();
    }

    /// <summary>Generates the Twitch OAuth authorization URL.</summary>
    public string GetAuthUrl(string? state = null)
    {
        var scope = string.Join(" ", Scopes);
        var url = $"https://id.twitch.tv/oauth2/authorize?response_type=code&client_id={_settings.ClientId}&scope={Uri.EscapeDataString(scope)}&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}&force_verify=true";
        if (!string.IsNullOrEmpty(state))
            url += $"&state={Uri.EscapeDataString(state)}";
        return url;
    }

    /// <summary>Exchanges an authorization code for access + refresh tokens.</summary>
    public async Task HandleCallbackAsync(string code)
    {
        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]   = "authorization_code",
            ["code"]         = code,
            ["redirect_uri"] = _settings.RedirectUri,
        });
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        _accessToken  = json.RootElement.GetProperty("access_token").GetString();
        _refreshToken = json.RootElement.GetProperty("refresh_token").GetString();
        _expiresAt    = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());

        // Fetch the authenticated user's login name for TwitchLib
        _botUsername = await FetchUsernameAsync();

        await SaveTokenToDb();
        _logger.LogInformation("Twitch authenticated as {Username}", _botUsername);
    }

    /// <summary>Returns a valid access token, auto-refreshing if expired.</summary>
    public async Task<string> GetAccessTokenAsync()
    {
        if (_accessToken == null)
            throw new InvalidOperationException("Not authenticated with Twitch");

        if (DateTimeOffset.UtcNow >= _expiresAt.AddMinutes(-5))
            await RefreshTokenAsync();

        return _accessToken;
    }

    /// <summary>Removes stored Twitch token and revokes it with Twitch's OAuth server.</summary>
    public async Task DisconnectAsync()
    {
        var tokenToRevoke = _accessToken;

        _accessToken  = null;
        _refreshToken = null;
        _botUsername  = null;
        _expiresAt    = default;

        // Revoke the token with Twitch so the session is invalidated server-side
        if (!string.IsNullOrWhiteSpace(tokenToRevoke))
        {
            try
            {
                var http = _httpFactory.CreateClient();
                var res = await http.PostAsync(
                    $"https://id.twitch.tv/oauth2/revoke?client_id={Uri.EscapeDataString(_settings.ClientId)}&token={Uri.EscapeDataString(tokenToRevoke)}",
                    null);
                _logger.LogInformation("Twitch token revoked — status {Status}", (int)res.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Twitch token revocation failed (token cleared locally regardless)");
            }
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var cfg = await db.PlatformConfigs
                .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "twitch_auth");
            if (cfg != null)
            {
                db.PlatformConfigs.Remove(cfg);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Twitch token from DB");
        }

        // Tell the Desktop layer to clear Twitch cookies from the main WebView2
        await AppEvents.NotifyPlatformAuthForgotten("twitch");

        _logger.LogInformation("Twitch disconnected");
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private async Task RefreshTokenAsync()
    {
        if (_refreshToken == null) throw new InvalidOperationException("No Twitch refresh token");

        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = _refreshToken,
        });
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        _accessToken = json.RootElement.GetProperty("access_token").GetString();
        _expiresAt   = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());

        if (json.RootElement.TryGetProperty("refresh_token", out var rt))
            _refreshToken = rt.GetString();

        await SaveTokenToDb();
        _logger.LogInformation("Twitch token refreshed");
    }

    private async Task<HttpResponseMessage> SendTokenRequest(Dictionary<string, string> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_relay.Url}/token/twitch");
        request.Headers.Add("X-Relay-Key", _relay.ApiKey);
        request.Content = JsonContent.Create(fields);

        var response = await _httpFactory.CreateClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Twitch token request failed: {response.StatusCode} {error}");
        }
        return response;
    }

    private async Task<string?> FetchUsernameAsync()
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/users");
            req.Headers.Add("Authorization", $"Bearer {_accessToken}");
            req.Headers.Add("Client-Id", _settings.ClientId);
            var res = await client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var json = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
            return json.RootElement.GetProperty("data")[0].GetProperty("login").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Twitch username");
            return null;
        }
    }

    private async Task SaveTokenToDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var tokenJson = JsonSerializer.Serialize(new
            {
                accessToken  = _accessToken,
                refreshToken = _refreshToken,
                botUsername   = _botUsername,
                expiresAt    = _expiresAt.ToUnixTimeSeconds(),
            });

            var existing = await db.PlatformConfigs
                .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "twitch_auth");

            if (existing != null)
            {
                existing.ConfigJson = tokenJson;
            }
            else
            {
                db.PlatformConfigs.Add(new PlatformConfig
                {
                    UserId     = LocalUser.Id,
                    Platform   = "twitch_auth",
                    ConfigJson = tokenJson,
                });
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save Twitch token to DB");
        }
    }

    private void LoadTokenFromDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var cfg = db.PlatformConfigs
                .FirstOrDefault(p => p.UserId == LocalUser.Id && p.Platform == "twitch_auth");
            if (cfg == null) return;

            var json = JsonDocument.Parse(cfg.ConfigJson).RootElement;
            _accessToken  = json.GetProperty("accessToken").GetString();
            _refreshToken = json.GetProperty("refreshToken").GetString();
            _botUsername   = json.TryGetProperty("botUsername", out var bn) ? bn.GetString() : null;
            _expiresAt    = DateTimeOffset.FromUnixTimeSeconds(json.GetProperty("expiresAt").GetInt64());

            _logger.LogInformation("Loaded Twitch token from DB (user: {Username})", _botUsername);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load Twitch token from DB");
        }
    }
}
