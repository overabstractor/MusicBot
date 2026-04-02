using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Hubs;

namespace MusicBot.Services;

public class SignalRBroadcastService : IHostedService
{
    private readonly UserContextManager _userContext;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly ConcurrentDictionary<Guid, SubscriptionSet> _subscriptions = new();

    public SignalRBroadcastService(UserContextManager userContext, IHubContext<OverlayHub> hub)
    {
        _userContext = userContext;
        _hub = hub;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userContext.OnUserCreated += SubscribeToUser;
        _userContext.OnUserRemoved += UnsubscribeFromUser;

        // Subscribe to any already-active users
        foreach (var (userId, services) in _userContext.GetAllActive())
            SubscribeToUser(userId, services);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userContext.OnUserCreated -= SubscribeToUser;
        _userContext.OnUserRemoved -= UnsubscribeFromUser;

        foreach (var userId in _subscriptions.Keys.ToList())
            UnsubscribeFromUser(userId);

        return Task.CompletedTask;
    }

    private void SubscribeToUser(Guid userId, UserServices services)
    {
        if (_subscriptions.ContainsKey(userId)) return;

        var group = $"user:{userId}";
        var queue = services.Queue;

        Action<QueueState> onQueueUpdated = state =>
            _hub.Clients.Group(group).SendAsync("queue:updated", state);

        Action<NowPlayingState> onNowPlaying = state =>
            _hub.Clients.Group(group).SendAsync("nowplaying:updated", state);

        Action<QueueItem> onSongAdded = item =>
            _hub.Clients.Group(group).SendAsync("queue:song-added", item);

        Action<QueueItem> onSongRemoved = item =>
            _hub.Clients.Group(group).SendAsync("queue:song-removed", item);

        queue.OnQueueUpdated += onQueueUpdated;
        queue.OnNowPlayingUpdated += onNowPlaying;
        queue.OnSongAdded += onSongAdded;
        queue.OnSongRemoved += onSongRemoved;

        _subscriptions[userId] = new SubscriptionSet(queue, onQueueUpdated, onNowPlaying, onSongAdded, onSongRemoved);
    }

    private void UnsubscribeFromUser(Guid userId)
    {
        if (!_subscriptions.TryRemove(userId, out var sub)) return;

        sub.Queue.OnQueueUpdated -= sub.OnQueueUpdated;
        sub.Queue.OnNowPlayingUpdated -= sub.OnNowPlaying;
        sub.Queue.OnSongAdded -= sub.OnSongAdded;
        sub.Queue.OnSongRemoved -= sub.OnSongRemoved;
    }

    private record SubscriptionSet(
        IQueueService Queue,
        Action<QueueState> OnQueueUpdated,
        Action<NowPlayingState> OnNowPlaying,
        Action<QueueItem> OnSongAdded,
        Action<QueueItem> OnSongRemoved);
}
