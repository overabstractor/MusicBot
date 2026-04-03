using MusicBot.Core.Interfaces;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MusicBot.Services.Player;

/// <summary>
/// Plays local audio files using NAudio (Windows MediaFoundation / WASAPI).
/// Supports selecting any active WASAPI render endpoint as the output device.
/// </summary>
public class LocalPlayerService : ILocalPlayerService
{
    private readonly ILogger<LocalPlayerService> _logger;

    private IWavePlayer?           _output;
    private MediaFoundationReader? _reader;
    private System.Threading.Timer? _timer;
    private bool   _stoppedManually;
    private string? _deviceId;
    private MMDevice? _activeDevice;

    public event Action<LocalPlayerState>? OnStateChanged;
    public event Action? OnTrackEnded;

    public bool    IsPlaying      => _output?.PlaybackState == NAudio.Wave.PlaybackState.Playing;
    public int     PositionMs     => (int)(_reader?.CurrentTime.TotalMilliseconds ?? 0);
    public int     DurationMs     => (int)(_reader?.TotalTime.TotalMilliseconds   ?? 0);
    public string? CurrentFilePath { get; private set; }
    public float   Volume         { get; private set; } = 1.0f;
    public string? DeviceId       => _deviceId;

    public LocalPlayerService(ILogger<LocalPlayerService> logger)
    {
        _logger = logger;
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    public Task PlayAsync(string filePath)
    {
        _stoppedManually = true;
        DisposePlayback();
        _stoppedManually = false;

        try
        {
            CurrentFilePath = filePath;
            _reader         = new MediaFoundationReader(filePath);
            _output         = CreateOutput(out _activeDevice);
            _output.Init(_reader);
            if (_output is WasapiOut w) w.Volume = Volume;
            _output.PlaybackStopped += HandlePlaybackStopped;
            _output.Play();
            StartTimer();

            // Apply fixed GroupingParam so audio routers (Mixline, etc.) always
            // identify MusicBot as the same app across restarts and debug sessions.
            var device = _activeDevice;
            if (device != null)
                _ = Task.Run(() => AudioSessionHelper.ApplyGroupingParam(device));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start playback for {Path}", filePath);
            DisposePlayback();
            throw; // propagate so callers (PlaybackSyncService) can handle and notify the user
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        _output?.Pause();
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        _output?.Play();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _stoppedManually = true;
        DisposePlayback();
        CurrentFilePath = null;
        return Task.CompletedTask;
    }

    public void SetVolume(float volume)
    {
        Volume = Math.Clamp(volume, 0f, 1f);
        if (_output is WasapiOut wasapi)
            wasapi.Volume = Volume;
    }

    public void SeekTo(int positionMs)
    {
        if (_reader == null) return;
        _reader.CurrentTime = TimeSpan.FromMilliseconds(positionMs);
    }

    // ── Device selection ──────────────────────────────────────────────────────

    public async Task SetDeviceAsync(string? deviceId)
    {
        _deviceId = deviceId;

        // If a track is already playing, restart it on the new device
        // from the current position so playback is seamless.
        if (CurrentFilePath != null)
        {
            var resumeAt = PositionMs;
            await PlayAsync(CurrentFilePath);
            if (resumeAt > 0) SeekTo(resumeAt);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a WasapiOut for the selected device (or default) and returns the
    /// MMDevice so the caller can apply session metadata (e.g. GroupingParam).
    /// </summary>
    private WasapiOut CreateOutput(out MMDevice outDevice)
    {
        MMDevice? device = null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            device = _deviceId != null
                ? enumerator.GetDevice(_deviceId)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            outDevice = device;
            return new WasapiOut(device, AudioClientShareMode.Shared, true, 400);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open audio device {Id}, falling back to default", _deviceId);
            _deviceId = null;
            device?.Dispose();
            var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            outDevice = device;
            return new WasapiOut(device, AudioClientShareMode.Shared, true, 400);
        }
    }

    private void HandlePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        StopTimer();

        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Playback error");
            Task.Run(() => OnTrackEnded?.Invoke());
            return;
        }

        if (!_stoppedManually)
            Task.Run(() => OnTrackEnded?.Invoke());
    }

    private void StartTimer()
    {
        _timer = new System.Threading.Timer(_ =>
        {
            try
            {
                OnStateChanged?.Invoke(new LocalPlayerState
                {
                    IsPlaying  = IsPlaying,
                    PositionMs = PositionMs,
                    DurationMs = DurationMs,
                    FilePath   = CurrentFilePath,
                });
            }
            catch { }
        }, null, 500, 500);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void DisposePlayback()
    {
        StopTimer();
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
        _activeDevice?.Dispose();
        _activeDevice = null;
    }

    public void Dispose() => DisposePlayback();
}
