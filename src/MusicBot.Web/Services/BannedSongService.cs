using System.IO;
using System.Text.Json;

namespace MusicBot.Services;

public record BannedSongEntry(string Uri, string Title, string Artist, DateTime BannedAt);

/// <summary>
/// Manages the list of banned songs. Persisted to banned-songs.json next to the database.
/// </summary>
public class BannedSongService
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly List<BannedSongEntry> _songs = new();

    internal BannedSongService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public BannedSongService(IWebHostEnvironment env, IConfiguration config)
    {
        var dataDir = config["DataDirectory"] ?? env.ContentRootPath;
        _filePath = Path.Combine(dataDir, "banned-songs.json");
        Load();
    }

    public bool IsBanned(string? uri)
    {
        if (uri == null) return false;
        lock (_lock) return _songs.Any(s => s.Uri == uri);
    }

    public BannedSongEntry? GetEntry(string uri)
    {
        lock (_lock) return _songs.FirstOrDefault(s => s.Uri == uri);
    }

    public void Ban(string uri, string title, string artist)
    {
        lock (_lock)
        {
            if (_songs.Any(s => s.Uri == uri)) return;
            _songs.Add(new BannedSongEntry(uri, title, artist, DateTime.UtcNow));
        }
        Save();
    }

    public bool Unban(string uri)
    {
        bool removed;
        lock (_lock) removed = _songs.RemoveAll(s => s.Uri == uri) > 0;
        if (removed) Save();
        return removed;
    }

    public IReadOnlyList<BannedSongEntry> GetAll()
    {
        lock (_lock) return _songs.ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<BannedSongEntry>>(json);
            if (list != null) { lock (_lock) { _songs.Clear(); _songs.AddRange(list); } }
        }
        catch { /* ignore corrupt file */ }
    }

    private void Save()
    {
        try
        {
            List<BannedSongEntry> snapshot;
            lock (_lock) snapshot = _songs.ToList();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore write errors */ }
    }
}
