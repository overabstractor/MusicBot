using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Data;
using MusicBot.Hubs;

namespace MusicBot.Services.Platforms;

/// <summary>
/// Manages YouTube/Google session cookies used by yt-dlp to bypass bot detection
/// ("Sign in to confirm you're not a bot" + HTTP 429).
/// Cookies are captured via an in-app WebView2 login window and written to a
/// Netscape-format cookies.txt that yt-dlp reads with --cookies.
/// State is persisted in PlatformConfig with platform key "youtube_auth".
/// Singleton — uses IServiceScopeFactory for DB access.
/// </summary>
public class YouTubeAuthService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<YouTubeAuthService> _logger;
    private readonly IHubContext<OverlayHub> _hub;

    private bool   _enabled;
    private string? _cookiesFilePath;
    private string? _accountLabel;
    private DateTimeOffset _savedAt;

    /// <summary>The toggle. When false, yt-dlp ignores cookies even if the file exists.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>True when cookies are present on disk and the toggle is on.</summary>
    public bool IsConnected => _enabled
        && !string.IsNullOrWhiteSpace(_cookiesFilePath)
        && File.Exists(_cookiesFilePath);

    /// <summary>Absolute path to the Netscape-format cookies.txt file. Null if never connected.</summary>
    public string? CookiesFilePath => _cookiesFilePath;

    /// <summary>Google account label (email or display name) when extractable, else null.</summary>
    public string? AccountLabel => _accountLabel;

    public DateTimeOffset SavedAt => _savedAt;

    /// <summary>Set to true when the login window is closed before completing login.</summary>
    public bool LoginCancelled { get; private set; }

    public static string DefaultCookiesPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicBot", "youtube_cookies.txt");

    private readonly Task _initialLoad;

    public YouTubeAuthService(IServiceScopeFactory scopeFactory, ILogger<YouTubeAuthService> logger, IHubContext<OverlayHub> hub)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _hub          = hub;

        AppEvents.OnYouTubeCookiesCaptured += OnCookiesCaptured;
        AppEvents.OnYouTubeLoginCancelled  += () => { LoginCancelled = true; };

        _initialLoad = LoadFromDbAsync();
    }

    /// <summary>
    /// Called by App.xaml.cs after subscribing to OnYouTubeSessionRestoreRequested.
    /// Triggers a silent restore if the feature is enabled and a cookies file is referenced.
    /// </summary>
    public async Task InitAsync()
    {
        await _initialLoad;
        if (_enabled && !string.IsNullOrWhiteSpace(_cookiesFilePath))
            AppEvents.RequestYouTubeSessionRestore();
    }

    public void ResetCancelledFlag() => LoginCancelled = false;

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task EnableAsync()
    {
        _enabled = true;
        await SaveToDbAsync();
        await NotifyStatusAsync();
    }

    public async Task DisableAsync()
    {
        _enabled = false;
        await SaveToDbAsync();
        await NotifyStatusAsync();
    }

    /// <summary>
    /// Disconnect: deletes the cookies file, clears WebView2 session, and resets state.
    /// The toggle (IsEnabled) is preserved — re-login will re-populate cookies.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cookiesFilePath) && File.Exists(_cookiesFilePath))
        {
            try { File.Delete(_cookiesFilePath); }
            catch (Exception ex) { _logger.LogWarning(ex, "YouTube: could not delete cookies file"); }
        }

        _cookiesFilePath = null;
        _accountLabel    = null;
        _savedAt         = default;

        await SaveToDbAsync();
        await AppEvents.NotifyPlatformAuthForgotten("youtube");
        await NotifyStatusAsync();
        _logger.LogInformation("YouTube auth disconnected");
    }

    // ── Called by Desktop WebView2 window after capturing cookies ─────────────

    private void OnCookiesCaptured(string cookiesFilePath, string? accountLabel)
    {
        _cookiesFilePath = cookiesFilePath;
        _accountLabel    = accountLabel;
        _savedAt         = DateTimeOffset.UtcNow;
        // Capturing cookies implicitly enables the feature
        _enabled         = true;

        _logger.LogInformation("YouTube cookies captured → {Path} (account={Account})",
            cookiesFilePath, accountLabel ?? "(unknown)");
        _ = SaveToDbAsync();
        _ = NotifyStatusAsync();
    }

    private Task NotifyStatusAsync() =>
        _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync(
            "auth:updated",
            new { platform = "youtube", authenticated = IsConnected, enabled = _enabled, account = _accountLabel });

    // ── DB persistence ────────────────────────────────────────────────────────

    private async Task LoadFromDbAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var cfg = await db.PlatformConfigs
                .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "youtube_auth");
            if (cfg == null) return;

            var doc = JsonDocument.Parse(cfg.ConfigJson);
            if (doc.RootElement.TryGetProperty("enabled", out var en))
                _enabled = en.GetBoolean();
            if (doc.RootElement.TryGetProperty("cookiesFilePath", out var cp))
                _cookiesFilePath = cp.GetString();
            if (doc.RootElement.TryGetProperty("accountLabel", out var al))
                _accountLabel = al.GetString();
            if (doc.RootElement.TryGetProperty("savedAt", out var sa))
                _savedAt = sa.GetDateTimeOffset();

            _logger.LogInformation("YouTube auth loaded from DB — enabled={Enabled} connected={Connected} account={Account}",
                _enabled, IsConnected, _accountLabel ?? "(unknown)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube: could not load auth from DB");
        }
    }

    private async Task SaveToDbAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var json = JsonSerializer.Serialize(new
            {
                enabled         = _enabled,
                cookiesFilePath = _cookiesFilePath,
                accountLabel    = _accountLabel,
                savedAt         = _savedAt == default ? DateTimeOffset.UtcNow : _savedAt,
            });

            var existing = await db.PlatformConfigs
                .FirstOrDefaultAsync(p => p.UserId == LocalUser.Id && p.Platform == "youtube_auth");

            if (existing == null)
            {
                db.PlatformConfigs.Add(new PlatformConfig
                {
                    UserId     = LocalUser.Id,
                    Platform   = "youtube_auth",
                    ConfigJson = json,
                });
            }
            else
            {
                existing.ConfigJson = json;
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube: could not save auth to DB");
        }
    }
}
