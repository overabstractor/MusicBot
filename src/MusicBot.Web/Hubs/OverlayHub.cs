using Microsoft.AspNetCore.SignalR;
using MusicBot.Services;

namespace MusicBot.Hubs;

public class OverlayHub : Hub
{
    private readonly UserContextManager       _userContext;
    private readonly PlaybackSyncService      _playbackSync;
    private readonly IntegrationStatusTracker _integrationStatus;
    private readonly QueueSettingsService     _queueSettings;
    private readonly KickVoteService          _kickVote;

    public OverlayHub(UserContextManager userContext, PlaybackSyncService playbackSync, IntegrationStatusTracker integrationStatus, QueueSettingsService queueSettings, KickVoteService kickVote)
    {
        _userContext        = userContext;
        _playbackSync       = playbackSync;
        _integrationStatus  = integrationStatus;
        _queueSettings      = queueSettings;
        _kickVote           = kickVote;
    }

    /// <summary>
    /// Join the overlay group and receive the current queue state plus
    /// the current status of platform integration services.
    /// </summary>
    public async Task JoinUserGroup(string overlayToken = "")
    {
        var services = _userContext.GetOrCreate(LocalUser.Id);

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{LocalUser.Id}");

        await Clients.Caller.SendAsync("queue:updated",      services.Queue.GetState());
        await Clients.Caller.SendAsync("nowplaying:updated", services.Queue.GetNowPlaying());

        var queue = await _playbackSync.GetEnrichedQueueAsync(services);
        if (queue != null)
            await Clients.Caller.SendAsync("spotify:queue-updated", queue);

        // Replay any in-progress downloads so the client doesn't miss them on late connect
        foreach (var dl in _playbackSync.GetActiveDownloads())
        {
            await Clients.Caller.SendAsync("download:started",  new { spotifyUri = dl.SpotifyUri, title = dl.Title, artist = dl.Artist });
            if (dl.Pct > 0)
                await Clients.Caller.SendAsync("download:progress", new { spotifyUri = dl.SpotifyUri, pct = dl.Pct });
        }

        // Platform connection status
        await Clients.Caller.SendAsync("integration:status", new { source = "tiktok",  status = _integrationStatus.TikTokStatus });
        await Clients.Caller.SendAsync("integration:status", new { source = "twitch",  status = _integrationStatus.TwitchStatus });
        await Clients.Caller.SendAsync("integration:status", new { source = "kick",    status = _integrationStatus.KickStatus });

        await Clients.Caller.SendAsync("settings:updated", new
        {
            maxQueueSize    = _queueSettings.MaxQueueSize,
            maxSongsPerUser = _queueSettings.MaxSongsPerUser,
            votingEnabled   = _queueSettings.VotingEnabled,
        });

        // Resume any vote already in progress
        var votePayload = _kickVote.GetCurrentVotePayload();
        if (votePayload != null)
            await Clients.Caller.SendAsync("vote:started", votePayload);
    }
}
