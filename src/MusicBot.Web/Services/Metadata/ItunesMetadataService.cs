using System.Text.Json;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;

namespace MusicBot.Services.Metadata;

/// <summary>
/// Searches song metadata using the iTunes Search API.
/// No API key required, no rate limits documented.
/// </summary>
public class ItunesMetadataService : IMetadataService
{
    private readonly HttpClient _http;
    private readonly ILogger<ItunesMetadataService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ItunesMetadataService(IHttpClientFactory httpFactory, ILogger<ItunesMetadataService> logger)
    {
        _http   = httpFactory.CreateClient("itunes");
        _logger = logger;
    }

    private static readonly string[] RemixCoverKeywords =
        ["remix", "cover", "karaoke", "tribute", "mashup", "nightcore", "bootleg"];

    public async Task<List<Song>> SearchAsync(string query, int limit = 5)
    {
        try
        {
            var fetchLimit = limit * 3;
            var url      = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=song&limit={fetchLimit}";
            var json     = await _http.GetStringAsync(url);
            var response = JsonSerializer.Deserialize<ItunesSearchResponse>(json, JsonOpts);

            var tracks = response?.Results?
                .Where(r => r.TrackId > 0 && !string.IsNullOrEmpty(r.TrackName))
                .ToList() ?? [];

            var queryLower = query.ToLowerInvariant();
            bool queryIsRemix = RemixCoverKeywords.Any(k => queryLower.Contains(k));

            if (!queryIsRemix)
            {
                var originals = tracks
                    .Where(r => !RemixCoverKeywords.Any(k =>
                        (r.TrackName ?? "").ToLowerInvariant().Contains(k)))
                    .ToList();
                if (originals.Count > 0)
                    tracks = originals;
            }

            return tracks
                .Take(limit)
                .Select(r => new Song
                {
                    SpotifyUri = $"itunes:{r.TrackId}",
                    Title      = r.TrackName ?? "",
                    Artist     = r.ArtistName ?? "",
                    CoverUrl   = UpscaleArtwork(r.ArtworkUrl100),
                    DurationMs = r.TrackTimeMillis
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "iTunes search failed for query: {Query}", query);
            return [];
        }
    }

    private static string UpscaleArtwork(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        // iTunes returns 100x100; replace for 600x600
        return url.Replace("100x100bb", "600x600bb");
    }

    // ── iTunes API response models ───────────────────────────────────────────

    private class ItunesSearchResponse
    {
        public int ResultCount { get; set; }
        public List<ItunesTrack>? Results { get; set; }
    }

    private class ItunesTrack
    {
        public long   TrackId         { get; set; }
        public string? TrackName      { get; set; }
        public string? ArtistName     { get; set; }
        public string? CollectionName { get; set; }
        public string? ArtworkUrl100  { get; set; }
        public int    TrackTimeMillis { get; set; }
    }
}
