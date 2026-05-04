namespace MusicBot.Services.Platforms;

/// <summary>
/// Unified chat response service. Sends messages to the correct platform
/// (TikTok, Twitch, Kick) without depending on any external middleware.
/// Uses a registration pattern to avoid circular DI dependencies —
/// platform services call <see cref="RegisterSender"/> at connect time.
/// </summary>
public class ChatResponseService
{
    private readonly ILogger<ChatResponseService> _logger;
    private readonly Dictionary<string, Func<string, Task<bool>>> _senders = new();

    public ChatResponseService(ILogger<ChatResponseService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Register (or replace) a send function for a platform.
    /// Call with <c>null</c> to unregister when disconnecting.
    /// </summary>
    public void RegisterSender(string platform, Func<string, Task<bool>>? sender)
    {
        lock (_senders)
        {
            if (sender != null)
                _senders[platform] = sender;
            else
                _senders.Remove(platform);
        }
    }

    /// <summary>
    /// Sends a message to connected platforms without any user mention prefix.
    /// Used for ticker/broadcast messages that are not responses to a user command.
    /// </summary>
    /// <param name="platforms">
    /// Platforms to target. Null means all connected platforms; empty array means none.
    /// </param>
    public async Task SendBroadcastAsync(string message, string[]? platforms = null, CancellationToken ct = default)
    {
        if (platforms is { Length: 0 })
        {
            _logger.LogDebug("Ticker broadcast: no platforms configured, skipping");
            return;
        }

        List<(string key, Func<string, Task<bool>> sender)> senders;
        lock (_senders)
        {
            var entries = _senders.AsEnumerable();
            if (platforms != null)
                entries = entries.Where(kv => platforms.Contains(kv.Key));
            senders = entries.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        if (senders.Count == 0)
        {
            _logger.LogDebug("Ticker broadcast: no matching platform connections, skipping");
            return;
        }

        await Task.WhenAll(senders.Select(async s =>
        {
            try
            {
                var sent = await s.sender(message);
                if (sent) _logger.LogInformation("Ticker [{Platform}]: {Message}", s.key, message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ticker broadcast error on {Platform}", s.key);
            }
        }));
    }

    /// <summary>
    /// Sends a chat message to the platform that originated the command.
    /// Falls back to logging if the platform doesn't support sending or isn't connected.
    /// </summary>
    public async Task SendChatMessageAsync(string user, string message, string? platform = null, CancellationToken ct = default)
    {
        var fullMessage = $"@{user}: {message}";
        var target = platform?.ToLower() ?? "unknown";

        try
        {
            Func<string, Task<bool>>? sender;
            lock (_senders)
                _senders.TryGetValue(target, out sender);

            var sent = sender != null && await sender(fullMessage);

            if (sent)
                _logger.LogInformation("Chat [{Platform}]: {Message}", target, fullMessage);
            else
                _logger.LogDebug("Chat [{Platform}]: no active connection, message not sent: {Message}", target, fullMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat [{Platform}]: error sending message for {User}", target, user);
        }
    }
}
