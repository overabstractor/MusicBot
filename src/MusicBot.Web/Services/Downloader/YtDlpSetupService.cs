namespace MusicBot.Services.Downloader;

/// <summary>
/// Ensures yt-dlp is available on startup, downloading it automatically if missing.
/// </summary>
public class YtDlpSetupService : BackgroundService
{
    private readonly YtDlpDownloaderService _downloader;
    private readonly ILogger<YtDlpSetupService> _logger;

    public YtDlpSetupService(YtDlpDownloaderService downloader, ILogger<YtDlpSetupService> logger)
    {
        _downloader = downloader;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _downloader.EnsureYtDlpAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado durante la configuración de yt-dlp");
        }
    }
}
