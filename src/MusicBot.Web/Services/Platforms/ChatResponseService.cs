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
