using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using MusicBot.Hubs;

namespace MusicBot.Services;

public class TickerMessageService
{
    private readonly ILogger<TickerMessageService> _logger;
    private readonly IHubContext<OverlayHub>       _hub;
    private readonly string                        _filePath;
    private readonly object                        _lock = new();
    private List<TickerMessage>                    _messages = new();

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public TickerMessageService(ILogger<TickerMessageService> logger, IHubContext<OverlayHub> hub, IWebHostEnvironment env, IConfiguration config)
    {
        _logger   = logger;
        _hub      = hub;
        var dataDir = config["DataDirectory"] ?? env.ContentRootPath;
        _filePath = Path.Combine(dataDir, "ticker-messages.json");
        Load();
    }

    public List<TickerMessage> GetAll()
    {
        lock (_lock) return new List<TickerMessage>(_messages);
    }

    public TickerMessage Add(TickerMessage msg)
    {
        msg.Id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            msg.Order = _messages.Count;
            _messages.Add(msg);
            Save();
        }
        _ = BroadcastAsync();
        return msg;
    }

    public bool Update(string id, TickerMessage updated)
    {
        lock (_lock)
        {
            var idx = _messages.FindIndex(m => m.Id == id);
            if (idx < 0) return false;
            updated.Id = id;
            _messages[idx] = updated;
            Save();
        }
        _ = BroadcastAsync();
        return true;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var idx = _messages.FindIndex(m => m.Id == id);
            if (idx < 0) return false;
            _messages.RemoveAt(idx);
            // Re-assign order
            for (int i = 0; i < _messages.Count; i++) _messages[i].Order = i;
            Save();
        }
        _ = BroadcastAsync();
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            _messages = JsonSerializer.Deserialize<List<TickerMessage>>(json) ?? new();
            _logger.LogInformation("TickerMessages: loaded {Count} messages", _messages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TickerMessages: failed to load, starting empty");
            _messages = new();
        }
    }

    private void Save()
    {
        try { File.WriteAllText(_filePath, JsonSerializer.Serialize(_messages, _json)); }
        catch (Exception ex) { _logger.LogWarning(ex, "TickerMessages: failed to save"); }
    }

    private Task BroadcastAsync()
    {
        List<TickerMessage> msgs;
        lock (_lock) msgs = new List<TickerMessage>(_messages);
        return _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("ticker:updated", msgs);
    }
}
