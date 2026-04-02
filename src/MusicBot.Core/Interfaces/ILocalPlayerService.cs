namespace MusicBot.Core.Interfaces;

public interface ILocalPlayerService : IDisposable
{
    bool IsPlaying { get; }
    int PositionMs { get; }
    int DurationMs { get; }
    string? CurrentFilePath { get; }
    float Volume { get; }

    event Action<LocalPlayerState>? OnStateChanged;
    event Action? OnTrackEnded;

    Task PlayAsync(string filePath);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
    void SetVolume(float volume);   // 0.0 – 1.0
    void SeekTo(int positionMs);

    /// <summary>WASAPI device ID, or null for the system default.</summary>
    string? DeviceId { get; }

    /// <summary>
    /// Switch output device. Pass null to revert to the system default.
    /// If a track is currently playing it restarts on the new device
    /// from the same position.
    /// </summary>
    Task SetDeviceAsync(string? deviceId);
}

public class LocalPlayerState
{
    public bool IsPlaying { get; set; }
    public int PositionMs { get; set; }
    public int DurationMs { get; set; }
    public string? FilePath { get; set; }
}
