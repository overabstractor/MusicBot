using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Services;
using MusicBot.Data;
using MusicBot.Services.Player;
using MusicBot.Services.Spotify;

namespace MusicBot.Services;

public class UserContextManager
{
    private readonly ConcurrentDictionary<Guid, UserServices> _users = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _configuration;

    public event Action<Guid, UserServices>? OnUserCreated;
    public event Action<Guid>? OnUserRemoved;

    private readonly RelaySettings _relay;

    public UserContextManager(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        IOptions<RelaySettings> relay)
    {
        _scopeFactory  = scopeFactory;
        _httpFactory   = httpFactory;
        _loggerFactory = loggerFactory;
        _configuration = configuration;
        _relay         = relay.Value;
    }

    public UserServices GetOrCreate(Guid userId)
    {
        return _users.GetOrAdd(userId, id =>
        {
            var queueConfig = _configuration.GetSection("Queue");
            var maxSize     = queueConfig.GetValue("MaxSize", 50);
            var maxPerUser  = queueConfig.GetValue("MaxSongsPerUser", 10);
            var queue       = new QueueService(maxSize, maxPerUser);

            var spotifySettings = _configuration.GetSection("Spotify");
            var settings = new SpotifySettings
            {
                ClientId     = spotifySettings["ClientId"]     ?? "",
                ClientSecret = spotifySettings["ClientSecret"] ?? "",
                RedirectUri  = spotifySettings["RedirectUri"]  ?? ""
            };

            var spotify = new SpotifyService(
                Options.Create(settings),
                _relay,
                _httpFactory,
                _loggerFactory.CreateLogger<SpotifyService>(),
                _scopeFactory,
                id);

            var player = new LocalPlayerService(
                _loggerFactory.CreateLogger<LocalPlayerService>());

            var services = new UserServices(id, queue, spotify, player);
            OnUserCreated?.Invoke(id, services);
            return services;
        });
    }

    public UserServices? Get(Guid userId)
    {
        return _users.TryGetValue(userId, out var services) ? services : null;
    }

    public IEnumerable<(Guid UserId, UserServices Services)> GetAllActive()
    {
        return _users.Select(kv => (kv.Key, kv.Value));
    }

    public void Remove(Guid userId)
    {
        if (_users.TryRemove(userId, out var services))
        {
            services.Player.Dispose();
            OnUserRemoved?.Invoke(userId);
        }
    }

    public async Task<UserServices?> GetBySlugAsync(string slug)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Slug == slug);
        return user != null ? GetOrCreate(user.Id) : null;
    }

    public async Task<UserServices?> GetByOverlayTokenAsync(string overlayToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.OverlayToken == overlayToken);
        return user != null ? GetOrCreate(user.Id) : null;
    }
}
