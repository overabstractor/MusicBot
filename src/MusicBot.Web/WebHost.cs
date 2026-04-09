using Microsoft.EntityFrameworkCore;
using Serilog;
using Microsoft.OpenApi.Models;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;
using MusicBot.Hubs;
using MusicBot.Services;
using MusicBot.Services.Downloader;
using MusicBot.Services.Library;
using MusicBot.Services.Metadata;
using MusicBot.Services.Platforms;
using MusicBot.Services.Spotify;
using Scalar.AspNetCore;

namespace MusicBot;

/// <summary>
/// Builds and configures the ASP.NET Core web host.
/// Split into CreateBuilder + Configure so callers (e.g. MusicBot.Desktop)
/// can register their own services between the two steps.
/// </summary>
public static class WebHost
{
    /// <summary>
    /// Creates the WebApplicationBuilder with all application services registered.
    /// The caller may add/replace services before calling <see cref="Configure"/>.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseSerilog();

        // Explicitly load user secrets from MusicBot.Web assembly regardless of environment.
        // Needed because the entry point is MusicBot.Desktop (no UserSecretsId there).
        builder.Configuration.AddUserSecrets(typeof(WebHost).Assembly, optional: true);

        // ── User data directory (persists across Velopack updates) ───────────
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MusicBot");
        Directory.CreateDirectory(dataDir);
        builder.Configuration["DataDirectory"] = dataDir;
        builder.Configuration["ConnectionStrings:DefaultConnection"] =
            $"Data Source={Path.Combine(dataDir, "musicbot.db")}";

        // ── Configuration ────────────────────────────────────────────────────
        builder.Services.Configure<SpotifySettings>(builder.Configuration.GetSection("Spotify"));
        builder.Services.Configure<TikTokSettings>(builder.Configuration.GetSection("TikTok"));
        builder.Services.Configure<TwitchSettings>(builder.Configuration.GetSection("Twitch"));
        builder.Services.Configure<KickSettings>(builder.Configuration.GetSection("Kick"));
        builder.Services.Configure<MusicLibrarySettings>(builder.Configuration.GetSection("MusicLibrary"));
        builder.Services.Configure<RelaySettings>(builder.Configuration.GetSection("Relay"));

        // ── Database ─────────────────────────────────────────────────────────
        builder.Services.AddDbContext<MusicBotDbContext>(o =>
            o.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                        ?? "Data Source=musicbot.db"));

        // ── Core services ────────────────────────────────────────────────────
        builder.Services.AddHttpClient();
        builder.Services.AddHttpClient("itunes");

        builder.Services.AddSingleton<IMetadataService, ItunesMetadataService>();
        builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();
        builder.Services.AddSingleton<YtDlpDownloaderService>();
        builder.Services.AddHostedService<YtDlpSetupService>();

        builder.Services.AddSingleton<UserContextManager>();
        builder.Services.AddSingleton<KickVoteService>();
        builder.Services.AddSingleton<CommandRouterService>();

        builder.Services.AddSingleton<PlaybackSyncService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PlaybackSyncService>());
        builder.Services.AddHostedService<SignalRBroadcastService>();
        builder.Services.AddHostedService<QueuePersistenceService>();
        builder.Services.AddSingleton<TikTokRoomResolver>();
        builder.Services.AddSingleton<PlatformConnectionManager>();
        builder.Services.AddSingleton<TwitchAuthService>();
        builder.Services.AddSingleton<KickAuthService>();
        builder.Services.AddSingleton<TikTokAuthService>();
        builder.Services.AddHostedService<PlatformAutoConnectService>();
        builder.Services.AddSingleton<IntegrationStatusTracker>();
        builder.Services.AddSingleton<QueueSettingsService>();
        builder.Services.AddSingleton<BannedSongService>();
        builder.Services.AddSingleton<AutoQueueService>();
        builder.Services.AddSingleton<PlaylistLibraryService>();
        builder.Services.AddSingleton<ChatResponseService>();
        builder.Services.AddSingleton<PresenceCheckService>();
        builder.Services.AddSingleton<TickerMessageService>();

        builder.Services.AddSignalR()
            .AddJsonProtocol(o =>
                o.PayloadSerializerOptions.PropertyNamingPolicy =
                    System.Text.Json.JsonNamingPolicy.CamelCase);

        // AddApplicationPart ensures MVC discovers controllers in MusicBot.Web.dll
        // even when Assembly.GetEntryAssembly() points to MusicBot.Desktop.dll.
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(WebHost).Assembly)
            .AddJsonOptions(o =>
                o.JsonSerializerOptions.PropertyNamingPolicy =
                    System.Text.Json.JsonNamingPolicy.CamelCase);

        // ── OpenAPI ──────────────────────────────────────────────────────────
        builder.Services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo { Title = "MusicBot API", Version = "v1" };
                return Task.CompletedTask;
            });
        });

        // ── CORS ─────────────────────────────────────────────────────────────
        builder.Services.AddCors(o =>
        {
            o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            o.AddPolicy("SignalR", p =>
                p.SetIsOriginAllowed(_ => true).AllowAnyMethod().AllowAnyHeader().AllowCredentials());
        });

        return builder;
    }

    /// <summary>
    /// Builds the WebApplication from the given builder and configures the middleware pipeline.
    /// </summary>
    public static WebApplication Configure(WebApplicationBuilder builder)
    {
        var app = builder.Build();

        // ── DB init + seed local user ────────────────────────────────────────
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            db.Database.EnsureCreated();

            // Safe migrations for existing databases
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN IsSystem INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN IsPinned INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PlaylistLibraries ADD COLUMN PinOrder INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE PersistedQueueItems ADD COLUMN IsPlaylistItem INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }

            if (!db.Users.Any())
            {
                db.Users.Add(new AppUser
                {
                    Id           = LocalUser.Id,
                    Username     = LocalUser.Username,
                    Slug         = LocalUser.Slug,
                    PasswordHash = "",
                    OverlayToken = LocalUser.OverlayToken,
                });
                db.SaveChanges();
            }
        }

        // Seed system playlists (Liked Songs)
        var plSvc = app.Services.GetRequiredService<PlaylistLibraryService>();
        plSvc.EnsureSystemPlaylistsAsync().GetAwaiter().GetResult();

        app.UseCors();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var path = ctx.File.Name;
                // Vite hashes JS/CSS asset names — cache them indefinitely
                if ((path.EndsWith(".js") || path.EndsWith(".css")) &&
                    System.Text.RegularExpressions.Regex.IsMatch(path, @"-[A-Za-z0-9]{8,}\.(js|css)$"))
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
                else
                {
                    // index.html and everything else: always revalidate
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                }
            }
        });

        app.MapOpenApi();
        app.MapScalarApiReference(o => o
            .WithTitle("MusicBot API")
            .WithTheme(ScalarTheme.DeepSpace)
            .WithDefaultHttpClient(ScalarTarget.JavaScript, ScalarClient.Fetch));

        app.MapControllers();
        app.MapHub<OverlayHub>("/hub/overlay").RequireCors("SignalR");
        app.MapGet("/health", () => Results.Ok("ok")).AllowAnonymous();
        app.MapFallbackToFile("index.html");

        return app;
    }
}
