using System.IO;
using System.Web;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MusicBot.Core.Interfaces;
using MusicBot.Core.Models;
using MusicBot.Hubs;

namespace MusicBot.Services;

public class CommandRouterService
{
    private readonly ILogger<CommandRouterService> _logger;
    private readonly PlaybackSyncService           _sync;
    private readonly IMetadataService              _metadata;
    private readonly ILocalLibraryService          _library;
    private readonly Downloader.YtDlpDownloaderService _downloader;
    private readonly KickVoteService               _kickVote;
    private readonly BannedSongService             _banned;
    private readonly PresenceCheckService          _presence;
    private readonly AutoQueueService              _autoQueue;
    private readonly IServiceScopeFactory          _scopeFactory;
    private readonly IHubContext<OverlayHub>       _hub;

    public CommandRouterService(
        ILogger<CommandRouterService> logger,
        PlaybackSyncService sync,
        IMetadataService metadata,
        ILocalLibraryService library,
        Downloader.YtDlpDownloaderService downloader,
        KickVoteService kickVote,
        BannedSongService banned,
        PresenceCheckService presence,
        AutoQueueService autoQueue,
        IServiceScopeFactory scopeFactory,
        IHubContext<OverlayHub> hub)
    {
        _logger       = logger;
        _sync         = sync;
        _metadata     = metadata;
        _library      = library;
        _downloader   = downloader;
        _kickVote     = kickVote;
        _banned       = banned;
        _presence     = presence;
        _autoQueue    = autoQueue;
        _scopeFactory = scopeFactory;
        _hub          = hub;
    }

    public async Task<CommandResult> HandleAsync(BotCommand command, UserServices services)
    {
        try
        {
            return command.Type.ToLower() switch
            {
                "play"          => await HandlePlay(command, services),
                "skip"          => await HandleSkip(services),
                "selfskip"      => await HandleUserSkip(command, services),
                "revoke"        => HandleRevoke(command, services),
                "song"          => HandleSong(services),
                "like" or "love"=> await HandleLike(services),
                "queue" or "cola"=> HandleQueue(services),
                "pos" or "position" => HandlePos(command, services),
                "history" or "historial" => await HandleHistory(),
                "info"          => HandleInfo(command, services),
                "aqui"          => HandleAqui(command),
                "bump"          => HandleBump(command, services),
                "si" or "yes"   => await HandleVote(command.RequestedBy, true),
                "no"            => await HandleVote(command.RequestedBy, false),
                "keep"          => HandleKeep(),
                _               => CommandResult.Fail("Unknown command")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling command {Type}", command.Type);
            await BroadcastErrorAsync(ex.Message);
            return CommandResult.Fail(ex.Message);
        }
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    private async Task<CommandResult> HandlePlay(BotCommand command, UserServices services)
    {
        if (string.IsNullOrWhiteSpace(command.Query))
        {
            const string msg = "Por favor indica el nombre de la canción";
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        // 1. Search: direct URL handling, then YouTube-first for text queries
        Song? song = null;

        // YouTube URL directo
        if (TryExtractYouTubeVideoId(command.Query, out var ytVideoId))
        {
            song = await _downloader.FetchVideoMetadataAsync(ytVideoId);
            if (song != null)
                _logger.LogInformation("Metadata from YouTube URL: \"{Title}\" – {Artist}", song.Title, song.Artist);
        }
        // Spotify track URL
        else if (TryExtractSpotifyTrackId(command.Query, out var spotifyTrackId) && services.Spotify.IsAuthenticated)
        {
            try
            {
                song = await services.Spotify.GetTrackAsync(spotifyTrackId);
                if (song != null)
                    _logger.LogInformation("Metadata from Spotify URL: \"{Title}\" – {Artist}", song.Title, song.Artist);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch Spotify track {Id}", spotifyTrackId);
            }
        }
        else
        {
            // Text query: search YouTube directly first (most accurate for finding the right video)
            song = await _downloader.SearchBestMatchAsync(command.Query);
            if (song != null)
            {
                _logger.LogInformation("Found on YouTube: \"{Title}\" – {Artist}", song.Title, song.Artist);

                // Enrich cover art from iTunes using the YouTube title+artist
                var enrichQuery = $"{song.Title} {song.Artist}".Trim();
                var itunesResults = await _metadata.SearchAsync(enrichQuery, 1);
                if (itunesResults.Count > 0 && IsTitleSimilar(song.Title, itunesResults[0].Title))
                {
                    song.CoverUrl = itunesResults[0].CoverUrl;
                    _logger.LogInformation("Cover enriched from iTunes for \"{Title}\"", song.Title);
                }
                else if (services.Spotify.IsAuthenticated)
                {
                    try
                    {
                        var spotifyResults = await services.Spotify.SearchAsync(enrichQuery, 1);
                        if (spotifyResults.Count > 0 && IsTitleSimilar(song.Title, spotifyResults[0].Title))
                        {
                            song.CoverUrl = spotifyResults[0].CoverUrl;
                            _logger.LogInformation("Cover enriched from Spotify for \"{Title}\"", song.Title);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Spotify enrichment failed for \"{Title}\"", song.Title);
                    }
                }
                // If no cover found from iTunes/Spotify, the YouTube thumbnail remains as-is
            }
        }

        if (song == null)
        {
            var msg = $"No se encontraron resultados para \"{command.Query}\"";
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        // 2. Check if song is banned
        if (_banned.IsBanned(song.SpotifyUri))
        {
            var entry = _banned.GetEntry(song.SpotifyUri)!;
            var msg   = $"⛔ \"{entry.Title}\" de {entry.Artist} está baneada en este bot";
            await BroadcastErrorAsync(msg);
            return CommandResult.Fail(msg);
        }

        // 3. Check local library cache
        var cached = await _library.FindByTrackIdAsync(song.SpotifyUri);
        if (cached != null && File.Exists(cached.FilePath))
            song.LocalFilePath = cached.FilePath;

        // 4. Add to queue
        services.Queue.AddSong(song, command.RequestedBy, command.Platform);

        // 5. Notify overlays: song added
        await _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("song:added", new
        {
            title       = song.Title,
            artist      = song.Artist,
            coverUrl    = song.CoverUrl,
            requestedBy = command.RequestedBy,
            platform    = command.Platform,
        });

        // 6. Start background download if not cached
        if (song.LocalFilePath == null)
            _downloader.StartDownload(song);

        // 7. If something is already playing, pre-warm the next songs in queue
        //    so they download while the current track is playing
        if (services.Queue.GetCurrentItem() != null)
        {
            var upcoming = services.Queue.GetUpcoming();
            // Pre-warm the next two songs after the one we just added
            for (int i = 0; i < Math.Min(2, upcoming.Count); i++)
            {
                var nextSong = upcoming[i].Song;
                if (nextSong.LocalFilePath == null)
                    _ = Task.Run(() => PrewarmNextAsync(nextSong));
            }
        }

        // 8. If nothing is playing, advance and start immediately
        if (services.Queue.GetCurrentItem() == null)
        {
            services.Queue.Advance();
            await _sync.StartCurrentTrackAsync(services);
        }

        return CommandResult.Ok($"Agregada \"{song.Title}\" de {song.Artist} a la cola");
    }

    private async Task<CommandResult> HandleSkip(UserServices services)
    {
        await _sync.SkipCurrentAsync(services);
        return CommandResult.Ok("Canción salteada");
    }

    /// <summary>El usuario salta su propia canción (solo si es la que está sonando).</summary>
    private async Task<CommandResult> HandleUserSkip(BotCommand command, UserServices services)
    {
        var current = services.Queue.GetCurrentItem();
        if (current == null)
            return CommandResult.Fail("No hay ninguna canción sonando");

        if (!current.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"La canción actual fue pedida por {current.RequestedBy}, no puedes saltarla");

        await _sync.SkipCurrentAsync(services);
        return CommandResult.Ok($"@{command.RequestedBy} salteó su canción");
    }

    /// <summary>Muestra la canción que está sonando actualmente.</summary>
    private static CommandResult HandleSong(UserServices services)
    {
        var current = services.Queue.GetCurrentItem();
        if (current == null)
            return CommandResult.Ok("No hay ninguna canción sonando ahora mismo");

        var song   = current.Song;
        var artist = string.IsNullOrEmpty(song.Artist) ? "" : $" - {song.Artist}";
        return CommandResult.Ok($"Sonando: \"{song.Title}{artist}\" (pedida por @{current.RequestedBy})");
    }

    /// <summary>El usuario elimina su canción de la cola (upcoming).</summary>
    private static CommandResult HandleRevoke(BotCommand command, UserServices services)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy es requerido");

        var removed = services.Queue.Revoke(command.RequestedBy);
        return removed != null
            ? CommandResult.Ok($"@{command.RequestedBy}: se eliminó \"{removed.Song.Title}\" de la cola")
            : CommandResult.Fail($"@{command.RequestedBy} no tiene canciones en la cola");
    }

    /// <summary>Muestra cuántas canciones tiene el usuario en la cola y en qué posiciones.</summary>
    private static CommandResult HandleInfo(BotCommand command, UserServices services)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy es requerido");

        var upcoming = services.Queue.GetUpcoming();
        var userSongs = upcoming
            .Select((item, index) => (item, position: index + 1))
            .Where(x => x.item.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (userSongs.Count == 0)
            return CommandResult.Ok($"@{command.RequestedBy} no tienes canciones en la cola");

        var details = string.Join(", ", userSongs.Select(x => $"#{x.position} \"{x.item.Song.Title}\""));
        return CommandResult.Ok($"@{command.RequestedBy} tienes {userSongs.Count} canción(es) en la cola: {details}");
    }

    /// <summary>Confirma la presencia del usuario para el chequeo de presencia.</summary>
    private CommandResult HandleAqui(BotCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy es requerido");

        var confirmed = _presence.ConfirmPresence(command.RequestedBy);
        return confirmed
            ? CommandResult.Ok($"@{command.RequestedBy} ¡confirmado! Tu canción está reservada")
            : CommandResult.Fail($"@{command.RequestedBy} no hay ninguna canción esperándote ahora");
    }

    private static CommandResult HandleBump(BotCommand command, UserServices services)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy is required for bump");

        var moved = services.Queue.Bump(command.RequestedBy);
        return moved
            ? CommandResult.Ok($"La canción de {command.RequestedBy} subió en la cola")
            : CommandResult.Fail($"{command.RequestedBy} no tiene canciones para subir");
    }

    private async Task<CommandResult?> HandleVote(string? username, bool skip)
    {
        if (string.IsNullOrWhiteSpace(username))
            return CommandResult.Fail("Debes indicar tu nombre de usuario para votar");

        // Omit votes if voting is disabled, to avoid confusion.
        // This means the "si"/"no" commands will simply do nothing when there's no kick vote in progress.
        if (!_kickVote.VotingEnabled) return null;

        var result = await _kickVote.VoteAsync(username, skip);
        return result switch
        {
            "ok"           => CommandResult.Ok($"{username} votó {(skip ? "¡skipear!" : "¡quedarse!")}"),
            "already_voted"=> CommandResult.Fail($"{username} ya votó"),
            "no_active"    => CommandResult.Fail("No hay votación activa en este momento"),
            _              => CommandResult.Fail("Error al registrar voto"),
        };
    }

    private CommandResult HandleKeep()
    {
        var kept = _presence.KeepSong();
        return kept
            ? CommandResult.Ok("¡La canción se queda! El chat la salvó")
            : CommandResult.Fail("No hay canción esperando confirmación");
    }

    /// <summary>Agrega la canción actual a la auto-cola.</summary>
    private async Task<CommandResult> HandleLike(UserServices services)
    {
        var current = services.Queue.GetCurrentItem();
        if (current == null)
            return CommandResult.Fail("No hay ninguna canción sonando ahora mismo");

        var added = await _autoQueue.AddAsync(current.Song);
        var title = current.Song.Title;
        return added
            ? CommandResult.Ok($"❤️ \"{title}\" añadida a la auto-cola")
            : CommandResult.Fail($"\"{title}\" ya estaba en la auto-cola");
    }

    /// <summary>Muestra las próximas canciones en la cola.</summary>
    private static CommandResult HandleQueue(UserServices services)
    {
        var upcoming = services.Queue.GetUpcoming()
            .Where(i => !i.IsPlaylistItem)
            .Take(5)
            .ToList();

        if (upcoming.Count == 0)
            return CommandResult.Ok("La cola de solicitudes está vacía");

        var items = string.Join(", ", upcoming.Select((i, idx) =>
            $"#{idx + 1} \"{i.Song.Title}\" (@{i.RequestedBy})"));
        return CommandResult.Ok($"Cola ({upcoming.Count}): {items}");
    }

    /// <summary>Muestra la posición del usuario en la cola.</summary>
    private static CommandResult HandlePos(BotCommand command, UserServices services)
    {
        if (string.IsNullOrWhiteSpace(command.RequestedBy))
            return CommandResult.Fail("RequestedBy es requerido");

        var upcoming = services.Queue.GetUpcoming();
        var idx = upcoming.FindIndex(i =>
            i.RequestedBy.Equals(command.RequestedBy, StringComparison.OrdinalIgnoreCase));

        if (idx < 0)
            return CommandResult.Fail($"@{command.RequestedBy} no tienes canciones en la cola");

        var item    = upcoming[idx];
        var waitMs  = upcoming.Take(idx).Sum(i => i.Song.DurationMs);
        var waitMin = (int)Math.Ceiling(waitMs / 60_000.0);
        var waitStr = waitMin > 0 ? $" (~{waitMin} min)" : "";
        return CommandResult.Ok(
            $"@{command.RequestedBy}: \"{item.Song.Title}\" está en posición #{idx + 1}{waitStr}");
    }

    /// <summary>Muestra las últimas 3 canciones reproducidas.</summary>
    private async Task<CommandResult> HandleHistory()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicBot.Data.MusicBotDbContext>();
        var recent = await db.PlayedSongs
            .OrderByDescending(p => p.PlayedAt)
            .Take(3)
            .ToListAsync();

        if (recent.Count == 0)
            return CommandResult.Ok("No hay historial de canciones aún");

        var list = string.Join(", ", recent.Select(p =>
            string.IsNullOrEmpty(p.Artist) ? $"\"{p.Title}\"" : $"\"{p.Title}\" ({p.Artist})"));
        return CommandResult.Ok($"Últimas canciones: {list}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Starts a background download for the next song in queue (fire-and-forget).</summary>
    private async Task PrewarmNextAsync(Song song)
    {
        if (song.LocalFilePath != null && File.Exists(song.LocalFilePath)
            && new FileInfo(song.LocalFilePath).Length > 100_000)
            return;
        try
        {
            await _downloader.GetOrStartDownloadAsync(song);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Queue pre-warm download failed for \"{Title}\"", song.Title);
        }
    }

    private Task BroadcastErrorAsync(string message)
        => _hub.Clients.Group($"user:{LocalUser.Id}").SendAsync("queue:error", new { message });

    private static bool TryExtractYouTubeVideoId(string query, out string videoId)
    {
        videoId = string.Empty;
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();

        if (host is "youtu.be")
        {
            videoId = uri.AbsolutePath.TrimStart('/').Split('/')[0];
            return !string.IsNullOrEmpty(videoId);
        }

        if (host is "www.youtube.com" or "youtube.com" or "m.youtube.com")
        {
            // /watch?v=ID  or  /shorts/ID
            if (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
            {
                videoId = uri.AbsolutePath["/shorts/".Length..].Split('/')[0];
                return !string.IsNullOrEmpty(videoId);
            }

            var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var v  = qs["v"];
            if (!string.IsNullOrEmpty(v) && !uri.AbsolutePath.StartsWith("/playlist"))
            {
                videoId = v;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when two song titles are similar enough that iTunes/Spotify metadata
    /// can be used to enrich the YouTube result's cover art.
    /// </summary>
    private static bool IsTitleSimilar(string ytTitle, string metaTitle)
    {
        if (string.IsNullOrEmpty(ytTitle) || string.IsNullOrEmpty(metaTitle)) return false;
        var a = ytTitle.ToLowerInvariant().Trim();
        var b = metaTitle.ToLowerInvariant().Trim();
        return a == b || a.Contains(b) || b.Contains(a);
    }

    private static bool TryExtractSpotifyTrackId(string query, out string trackId)
    {
        trackId = string.Empty;
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host != "open.spotify.com") return false;

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length >= 2 && parts[0] == "track")
        {
            trackId = parts[1];
            return !string.IsNullOrEmpty(trackId);
        }

        return false;
    }
}
