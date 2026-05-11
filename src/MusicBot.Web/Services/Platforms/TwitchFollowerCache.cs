using System.Collections.Concurrent;

namespace MusicBot.Services.Platforms;

/// <summary>
/// In-memory cache for Twitch follower lookups. Per (broadcasterId, userId) entry
/// with a TTL to avoid hitting the Helix API on every chat message.
/// </summary>
public class TwitchFollowerCache
{
    private readonly TwitchAuthService _auth;
    private readonly ILogger<TwitchFollowerCache> _logger;
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(5);

    private record Entry(bool IsFollower, DateTimeOffset At);

    // broadcasterId → (userId → Entry)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _cache = new();

    // login → broadcasterId
    private readonly ConcurrentDictionary<string, string> _broadcasterIds = new(StringComparer.OrdinalIgnoreCase);

    public TwitchFollowerCache(TwitchAuthService auth, ILogger<TwitchFollowerCache> logger)
    {
        _auth   = auth;
        _logger = logger;
    }

    /// <summary>Resolves and caches the broadcaster_id for a Twitch channel login.</summary>
    public async Task<string?> ResolveBroadcasterIdAsync(string login)
    {
        if (string.IsNullOrWhiteSpace(login)) return null;
        if (_broadcasterIds.TryGetValue(login, out var cached)) return cached;
        var id = await _auth.GetUserIdByLoginAsync(login);
        if (id != null) _broadcasterIds[login] = id;
        return id;
    }

    /// <summary>Cache-aware check: is user X a follower of broadcaster Y?</summary>
    public async Task<bool> IsFollowerAsync(string broadcasterId, string userId)
    {
        if (string.IsNullOrEmpty(broadcasterId) || string.IsNullOrEmpty(userId)) return false;

        var channelCache = _cache.GetOrAdd(broadcasterId, _ => new ConcurrentDictionary<string, Entry>());
        if (channelCache.TryGetValue(userId, out var entry) && DateTimeOffset.UtcNow - entry.At < _ttl)
            return entry.IsFollower;

        var isFollower = await _auth.IsFollowerAsync(broadcasterId, userId);
        channelCache[userId] = new Entry(isFollower, DateTimeOffset.UtcNow);
        return isFollower;
    }
}
