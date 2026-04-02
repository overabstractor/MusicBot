using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;

namespace MusicBot.Services;

/// <summary>
/// Holds the live queue/voting configuration and broadcasts changes via SignalR.
/// </summary>
public class QueueSettingsService
{
    public int    MaxQueueSize                     { get; private set; }
    public int    MaxSongsPerUser                  { get; private set; }
    public bool   VotingEnabled                    { get; private set; }
    public bool   PresenceCheckEnabled             { get; private set; } = false;
    public int    PresenceCheckWarningSeconds      { get; private set; } = 30;
    public int    PresenceCheckConfirmSeconds      { get; private set; } = 30;
    public bool   SaveDownloads                    { get; private set; } = false;
    public bool   AutoQueueEnabled                 { get; private set; } = false;

    private readonly IHubContext<OverlayHub> _hub;

    public QueueSettingsService(IConfiguration config, IHubContext<OverlayHub> hub)
    {
        _hub = hub;
        var q       = config.GetSection("Queue");
        MaxQueueSize    = q.GetValue("MaxSize",         50);
        MaxSongsPerUser = q.GetValue("MaxSongsPerUser", 10);
        VotingEnabled   = q.GetValue("VotingEnabled",   false);
        PresenceCheckEnabled        = q.GetValue("PresenceCheckEnabled",        false);
        PresenceCheckWarningSeconds = q.GetValue("PresenceCheckWarningSeconds", 30);
        PresenceCheckConfirmSeconds = q.GetValue("PresenceCheckConfirmSeconds", 30);
        SaveDownloads    = q.GetValue("SaveDownloads",    false);
        AutoQueueEnabled = q.GetValue("AutoQueueEnabled", false);
    }

    public async Task UpdateAsync(
        int    maxQueueSize,
        int    maxSongsPerUser,
        bool   votingEnabled,
        bool   presenceCheckEnabled,
        int    presenceCheckWarningSeconds,
        int    presenceCheckConfirmSeconds,
        bool   saveDownloads   = false,
        bool   autoQueueEnabled = false)
    {
        MaxQueueSize                    = maxQueueSize;
        MaxSongsPerUser                 = maxSongsPerUser;
        VotingEnabled                   = votingEnabled;
        PresenceCheckEnabled            = presenceCheckEnabled;
        PresenceCheckWarningSeconds     = Math.Max(5, presenceCheckWarningSeconds);
        PresenceCheckConfirmSeconds     = Math.Max(5, presenceCheckConfirmSeconds);
        SaveDownloads                   = saveDownloads;
        AutoQueueEnabled                = autoQueueEnabled;

        await _hub.Clients.Group($"user:{LocalUser.Id}")
                          .SendAsync("settings:updated", new
                          {
                              maxQueueSize,
                              maxSongsPerUser,
                              votingEnabled,
                              presenceCheckEnabled,
                              presenceCheckWarningSeconds     = PresenceCheckWarningSeconds,
                              presenceCheckConfirmSeconds     = PresenceCheckConfirmSeconds,
                              saveDownloads,
                              autoQueueEnabled,
                          });
    }
}
