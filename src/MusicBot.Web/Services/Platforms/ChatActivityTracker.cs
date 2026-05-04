namespace MusicBot.Services.Platforms;

/// <summary>
/// Counts viewer chat messages across all connected platforms.
/// Messages from the streamer and bot accounts are excluded so they don't
/// artificially satisfy the minimum-messages threshold on ticker timers.
/// </summary>
public class ChatActivityTracker
{
    private long _count;
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public long MessageCount => Interlocked.Read(ref _count);

    /// <summary>Register or unregister a username to be excluded from the count.</summary>
    public void SetIgnored(string username, bool ignore)
    {
        lock (_ignored)
        {
            if (ignore) _ignored.Add(username);
            else        _ignored.Remove(username);
        }
    }

    public void RecordMessage(string username)
    {
        bool skip;
        lock (_ignored) skip = _ignored.Contains(username);
        if (!skip) Interlocked.Increment(ref _count);
    }
}
