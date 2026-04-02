using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Data;

namespace MusicBot.Services.Spotify;

public class SpotifyService : ISpotifyService
{
    private readonly SpotifySettings _settings;
    private readonly RelaySettings _relay;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SpotifyService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Guid _userId;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    private static readonly string[] Scopes = {
        "user-read-playback-state",
        "user-modify-playback-state",
        "user-read-currently-playing",
        "streaming",
        "user-read-email",
        "user-read-private",
        "playlist-read-private",
        "playlist-read-collaborative"
    };

    public bool IsAuthenticated => _accessToken != null;
    public Guid UserId => _userId;

    public SpotifyService(
        IOptions<SpotifySettings> settings,
        RelaySettings relay,
        IHttpClientFactory httpFactory,
        ILogger<SpotifyService> logger,
        IServiceScopeFactory scopeFactory,
        Guid userId)
    {
        _settings = settings.Value;
        _relay = relay;
        _httpFactory = httpFactory;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _userId = userId;
        LoadTokenFromDb();
    }

    public string GetAuthUrl(string? state = null)
    {
        var scope = string.Join(" ", Scopes);
        var url = $"https://accounts.spotify.com/authorize?response_type=code&client_id={_settings.ClientId}&scope={Uri.EscapeDataString(scope)}&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}&show_dialog=true";
        if (!string.IsNullOrEmpty(state))
            url += $"&state={Uri.EscapeDataString(state)}";
        return url;
    }

    public string GetAuthUrl() => GetAuthUrl(null);

    public async Task DisconnectAsync()
    {
        _accessToken  = null;
        _refreshToken = null;
        _expiresAt    = default;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var token = await db.SpotifyTokens.FindAsync(_userId);
            if (token != null)
            {
                db.SpotifyTokens.Remove(token);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Spotify token from DB for user {UserId}", _userId);
        }

        _logger.LogInformation("Spotify disconnected for user {UserId}", _userId);
    }

    public async Task HandleCallbackAsync(string code)
    {
        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]   = "authorization_code",
            ["code"]         = code,
            ["redirect_uri"] = _settings.RedirectUri,
        });
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        _accessToken = json.RootElement.GetProperty("access_token").GetString();
        _refreshToken = json.RootElement.GetProperty("refresh_token").GetString();
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());

        await SaveTokenToDb();
        _logger.LogInformation("Spotify authenticated for user {UserId}", _userId);
    }

    public async Task<List<Song>> SearchAsync(string query, int limit = 5)
    {
        var token = await GetAccessTokenAsync();
        var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit={limit}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpFactory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var tracks = json.RootElement.GetProperty("tracks").GetProperty("items");

        var songs = new List<Song>();
        foreach (var track in tracks.EnumerateArray())
            songs.Add(ParseTrack(track));

        return songs;
    }

    public async Task PlayAsync(string spotifyUri, string? deviceId = null)
    {
        var token = await GetAccessTokenAsync();
        var url = "https://api.spotify.com/v1/me/player/play";
        if (!string.IsNullOrEmpty(deviceId))
            url += $"?device_id={Uri.EscapeDataString(deviceId)}";

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { uris = new[] { spotifyUri } }),
            Encoding.UTF8, "application/json");

        var response = await _httpFactory.CreateClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Spotify PlayAsync failed: {Status} {Error}", response.StatusCode, error);
        }
    }

    // POST /me/player/queue?uri={uri} — requires user-modify-playback-state, returns 204
    public async Task AddToQueueAsync(string spotifyUri)
    {
        var token = await GetAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.spotify.com/v1/me/player/queue?uri={Uri.EscapeDataString(spotifyUri)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpFactory.CreateClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("Spotify add-to-queue failed: {Status} {Error}", response.StatusCode, error);
            throw new InvalidOperationException($"Spotify rejected the request ({(int)response.StatusCode}): {error}");
        }
    }

    // POST /me/player/next — requires user-modify-playback-state, returns 204
    public async Task SkipAsync()
    {
        var token = await GetAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.spotify.com/v1/me/player/next");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await _httpFactory.CreateClient().SendAsync(request);
    }

    public async Task PauseAsync()
    {
        var token = await GetAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, "https://api.spotify.com/v1/me/player/pause");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await _httpFactory.CreateClient().SendAsync(request);
    }

    public async Task ResumeAsync()
    {
        var token = await GetAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, "https://api.spotify.com/v1/me/player/play");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await _httpFactory.CreateClient().SendAsync(request);
    }

    // GET /me/player — requires user-read-playback-state
    public async Task<PlaybackState?> GetPlaybackStateAsync()
    {
        var token = await GetAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpFactory.CreateClient().SendAsync(request);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
            response.StatusCode == System.Net.HttpStatusCode.Accepted)
            return null;

        if (!response.IsSuccessStatusCode) return null;

        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;

        if (!root.TryGetProperty("item", out var item)) return null;

        var song = ParseTrack(item);

        return new PlaybackState
        {
            ProgressMs = root.TryGetProperty("progress_ms", out var p) ? p.GetInt32() : 0,
            IsPlaying = root.TryGetProperty("is_playing", out var ip) && ip.GetBoolean(),
            TrackUri = song.SpotifyUri,
            DurationMs = song.DurationMs,
            Title = song.Title,
            Artist = song.Artist,
            CoverUrl = song.CoverUrl
        };
    }

    // GET /me/player/queue — requires user-read-playback-state
    public async Task<SpotifyQueueState> GetQueueAsync()
    {
        var token = await GetAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/queue");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpFactory.CreateClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return new SpotifyQueueState();

        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var result = new SpotifyQueueState();

        if (json.RootElement.TryGetProperty("currently_playing", out var cp) &&
            cp.ValueKind != JsonValueKind.Null)
            result.CurrentlyPlaying = ParseTrack(cp);

        if (json.RootElement.TryGetProperty("queue", out var queue))
            foreach (var item in queue.EnumerateArray())
                result.Queue.Add(ParseTrack(item));

        return result;
    }

    public async Task<Song?> GetTrackAsync(string trackId)
    {
        var token = await GetAccessTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/tracks/{trackId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpFactory.CreateClient().SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return ParseTrack(json.RootElement);
    }

    public async Task<List<Song>> GetPlaylistTracksAsync(string playlistId, int maxTracks = 50)
    {
        // Always use user token for playlist access. Spotify no longer allows
        // Client Credentials tokens to access playlist content.
        var token = await GetAccessTokenAsync();

        var songs  = new List<Song>();
        var offset = 0;
        const int pageSize = 50;

        while (songs.Count < maxTracks)
        {
            var limit   = Math.Min(pageSize, maxTracks - songs.Count);
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.spotify.com/v1/playlists/{playlistId}/items?limit={limit}&offset={offset}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpFactory.CreateClient().SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Spotify playlist {Id}: {Status}: {Body}", playlistId, (int)response.StatusCode, body);
                string hint = response.StatusCode is System.Net.HttpStatusCode.Forbidden
                    ? " (si la playlist es privada o no tienes acceso, reconecta Spotify en Ajustes para renovar los permisos)"
                    : "";
                throw new InvalidOperationException(
                    $"Spotify API respondió {(int)response.StatusCode}: {body}{hint}");
            }

            var json  = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var items = json.RootElement.GetProperty("items");
            int added = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("track", out var track) || track.ValueKind == JsonValueKind.Null) continue;
                songs.Add(ParseTrack(track));
                added++;
            }

            if (added < limit) break; // last page
            offset += added;
        }

        return songs;
    }

    /// <summary>
    /// Obtains a short-lived app-level token using the Client Credentials flow.
    /// Works for all public Spotify content without requiring user authorization.
    /// </summary>
    private async Task<string> GetClientCredentialsTokenAsync()
    {
        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });
        var json     = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return json.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("Spotify CC token vacío");
    }

    private static Song ParseTrack(JsonElement track)
    {
        var artists = track.TryGetProperty("artists", out var arr)
            ? string.Join(", ", arr.EnumerateArray().Select(a => a.GetProperty("name").GetString() ?? ""))
            : "";

        var coverUrl = "";
        if (track.TryGetProperty("album", out var album) &&
            album.TryGetProperty("images", out var images))
        {
            var list = images.EnumerateArray().ToList();
            if (list.Count > 0) coverUrl = list[0].GetProperty("url").GetString() ?? "";
        }

        return new Song
        {
            SpotifyUri = track.TryGetProperty("uri", out var uri) ? uri.GetString() ?? "" : "",
            Title = track.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Artist = artists,
            CoverUrl = coverUrl,
            DurationMs = track.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0
        };
    }

    public async Task<string> GetAccessTokenAsync()
    {
        if (_accessToken == null)
            throw new InvalidOperationException("Not authenticated with Spotify");

        if (DateTimeOffset.UtcNow >= _expiresAt.AddMinutes(-1))
            await RefreshTokenAsync();

        return _accessToken;
    }

    private async Task RefreshTokenAsync()
    {
        if (_refreshToken == null) throw new InvalidOperationException("No refresh token");

        var response = await SendTokenRequest(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = _refreshToken,
        });
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        _accessToken = json.RootElement.GetProperty("access_token").GetString();
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());

        if (json.RootElement.TryGetProperty("refresh_token", out var rt))
            _refreshToken = rt.GetString();

        await SaveTokenToDb();
        _logger.LogInformation("Spotify token refreshed for user {UserId}", _userId);
    }

    private async Task<HttpResponseMessage> SendTokenRequest(Dictionary<string, string> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_relay.Url}/token/spotify");
        request.Headers.Add("X-Relay-Key", _relay.ApiKey);
        request.Content = JsonContent.Create(fields);

        var response = await _httpFactory.CreateClient().SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Spotify token request failed: {response.StatusCode} {error}");
        }
        return response;
    }

    private async Task SaveTokenToDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var existing = await db.SpotifyTokens.FindAsync(_userId);
            if (existing != null)
            {
                existing.AccessToken = _accessToken!;
                existing.RefreshToken = _refreshToken!;
                existing.ExpiresAt = _expiresAt;
            }
            else
            {
                db.SpotifyTokens.Add(new SpotifyToken
                {
                    UserId = _userId,
                    AccessToken = _accessToken!,
                    RefreshToken = _refreshToken!,
                    ExpiresAt = _expiresAt
                });
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save Spotify token to database");
        }
    }

    private void LoadTokenFromDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();

            var token = db.SpotifyTokens.Find(_userId);
            if (token == null) return;

            _accessToken = token.AccessToken;
            _refreshToken = token.RefreshToken;
            _expiresAt = token.ExpiresAt;
            _logger.LogInformation("Loaded Spotify token from DB for user {UserId}", _userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load Spotify token from DB");
        }
    }
}
