using MusicBot.Services;
using Xunit;

namespace MusicBot.Tests;

/// <summary>
/// Tests para BannedSongService usando el constructor interno con ruta de archivo temporal.
/// Cada test usa su propio archivo para aislamiento total.
/// </summary>
public class BannedSongServiceTests : IDisposable
{
    private readonly string _tempFile;
    private readonly BannedSongService _svc;

    public BannedSongServiceTests()
    {
        _tempFile = Path.GetTempFileName();
        _svc = new BannedSongService(_tempFile);
    }

    public void Dispose() => File.Delete(_tempFile);

    // ── IsBanned ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsBanned_ReturnsFalseForUnknownUri()
    {
        Assert.False(_svc.IsBanned("spotify:track:unknown"));
    }

    [Fact]
    public void IsBanned_ReturnsFalseForNull()
    {
        Assert.False(_svc.IsBanned(null));
    }

    // ── Ban ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Ban_AddsSongAndIsBannedReturnsTrue()
    {
        _svc.Ban("spotify:track:abc", "Canción X", "Artista Y");

        Assert.True(_svc.IsBanned("spotify:track:abc"));
    }

    [Fact]
    public void Ban_IsIdempotent_DoesNotDuplicateEntries()
    {
        _svc.Ban("spotify:track:abc", "Canción X", "Artista Y");
        _svc.Ban("spotify:track:abc", "Canción X", "Artista Y");

        Assert.Single(_svc.GetAll());
    }

    [Fact]
    public void Ban_MultipleDistinctSongs_AllBanned()
    {
        _svc.Ban("uri:1", "Song A", "Artist A");
        _svc.Ban("uri:2", "Song B", "Artist B");

        Assert.True(_svc.IsBanned("uri:1"));
        Assert.True(_svc.IsBanned("uri:2"));
        Assert.Equal(2, _svc.GetAll().Count);
    }

    // ── Unban ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Unban_RemovesSong_IsBannedReturnsFalse()
    {
        _svc.Ban("uri:1", "Song", "Artist");
        var removed = _svc.Unban("uri:1");

        Assert.True(removed);
        Assert.False(_svc.IsBanned("uri:1"));
    }

    [Fact]
    public void Unban_ReturnsFalseForNonExistentUri()
    {
        Assert.False(_svc.Unban("uri:nonexistent"));
    }

    [Fact]
    public void Unban_DoesNotAffectOtherEntries()
    {
        _svc.Ban("uri:1", "Song A", "Artist A");
        _svc.Ban("uri:2", "Song B", "Artist B");
        _svc.Unban("uri:1");

        Assert.True(_svc.IsBanned("uri:2"));
        Assert.Single(_svc.GetAll());
    }

    // ── GetEntry ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetEntry_ReturnsNullForUnknownUri()
    {
        Assert.Null(_svc.GetEntry("uri:unknown"));
    }

    [Fact]
    public void GetEntry_ReturnsCorrectEntryAfterBan()
    {
        _svc.Ban("uri:1", "Mi Canción", "Mi Artista");
        var entry = _svc.GetEntry("uri:1");

        Assert.NotNull(entry);
        Assert.Equal("uri:1",       entry!.Uri);
        Assert.Equal("Mi Canción",  entry.Title);
        Assert.Equal("Mi Artista",  entry.Artist);
    }

    // ── Persistence (file round-trip) ─────────────────────────────────────────

    [Fact]
    public void Persistence_BannedSongsReloadedFromFile()
    {
        _svc.Ban("uri:persist", "Saved Song", "Saved Artist");

        // Create a new service instance pointing to the same file
        var svc2 = new BannedSongService(_tempFile);

        Assert.True(svc2.IsBanned("uri:persist"));
    }

    [Fact]
    public void Persistence_EmptyFileLoadsWithoutError()
    {
        // _tempFile already exists (from GetTempFileName) — new service should load fine
        var svc2 = new BannedSongService(_tempFile);
        Assert.Empty(svc2.GetAll());
    }
}
