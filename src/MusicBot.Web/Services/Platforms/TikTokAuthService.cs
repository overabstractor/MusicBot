using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;
using MusicBot.Hubs;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Manages TikTok session cookies used for sending chat messages.
/// Cookies are captured via an in-app WebView2 login window (no manual extraction needed).
/// Stored in PlatformConfig table with platform key "tiktok_auth".
/// Singleton — uses IServiceScopeFactory for DB access.
/// </summary>
public class TikTokAuthService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TikTokAuthService> _logger;
    private readonly IHubContext<OverlayHub> _hub;

    private string? _cookieString;
    private string? _username;
    private DateTimeOffset _savedAt;

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_cookieString);
    public string? CookieString => _cookieString;
    /// <summary>TikTok @handle (without @) of the logged-in account, if detected.</summary>
    public string? Username => _username;
    /// <summary>Set to true when the login window is closed before completing login.</summary>
    public bool LoginCancelled { get; private set; }

    // Stores the initial DB load so App.xaml.cs can await it before triggering restore
    private readonly Task _initialLoad;

    public TikTokAuthService(IServiceScopeFactory scopeFactory, ILogger<TikTokAuthService> logger, IHubContext<OverlayHub> hub)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _hub          = hub;

        // Subscribe to Desktop events
        AppEvents.OnTikTokCookiesCaptured  += OnCookiesCaptured;
        AppEvents.OnTikTokLoginCancelled   += () => { LoginCancelled = true; };

        _initialLoad = LoadFromDbAsync();
    }

    /// <summary>
    /// Called by App.xaml.cs after it has subscribed to AppEvents.OnTikTokSessionRestoreRequested.
    /// Waits for the initial DB load to finish, then fires the restore event if authenticated.
    /// This avoids the race condition where the event fired before the WPF subscriber was ready.
    /// </summary>
    public async Task InitAsync()
    {
        await _initialLoad;
        if (IsAuthenticated)
            AppEvents.RequestTikTokSessionRestore();
    }

    public void ResetCancelledFlag() => LoginCancelled = false;

    /// <summary>
    /// Updates the in-memory cookie string with fresh values read from the live WebView2 context.
    /// Called after a successful chat send to capture rotated tokens (e.g. msToken).
    /// Does NOT emit SignalR — this is a silent token refresh, not a login event.
    /// </summary>
    public void UpdateCookiesFromWebView(string freshCookieString)
    {
        if (string.IsNullOrWhiteSpace(freshCookieString)) return;
        _cookieString = freshCookieString;
        _savedAt      = DateTimeOffset.UtcNow;
        _ = SaveToDbAsync(freshCookieString, _username);
    }

    // ── Called by Desktop WebView2 window after login ─────────────────────────

    private void OnCookiesCaptured(string cookieString, string? username)
    {
        _cookieString = cookieString;
        _username     = username;
        _savedAt      = DateTimeOffset.UtcNow;
        _logger.LogInformation("TikTok cookies captured — user={Username}", username ?? "(unknown)");
        _ = SaveToDbAsync(cookieString, username);

        // Push auth:updated so the frontend stops polling immediately
        _ = _hub.Clients
                .Group($"user:{LocalUser.Id}")
                .SendAsync("auth:updated", new { platform = "tiktok", authenticated = true, username });
    }

    // ── Public ────────────────────────────────────────────────────────────────

    public async Task DisconnectAsync()
    {
        _cookieString = null;
        _username     = null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var existing = await db.PlatformConfigs
            .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "tiktok_auth");
        if (existing != null)
        {
            db.PlatformConfigs.Remove(existing);
            await db.SaveChangesAsync();
        }
        _logger.LogInformation("TikTok auth disconnected");

        // Tell the Desktop layer to clear WebView2 session cookies
        await AppEvents.NotifyPlatformAuthForgotten("tiktok");
    }

    // ── DPAPI encryption (Windows CurrentUser scope) ──────────────────────────

    private static string Protect(string plaintext)
    {
        var bytes     = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string? TryUnprotect(string base64)
    {
        try
        {
            var encrypted = Convert.FromBase64String(base64);
            var bytes     = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Not encrypted (legacy plain-text) — return as-is for backward-compatible migration
            return null;
        }
    }

    // ── DB persistence ────────────────────────────────────────────────────────

    private async Task LoadFromDbAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var cfg = await db.PlatformConfigs
                .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "tiktok_auth");
            if (cfg == null) return;

            var doc = JsonDocument.Parse(cfg.ConfigJson);
            if (doc.RootElement.TryGetProperty("username", out var un))
                _username = un.GetString();
            if (doc.RootElement.TryGetProperty("savedAt", out var sa))
                _savedAt = sa.GetDateTimeOffset();

            if (doc.RootElement.TryGetProperty("cookieString", out var cs))
            {
                var raw = cs.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    // Attempt DPAPI decrypt. If it fails the value is legacy plain-text —
                    // accept it for this session and re-encrypt it on next save.
                    var encrypted = doc.RootElement.TryGetProperty("encrypted", out var enc) && enc.GetBoolean();
                    if (encrypted)
                    {
                        _cookieString = TryUnprotect(raw) ?? raw; // fallback if different machine
                        if (_cookieString == raw)
                            _logger.LogWarning("TikTok auth: DPAPI decrypt failed (different machine?), using raw value");
                    }
                    else
                    {
                        _cookieString = raw;
                        // Migrate legacy plain-text to encrypted on next save
                        _logger.LogDebug("TikTok auth: migrating legacy plain-text cookies to DPAPI encryption");
                        _ = SaveToDbAsync(raw, _username);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_cookieString))
            {
                _logger.LogInformation("TikTok auth loaded from DB — user={Username} (saved {Age:g} ago)",
                    _username ?? "(unknown)", DateTimeOffset.UtcNow - _savedAt);
                // Session restore is triggered by InitAsync() after App.xaml.cs has subscribed.
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TikTok: could not load auth from DB");
        }
    }

    private async Task SaveToDbAsync(string cookieString, string? username)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var json = JsonSerializer.Serialize(new
            {
                cookieString = Protect(cookieString),
                encrypted    = true,
                username,
                savedAt = DateTimeOffset.UtcNow,
            });

            var existing = await db.PlatformConfigs
                .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "tiktok_auth");

            if (existing == null)
            {
                db.PlatformConfigs.Add(new PlatformConfig
                {
                    UserId     = LocalUser.Id,
                    Platform   = "tiktok_auth",
                    ConfigJson = json,
                });
            }
            else
            {
                existing.ConfigJson = json;
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("TikTok auth saved to DB");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TikTok: could not save auth to DB");
        }
    }
}
