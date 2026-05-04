using MusicBot.Services.Platforms;

namespace MusicBot.Services;

public class TickerChatService(
    TickerMessageService ticker,
    ChatResponseService chat,
    ChatActivityTracker activity,
    ILogger<TickerChatService> logger) : BackgroundService
{
    // Per-message tracking: last send time and global chat-message count at that moment.
    private readonly Dictionary<string, DateTimeOffset> _lastSent       = new();
    private readonly Dictionary<string, long>           _countAtLastSend = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                var now          = DateTimeOffset.UtcNow;
                var currentCount = activity.MessageCount;

                foreach (var msg in ticker.GetAll().Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Text)))
                {
                    var last      = _lastSent.GetValueOrDefault(msg.Id, DateTimeOffset.MinValue);
                    var countBase = _countAtLastSend.GetValueOrDefault(msg.Id, 0L);

                    if ((now - last).TotalMinutes < msg.IntervalMinutes) continue;

                    if (msg.MinChatMessages > 0 && currentCount - countBase < msg.MinChatMessages)
                    {
                        logger.LogDebug("Ticker [{Id}]: skipping — only {Got}/{Need} chat messages since last send",
                            msg.Id, currentCount - countBase, msg.MinChatMessages);
                        continue;
                    }

                    await chat.SendBroadcastAsync(msg.Text, msg.Platforms, ct);

                    _lastSent[msg.Id]        = now;
                    _countAtLastSend[msg.Id] = currentCount;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TickerChatService: unexpected error");
            }
        }
    }
}
