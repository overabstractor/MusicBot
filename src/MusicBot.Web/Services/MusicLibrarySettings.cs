namespace MusicBot.Services;

public class MusicLibrarySettings
{
    /// <summary>Directory where downloaded audio files are stored. Relative to working directory or absolute.</summary>
    public string LibraryPath { get; set; } = "music-library";
    /// <summary>Path to the yt-dlp executable. Must be in PATH or provide full path.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";
    /// <summary>Path to the ffmpeg executable. Must be in PATH or provide full path.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
    /// <summary>
    /// When true, yt-dlp downloads the native M4A stream from YouTube without invoking ffmpeg.
    /// NAudio/MediaFoundationReader plays M4A natively on Windows 10/11.
    /// Set to false to convert to MP3 via ffmpeg instead.
    /// </summary>
    public bool UseNativeAudioFormat { get; set; } = true;
}
