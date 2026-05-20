using MusicBot.Core.Interfaces;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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
    private VolumeSampleProvider?  _volumeProvider;
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _rampCts;
    private bool   _stoppedManually;
    private string? _deviceId;
    private MMDevice? _activeDevice;

    /// <summary>
    /// Duration of the volume ramp applied on each SetVolume call. ~120ms is
    /// the standard short-fade duration used in Web Audio API (setTargetAtTime
    /// time-constants) and AVPlayer (setVolume:fadeDuration:) — long enough to
    /// avoid audible "clicks"/zipper noise on big jumps (mute → 100%, wheel
    /// scroll), short enough to feel instantaneous.
    /// </summary>
    private const int VolumeRampMs = 120;
    private const int VolumeRampSteps = 12;

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
            // Apply volume as software gain BEFORE WASAPI so neither the Windows session
            // volume nor the master endpoint volume is ever touched by us.
            _volumeProvider = new VolumeSampleProvider(_reader.ToSampleProvider()) { Volume = Volume };
            _output         = CreateOutput(out _activeDevice);
            _output.Init(_volumeProvider);  // WASAPI session is created here (IAudioClient.Initialize)

            // Pin GroupingParam + DisplayName immediately after session creation,
            // before Play() so audio routers (Logitech G HUB, Mixline, etc.) see
            // the correct identity on first notification and never create a duplicate entry.
            if (_activeDevice != null)
                AudioSessionHelper.ApplySessionMetadata(_activeDevice);

            _output.PlaybackStopped += HandlePlaybackStopped;
            _output.Play();
            StartTimer();
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
        var target = Math.Clamp(volume, 0f, 1f);
        Volume = target;

        var provider = _volumeProvider;
        if (provider == null) return;

        // Cancel any in-flight ramp; a new SetVolume supersedes it.
        var oldCts = Interlocked.Exchange(ref _rampCts, new CancellationTokenSource());
        oldCts?.Cancel();
        oldCts?.Dispose();

        var from = provider.Volume;
        var delta = target - from;

        // Tiny changes apply instantly — no point in scheduling a Task.
        if (MathF.Abs(delta) < 0.01f)
        {
            provider.Volume = target;
            return;
        }

        var cts = _rampCts!;
        var ct  = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                const int stepMs = VolumeRampMs / VolumeRampSteps;
                for (int i = 1; i <= VolumeRampSteps; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    var t = i / (float)VolumeRampSteps;
                    provider.Volume = from + delta * t;
                    await Task.Delay(stepMs, ct);
                }
                provider.Volume = target;
            }
            catch (OperationCanceledException) { /* superseded */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Volume ramp failed");
            }
        }, ct);
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
            // Release the file handle before notifying listeners so callers can delete the file.
            Task.Run(() => { ReleaseReader(); OnTrackEnded?.Invoke(); });
            return;
        }

        if (!_stoppedManually)
            Task.Run(() => { ReleaseReader(); OnTrackEnded?.Invoke(); });
    }

    private void ReleaseReader()
    {
        var r = Interlocked.Exchange(ref _reader, null);
        r?.Dispose();
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
        var rampCts = Interlocked.Exchange(ref _rampCts, null);
        rampCts?.Cancel();
        rampCts?.Dispose();
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _output = null;
        _volumeProvider = null;
        _reader?.Dispose();
        _reader = null;
        _activeDevice?.Dispose();
        _activeDevice = null;
    }

    public void Dispose() => DisposePlayback();
}
