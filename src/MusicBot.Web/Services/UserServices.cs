using System.Collections.Concurrent;
using MusicBot.Core.Interfaces;

namespace MusicBot.Services;

public class UserServices
{
    public Guid UserId { get; }
    public IQueueService Queue { get; }
    public ISpotifyService Spotify { get; }
    public ILocalPlayerService Player { get; }

    /// Device ID of the Spotify Web Playback SDK player (kept for backward compat, not used in local playback).
    public string? SpotifyDeviceId { get; set; }

    /// Maps TrackId → (RequestedBy, Platform) for songs added via the play command.
    public ConcurrentDictionary<string, SongRequest> RequestedByMap { get; } = new();

    public record SongRequest(string RequestedBy, string Platform);

    public UserServices(Guid userId, IQueueService queue, ISpotifyService spotify, ILocalPlayerService player)
    {
        UserId  = userId;
        Queue   = queue;
        Spotify = spotify;
        Player  = player;
    }
}
