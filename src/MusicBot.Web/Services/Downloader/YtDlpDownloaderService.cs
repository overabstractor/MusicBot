using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace MusicBot.Services.Downloader;

/// <summary>
/// Downloads audio from YouTube via yt-dlp (using YoutubeDLSharp).
/// Searches for the best matching video by title/artist/duration,
/// downloads as MP3, and persists to the local library.
/// In-progress downloads are tracked so duplicate requests await the same task.
/// </summary>
public class YtDlpDownloaderService
{
    private readonly ILocalLibraryService _library;
    private readonly MusicLibrarySettings _settings;
    private readonly QueueSettingsService _queueSettings;
    private readonly ILogger<YtDlpDownloaderService> _logger;
    private readonly YoutubeDL _ytdl;

    // trackId → running download task
    private readonly ConcurrentDictionary<string, Task<string>> _inProgress = new();

    /// <summary>Fires with (spotifyUri, percentComplete 0–100) during downloads.</summary>
    public event Action<string, int>? ProgressChanged;

    /// <summary>Fires with (spotifyUri, title, artist) when a real download begins (not served from cache).</summary>
    public event Action<string, string, string>? DownloadStarted;

    /// <summary>Fires with (spotifyUri) when a download completes successfully.</summary>
    public event Action<string>? DownloadCompleted;

    /// <summary>Fires with (spotifyUri, errorMessage) when a download fails.</summary>
    public event Action<string, string>? DownloadFailed;

    public YtDlpDownloaderService(
        ILocalLibraryService library,
        IOptions<MusicLibrarySettings> settings,
        QueueSettingsService queueSettings,
        ILogger<YtDlpDownloaderService> logger)
    {
        _library       = library;
        _settings      = settings.Value;
        _queueSettings = queueSettings;
        _logger        = logger;

        Directory.CreateDirectory(_settings.LibraryPath);

        _ytdl = new YoutubeDL
        {
            YoutubeDLPath  = ResolveYtDlpPath(_settings.YtDlpPath),
            FFmpegPath     = _settings.FfmpegPath ?? "ffmpeg",
            OutputFolder   = _settings.LibraryPath,
            OverwriteFiles = false,
        };
    }

    /// <summary>
    /// Ensures yt-dlp is available next to the executable, downloading it if needed.
    /// Called by YtDlpSetupService at startup.
    /// </summary>
    public async Task EnsureYtDlpAsync()
    {
        var path = _ytdl.YoutubeDLPath;

        if (File.Exists(path))
        {
            _logger.LogInformation("yt-dlp disponible en '{Path}'", path);
            return;
        }

        if (IsOnPath(_settings.YtDlpPath))
        {
            _logger.LogInformation("yt-dlp encontrado en PATH");
            return;
        }

        _logger.LogInformation("yt-dlp no encontrado — descargando automáticamente en '{Dir}'...",
            AppContext.BaseDirectory);

        try
        {
            await YoutubeDLSharp.Utils.DownloadYtDlp(AppContext.BaseDirectory);
            _ytdl.YoutubeDLPath = path; // path already points to BaseDirectory/yt-dlp.exe
            _logger.LogInformation("yt-dlp descargado correctamente en '{Path}'", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo descargar yt-dlp automáticamente. " +
                "Instálalo manualmente con: winget install yt-dlp.yt-dlp");
        }
    }

    /// <summary>
    /// Resolves the effective yt-dlp path:
    /// 1. Next to the executable (preferred — works after auto-download)
    /// 2. Configured path if absolute and exists
    /// 3. Configured name if on PATH
    /// 4. Falls back to BaseDirectory/yt-dlp.exe (download target)
    /// </summary>
    private static string ResolveYtDlpPath(string configured)
    {
        var appDirExe = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(appDirExe)) return appDirExe;
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        if (IsOnPath(configured)) return configured;
        return appDirExe; // download target
    }

    private static bool IsOnPath(string name)
    {
        var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        return envPath.Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, name)) ||
                        File.Exists(Path.Combine(dir, name + ".exe")));
    }

    /// <summary>Fire-and-forget: starts a background download if not already running.</summary>
    public void StartDownload(Song song)
    {
        _inProgress.TryAdd(song.SpotifyUri, DownloadCoreAsync(song));
    }

    /// <summary>Returns the local file path, waiting for an in-progress download or starting one.</summary>
    public Task<string> GetOrStartDownloadAsync(Song song)
    {
        return _inProgress.GetOrAdd(song.SpotifyUri, _ => DownloadCoreAsync(song));
    }

    /// <summary>
    /// Removes a completed (possibly corrupt) download task from the cache so that the
    /// next call to GetOrStartDownloadAsync will re-download the file from scratch.
    /// </summary>
    public void InvalidateCachedDownload(string spotifyUri) =>
        _inProgress.TryRemove(spotifyUri, out _);

    // ── YouTube metadata search ──────────────────────────────────────────────

    /// <summary>Fetch metadata for a single YouTube video by its ID.</summary>
    public async Task<Song?> FetchVideoMetadataAsync(string videoId)
    {
        RunResult<VideoData> result;
        try
        {
            result = await _ytdl.RunVideoDataFetch(
                $"https://www.youtube.com/watch?v={videoId}",
                ct: default,
                flat: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("yt-dlp single-video fetch failed for {Id}: {Error}", videoId, ex.Message);
            return null;
        }

        if (!result.Success || result.Data == null) return null;

        var data = result.Data;
        var (title, artist) = SplitVideoTitle(data.Title ?? "", data.Channel ?? data.Uploader ?? "");
        return new Song
        {
            SpotifyUri = $"youtube:{videoId}",
            Title      = title,
            Artist     = artist,
            CoverUrl   = data.Thumbnail ?? "",
            DurationMs = data.Duration.HasValue ? (int)(data.Duration.Value * 1000) : 0
        };
    }

    /// <summary>
    /// Fetch all tracks from a YouTube playlist URL.
    /// Uses <c>--print</c> (tab-separated, one line per entry) — the most reliable method
    /// across yt-dlp versions.  Thumbnail is constructed from the video ID.
    /// </summary>
    public async Task<List<Song>> ImportPlaylistAsync(string playlistUrl, int maxTracks = 200)
    {
        _logger.LogInformation("Importing playlist: {Url} (max {Max})", playlistUrl, maxTracks);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var psi = new ProcessStartInfo
        {
            FileName               = _settings.YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        // --print outputs one line per entry (id<TAB>title<TAB>channel<TAB>duration).
        // More reliable than -j/-J for paginated YouTube playlists.
        // Thumbnail is always reconstructable from the video ID.
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(id)s\t%(title)s\t%(channel)s\t%(duration)s");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--playlist-items");
        psi.ArgumentList.Add($"1-{maxTracks}");
        psi.ArgumentList.Add(playlistUrl);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var songs = new List<Song>();
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cts.Token)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts  = line.Split('\t');
                var id     = parts.Length > 0 ? parts[0].Trim() : null;
                var title  = parts.Length > 1 ? parts[1].Trim() : "";
                var chan    = parts.Length > 2 ? parts[2].Trim() : "";
                var durStr = parts.Length > 3 ? parts[3].Trim() : "0";

                if (string.IsNullOrEmpty(id) || id == "NA") continue;

                double.TryParse(durStr is "NA" or "" ? "0" : durStr,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double dur);

                var (songTitle, artist) = SplitVideoTitle(title, chan == "NA" ? "" : chan);
                songs.Add(new Song
                {
                    SpotifyUri = $"youtube:{id}",
                    Title      = songTitle,
                    Artist     = artist,
                    // YouTube always serves thumbnails at this URL — no NA risk
                    CoverUrl   = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
                    DurationMs = (int)(dur * 1000),
                });

                if (songs.Count >= maxTracks) break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Playlist import timed out after 5 min (got {N} tracks so far)", songs.Count);
        }

        try { await process.WaitForExitAsync(); } catch { }
        _logger.LogInformation("Playlist import complete: {N} tracks from {Url}", songs.Count, playlistUrl);
        return songs;
    }

    /// <summary>Search YouTube and return matching songs.</summary>
    public async Task<List<Song>> SearchAsync(string query, int limit = 5)
    {
        using var doc = await RunYtDlpJsonAsync(
            ["--flat-playlist", "-J", "--no-warnings", $"ytsearch{limit}:{query}"],
            TimeSpan.FromSeconds(15));

        if (doc == null || !doc.RootElement.TryGetProperty("entries", out var entries))
            return [];

        return ParseEntries(entries, limit);
    }

    /// <summary>
    /// Finds the single best YouTube match for a free-text query.
    /// Appends "official audio" for non-remix queries.
    /// Filters out remixes/covers/karaoke unless the query explicitly asks for them.
    /// Returns null if nothing is found.
    /// </summary>
    public async Task<Song?> SearchBestMatchAsync(string query)
    {
        var queryLower = query.ToLowerInvariant();
        bool isSpecialQuery = RemixCoverKeywords.Any(k => queryLower.Contains(k));

        var searchQuery = isSpecialQuery ? query : $"{query} official audio";

        var results = await SearchAsync(searchQuery, 5);
        if (results.Count == 0) return null;

        if (!isSpecialQuery)
        {
            // Prefer a result whose title/artist does NOT contain remix/cover keywords
            var original = results.FirstOrDefault(s =>
                !RemixCoverKeywords.Any(k =>
                    s.Title.ToLowerInvariant().Contains(k) ||
                    s.Artist.ToLowerInvariant().Contains(k)));
            if (original != null) return original;
        }

        return results[0];
    }

    // ── Direct yt-dlp process helpers ────────────────────────────────────────

    /// <summary>
    /// Runs yt-dlp with the given arguments and returns the parsed JSON output.
    /// Returns null on timeout, error, or parse failure.
    /// </summary>
    private async Task<JsonDocument?> RunYtDlpJsonAsync(IEnumerable<string> args, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        var psi = new ProcessStartInfo
        {
            FileName               = _settings.YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout and stderr concurrently to avoid deadlock on full pipe buffers
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
            var output = await stdoutTask;
            if (string.IsNullOrWhiteSpace(output))
            {
                var err = await stderrTask;
                _logger.LogWarning("yt-dlp produced no output. stderr: {Err}", err.Length > 500 ? err[..500] : err);
                return null;
            }
            return JsonDocument.Parse(output);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("yt-dlp process timed out after {Timeout}", timeout);
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp JSON parse/run failed");
            return null;
        }
    }

    private List<Song> ParseEntries(JsonElement entries, int maxCount)
    {
        var songs = new List<Song>();
        foreach (var entry in entries.EnumerateArray().Take(maxCount))
        {
            var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;

            var title   = entry.TryGetProperty("title",    out var tEl)  ? tEl.GetString()  ?? "" : "";
            var channel = entry.TryGetProperty("channel",  out var chEl) ? chEl.GetString() ?? "" :
                          entry.TryGetProperty("uploader", out var upEl) ? upEl.GetString() ?? "" : "";
            var thumb   = entry.TryGetProperty("thumbnail", out var thEl) ? thEl.GetString() ?? "" : "";
            var dur     = entry.TryGetProperty("duration",  out var dEl) && dEl.ValueKind == JsonValueKind.Number
                          ? dEl.GetDouble() : 0;

            var (songTitle, artist) = SplitVideoTitle(title, channel);
            songs.Add(new Song
            {
                SpotifyUri = $"youtube:{id}",
                Title      = songTitle,
                Artist     = artist,
                CoverUrl   = thumb,
                DurationMs = (int)(dur * 1000),
            });
        }
        return songs;
    }

    private static (string title, string artist) SplitVideoTitle(string videoTitle, string channelName)
    {
        var idx = videoTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0)
        {
            var artist = videoTitle[..idx].Trim();
            var title  = videoTitle[(idx + 3)..].Trim();
            title = System.Text.RegularExpressions.Regex.Replace(
                title, @"\s*[\(\[][^\)\]]*[\)\]]$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return (title, artist);
        }
        return (videoTitle.Trim(), channelName.Trim());
    }

    // ── Core download logic ──────────────────────────────────────────────────

    private async Task<string> DownloadCoreAsync(Song song)
    {
        var safeId     = song.SpotifyUri.Replace(":", "_");
        var ext        = _settings.UseNativeAudioFormat ? "m4a" : "mp3";
        var outputPath = Path.GetFullPath(Path.Combine(_settings.LibraryPath, $"{safeId}.{ext}"));

        // Already on disk — validate it has reasonable content (> 100 KB),
        // otherwise treat it as a corrupt stub and delete it before re-downloading.
        if (File.Exists(outputPath))
        {
            if (new FileInfo(outputPath).Length > 100_000)
            {
                await EnsureSavedToLibraryAsync(song, outputPath);
                return outputPath;
            }
            _logger.LogWarning("Cached file for \"{Title}\" is too small (corrupt stub) — deleting and re-downloading", song.Title);
            try { File.Delete(outputPath); } catch { }
        }

        _logger.LogInformation("Downloading \"{Title}\" by {Artist}...", song.Title, song.Artist);
        DownloadStarted?.Invoke(song.SpotifyUri, song.Title, song.Artist);

        try
        {
            string? videoId;
            if (song.SpotifyUri.StartsWith("youtube:"))
                videoId = song.SpotifyUri["youtube:".Length..];
            else
                videoId = await FindBestYouTubeMatchAsync(song);

            if (videoId == null)
                throw new InvalidOperationException($"No YouTube match found for \"{song.Title}\" by {song.Artist}");

            var url = $"https://www.youtube.com/watch?v={videoId}";

            int lastPct = -1;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.State != DownloadState.Downloading) return;
                var pct = Math.Min(99, (int)(p.Progress * 100));
                if (pct == lastPct) return;
                lastPct = pct;
                _logger.LogInformation("Download progress [{TrackId}]: {Pct}%", song.SpotifyUri, pct);
                ProgressChanged?.Invoke(song.SpotifyUri, pct);
            });

            var overrideOptions = new OptionSet
            {
                Output     = outputPath,
                NoPlaylist = true,
            };

            var audioFormat = _settings.UseNativeAudioFormat
                ? AudioConversionFormat.M4a   // no ffmpeg needed; Windows MF plays M4A natively
                : AudioConversionFormat.Mp3;  // requires ffmpeg

            var result = await _ytdl.RunAudioDownload(
                url,
                audioFormat,
                progress: progress,
                overrideOptions: overrideOptions);

            if (!result.Success)
                throw new InvalidOperationException(
                    $"yt-dlp failed for \"{song.Title}\": {string.Join("; ", result.ErrorOutput)}");

            var filePath = File.Exists(outputPath) ? outputPath : result.Data ?? outputPath;

            if (!File.Exists(filePath))
                throw new InvalidOperationException($"yt-dlp finished but output file not found: {filePath}");

            // Correct DurationMs from the actual downloaded file (metadata duration can differ)
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var actualMs = (int)tagFile.Properties.Duration.TotalMilliseconds;
                if (actualMs > 0 && Math.Abs(actualMs - song.DurationMs) > 1000)
                {
                    _logger.LogInformation(
                        "Correcting duration for \"{Title}\": {Old}ms → {New}ms",
                        song.Title, song.DurationMs, actualMs);
                    song.DurationMs = actualMs;
                }
            }
            catch { /* TagLib optional; keep original estimate */ }

            await EnsureSavedToLibraryAsync(song, filePath);
            if (_queueSettings.SaveDownloads)
                await WriteId3TagsAsync(song, filePath);
            _logger.LogInformation("Downloaded \"{Title}\" → {Path}", song.Title, filePath);
            DownloadCompleted?.Invoke(song.SpotifyUri);
            return filePath;
        }
        catch (Exception ex)
        {
            // Remove the faulted task so the song can be re-downloaded on the next attempt
            _inProgress.TryRemove(song.SpotifyUri, out _);
            _logger.LogError(ex, "Download failed for \"{Title}\" ({SpotifyUri})", song.Title, song.SpotifyUri);
            DownloadFailed?.Invoke(song.SpotifyUri, ex.Message);
            throw;
        }
    }

    private async Task EnsureSavedToLibraryAsync(Song song, string filePath)
    {
        var existing = await _library.FindByTrackIdAsync(song.SpotifyUri);
        if (existing != null)
        {
            // Persist corrected duration if it changed after reading the actual file
            if (song.DurationMs > 0 && existing.DurationMs != song.DurationMs)
                await _library.UpdateDurationAsync(song.SpotifyUri, song.DurationMs);
            // Persist new path if the file was re-downloaded to a different location
            // (e.g. after a Velopack update wiped the old current\ directory)
            if (!string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                await _library.UpdateFilePathAsync(song.SpotifyUri, filePath);
            return;
        }

        await _library.SaveAsync(new CachedTrack
        {
            TrackId      = song.SpotifyUri,
            Title        = song.Title,
            Artist       = song.Artist,
            CoverUrl     = song.CoverUrl,
            DurationMs   = song.DurationMs,
            FilePath     = filePath,
            DownloadedAt = DateTime.UtcNow
        });
    }

    // ── YouTube search ───────────────────────────────────────────────────────

    private static readonly string[] RemixCoverKeywords =
        ["remix", "cover", "karaoke", "tribute", "mashup", "nightcore", "bootleg"];

    private async Task<string?> FindBestYouTubeMatchAsync(Song song)
    {
        var expectedSec  = song.DurationMs / 1000.0;
        var toleranceSec = Math.Max(expectedSec * 0.20, 10);

        var metaLower   = $"{song.Title} {song.Artist}".ToLowerInvariant();
        var isRemixReq  = RemixCoverKeywords.Any(k => metaLower.Contains(k));
        var searchQuery = isRemixReq
            ? $"{song.Title} {song.Artist}"
            : $"{song.Title} {song.Artist} official audio";

        RunResult<VideoData> result;
        try
        {
            result = await _ytdl.RunVideoDataFetch(
                $"ytsearch10:{searchQuery}",
                flat: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("yt-dlp search failed: {Error}", ex.Message);
            return null;
        }

        if (!result.Success || result.Data?.Entries == null)
        {
            _logger.LogWarning("No YouTube results for \"{Title}\"", song.Title);
            return null;
        }

        var candidates = ScoreCandidates(result.Data.Entries, song, expectedSec, toleranceSec, skipFilter: false);

        if (candidates.Count == 0)
        {
            _logger.LogWarning("No filtered match for \"{Title}\", retrying without filter.", song.Title);
            candidates = ScoreCandidates(result.Data.Entries, song, expectedSec, toleranceSec, skipFilter: true);
        }

        if (candidates.Count == 0)
        {
            _logger.LogWarning("No YouTube match found for \"{Title}\"", song.Title);
            return null;
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.DurationDiff).First().Id;
    }

    private async Task WriteId3TagsAsync(Song song, string filePath)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            file.Tag.Title      = song.Title;
            file.Tag.Performers = new[] { song.Artist };
            file.Tag.Comment    = song.SpotifyUri;

            if (!string.IsNullOrEmpty(song.CoverUrl))
            {
                try
                {
                    using var http   = new HttpClient();
                    var imageBytes   = await http.GetByteArrayAsync(song.CoverUrl);
                    var pic          = new TagLib.Picture(new TagLib.ByteVector(imageBytes))
                    {
                        Type        = TagLib.PictureType.FrontCover,
                        MimeType    = "image/jpeg",
                    };
                    file.Tag.Pictures = new TagLib.IPicture[] { pic };
                }
                catch { /* skip cover if download fails */ }
            }

            file.Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not write ID3 tags to {Path}: {Error}", filePath, ex.Message);
        }
    }

    private record YtCandidate(string Id, double Score, double DurationDiff);

    private List<YtCandidate> ScoreCandidates(
        VideoData[] entries, Song song,
        double expectedSec, double toleranceSec,
        bool skipFilter)
    {
        var titleLower  = song.Title.ToLowerInvariant();
        var artistLower = song.Artist.ToLowerInvariant();
        var results     = new List<YtCandidate>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.ID)) continue;
            if (!entry.Duration.HasValue || entry.Duration.Value <= 0) continue;

            var diff = Math.Abs(entry.Duration.Value - expectedSec);
            if (diff > toleranceSec) continue;

            var titleL   = (entry.Title   ?? "").ToLowerInvariant();
            var channelL = (entry.Channel ?? entry.Uploader ?? "").ToLowerInvariant();

            if (!skipFilter && RemixCoverKeywords.Any(k => titleL.Contains(k)))
                continue;

            bool titleMatch  = titleL.Contains(titleLower)  || (titleLower.Length > 4 && titleLower.Contains(titleL));
            bool artistMatch = titleL.Contains(artistLower) || channelL.Contains(artistLower);

            // Require at least title or artist match unless we're in last-resort mode
            if (!skipFilter && !titleMatch && !artistMatch)
                continue;

            double score = 0;
            if (titleMatch && artistMatch) score += 4;
            else if (titleMatch)           score += 2;
            else if (artistMatch)          score += 1;

            if (channelL.Contains("vevo"))                                    score += 3;
            if (channelL.Contains("official") || titleL.Contains("official")) score += 2;
            if (titleL.Contains("audio"))                                     score += 1;

            results.Add(new YtCandidate(entry.ID, score, diff));
        }
        return results;
    }
}
