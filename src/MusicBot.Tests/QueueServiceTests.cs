using MusicBot.Core.Models;
using MusicBot.Core.Services;
using Xunit;

namespace MusicBot.Tests;

public class QueueServiceTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static Song MakeSong(string id, string title = "Song", int durationMs = 200_000) => new()
    {
        SpotifyUri = id,
        Title      = title,
        Artist     = "Artist",
        CoverUrl   = "",
        DurationMs = durationMs,
    };

    private static QueueItem AddUser(QueueService svc, string id, string user = "user1", string platform = "twitch")
        => svc.AddSong(MakeSong(id), user, platform);

    // ── AddSong ───────────────────────────────────────────────────────────────

    [Fact]
    public void AddSong_EnqueuesAndReturnsItem()
    {
        var svc  = new QueueService();
        var item = AddUser(svc, "s:1");

        Assert.Equal("s:1", item.Song.SpotifyUri);
        Assert.Equal("user1", item.RequestedBy);
        Assert.Equal(1, svc.QueueLength);
    }

    [Fact]
    public void AddSong_ThrowsWhenSongIsCurrentlyPlaying()
    {
        var svc  = new QueueService();
        var item = AddUser(svc, "s:1");
        svc.Advance();   // s:1 becomes current

        Assert.Throws<InvalidOperationException>(() => AddUser(svc, "s:1"));
    }

    [Fact]
    public void AddSong_ThrowsWhenDuplicateInQueue()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1");

        Assert.Throws<InvalidOperationException>(() => AddUser(svc, "s:1", "user2"));
    }

    [Fact]
    public void AddSong_ThrowsWhenQueueFull()
    {
        var svc = new QueueService(maxSize: 2);
        AddUser(svc, "s:1", "u1");
        AddUser(svc, "s:2", "u2");

        Assert.Throws<InvalidOperationException>(() => AddUser(svc, "s:3", "u3"));
    }

    [Fact]
    public void AddSong_ThrowsWhenUserLimitReached()
    {
        var svc = new QueueService(maxPerUser: 1);
        AddUser(svc, "s:1", "u1");

        Assert.Throws<InvalidOperationException>(() => AddUser(svc, "s:2", "u1"));
    }

    [Fact]
    public void AddSong_BypassUserLimit_Works()
    {
        var svc  = new QueueService(maxPerUser: 1);
        AddUser(svc, "s:1", "u1");
        svc.AddSong(MakeSong("s:2"), "u1", "twitch", bypassUserLimit: true);

        Assert.Equal(2, svc.QueueLength);
    }

    [Fact]
    public void AddSong_UserItemInsertedBeforePlaylistItems()
    {
        var svc = new QueueService();
        svc.AddSong(MakeSong("pl:1"), "Playlist", "web", isPlaylistItem: true);
        svc.AddSong(MakeSong("pl:2"), "Playlist", "web", isPlaylistItem: true);

        // User request should jump ahead of playlist items
        var item = svc.AddSong(MakeSong("u:1"), "user1", "twitch");
        var upcoming = svc.GetUpcoming();

        Assert.Equal("u:1", upcoming[0].Song.SpotifyUri);
    }

    // ── Advance ───────────────────────────────────────────────────────────────

    [Fact]
    public void Advance_ReturnsFirstUpcomingAndRemovesIt()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1");
        AddUser(svc, "s:2", "user2");

        var current = svc.Advance();

        Assert.Equal("s:1", current!.Song.SpotifyUri);
        Assert.Equal(1, svc.QueueLength);
    }

    [Fact]
    public void Advance_ReturnsNullWhenEmpty()
    {
        var svc = new QueueService();
        Assert.Null(svc.Advance());
        Assert.Null(svc.GetCurrentItem());
    }

    [Fact]
    public void Advance_FallsBackToBackgroundPlaylist()
    {
        var svc = new QueueService();
        svc.SetBackgroundPlaylist([MakeSong("bg:1"), MakeSong("bg:2")]);

        var first  = svc.Advance();
        var second = svc.Advance();

        Assert.Equal("bg:1", first!.Song.SpotifyUri);
        Assert.True(first.IsPlaylistItem);
        Assert.Equal("bg:2", second!.Song.SpotifyUri);
    }

    [Fact]
    public void Advance_BackgroundPlaylistIsCyclic()
    {
        var svc = new QueueService();
        svc.SetBackgroundPlaylist([MakeSong("bg:1")]);

        svc.Advance();
        var second = svc.Advance();

        Assert.Equal("bg:1", second!.Song.SpotifyUri);
    }

    // ── Skip ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Skip_AdvancesQueueAndReturnsSkippedItem()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1");
        AddUser(svc, "s:2", "user2");
        svc.Advance(); // s:1 is now playing

        var skipped = svc.Skip();

        Assert.Equal("s:1", skipped!.Song.SpotifyUri);
        Assert.Equal("s:2", svc.GetCurrentItem()!.Song.SpotifyUri);
    }

    // ── Revoke ────────────────────────────────────────────────────────────────

    [Fact]
    public void Revoke_RemovesFirstUserSong()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "alice");

        var removed = svc.Revoke("alice");

        Assert.Equal("s:1", removed!.Song.SpotifyUri);
        Assert.Equal(0, svc.QueueLength);
    }

    [Fact]
    public void Revoke_ReturnsNullWhenUserHasNoSong()
    {
        var svc = new QueueService();
        Assert.Null(svc.Revoke("nobody"));
    }

    [Fact]
    public void Revoke_IsCaseInsensitive()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "Alice");

        var removed = svc.Revoke("alice");
        Assert.NotNull(removed);
    }

    // ── Bump ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Bump_MovesUserSongOnePositionUp()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");
        AddUser(svc, "s:2", "u2");

        var moved = svc.Bump("u2");

        Assert.True(moved);
        Assert.Equal("s:2", svc.GetUpcoming()[0].Song.SpotifyUri);
    }

    [Fact]
    public void Bump_ReturnsFalseWhenAlreadyFirst()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");

        Assert.False(svc.Bump("u1"));
    }

    // ── BumpToFront ───────────────────────────────────────────────────────────

    [Fact]
    public void BumpToFront_MovesItemToPosition0()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");
        AddUser(svc, "s:2", "u2");
        AddUser(svc, "s:3", "u3");

        svc.BumpToFront("u3");

        Assert.Equal("s:3", svc.GetUpcoming()[0].Song.SpotifyUri);
    }

    // ── MoveUp / MoveDown ─────────────────────────────────────────────────────

    [Fact]
    public void MoveUp_SwapsWithPreviousItem()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");
        AddUser(svc, "s:2", "u2");

        svc.MoveUp("s:2");
        var upcoming = svc.GetUpcoming();

        Assert.Equal("s:2", upcoming[0].Song.SpotifyUri);
        Assert.Equal("s:1", upcoming[1].Song.SpotifyUri);
    }

    [Fact]
    public void MoveDown_SwapsWithNextItem()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");
        AddUser(svc, "s:2", "u2");

        svc.MoveDown("s:1");
        var upcoming = svc.GetUpcoming();

        Assert.Equal("s:2", upcoming[0].Song.SpotifyUri);
        Assert.Equal("s:1", upcoming[1].Song.SpotifyUri);
    }

    // ── PlayNow ───────────────────────────────────────────────────────────────

    [Fact]
    public void PlayNow_SetsSongAsCurrentAndPushesPreviousBack()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");
        svc.Advance(); // s:1 is playing

        svc.PlayNow(MakeSong("s:now"), "admin", "web");

        Assert.Equal("s:now", svc.GetCurrentItem()!.Song.SpotifyUri);
        Assert.Equal("s:1", svc.GetUpcoming()[0].Song.SpotifyUri); // pushed back
    }

    // ── InterruptForUser ──────────────────────────────────────────────────────

    [Fact]
    public void InterruptForUser_SetsDonorAsCurrent_AndPushesInterruptedBack()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");
        svc.Advance(); // s:1 is now playing
        AddUser(svc, "s:donor", "donor");

        var result = svc.InterruptForUser("donor");

        Assert.True(result);
        Assert.Equal("s:donor", svc.GetCurrentItem()!.Song.SpotifyUri);
        Assert.Equal("s:1", svc.GetUpcoming()[0].Song.SpotifyUri);
    }

    // ── Reorder ───────────────────────────────────────────────────────────────

    [Fact]
    public void Reorder_MovesItemToNewIndex()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");
        AddUser(svc, "s:2", "u2");
        AddUser(svc, "s:3", "u3");

        svc.Reorder("s:3", 0);
        var upcoming = svc.GetUpcoming();

        Assert.Equal("s:3", upcoming[0].Song.SpotifyUri);
        Assert.Equal("s:1", upcoming[1].Song.SpotifyUri);
    }

    // ── RemoveByUri ───────────────────────────────────────────────────────────

    [Fact]
    public void RemoveByUri_RemovesCorrectItem()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");
        AddUser(svc, "s:2", "u2");

        var removed = svc.RemoveByUri("s:1");

        Assert.True(removed);
        Assert.Equal(1, svc.QueueLength);
        Assert.Equal("s:2", svc.GetUpcoming()[0].Song.SpotifyUri);
    }

    // ── SetBackgroundPlaylist ─────────────────────────────────────────────────

    [Fact]
    public void SetBackgroundPlaylist_ClearsOldPlaylistItemsFromQueue()
    {
        var svc = new QueueService();
        svc.AddSong(MakeSong("pl:old"), "Playlist", "web", isPlaylistItem: true);
        svc.SetBackgroundPlaylist([MakeSong("pl:new")]);

        // Old playlist items should be removed
        Assert.Equal(0, svc.QueueLength);
    }

    // ── ClearUserQueue ────────────────────────────────────────────────────────

    [Fact]
    public void ClearUserQueue_RemovesOnlyUserItems()
    {
        var svc = new QueueService();
        AddUser(svc, "u:1", "u1");
        svc.AddSong(MakeSong("pl:1"), "Playlist", "web", isPlaylistItem: true);

        svc.ClearUserQueue();

        var upcoming = svc.GetUpcoming();
        Assert.Single(upcoming);
        Assert.True(upcoming[0].IsPlaylistItem);
    }

    // ── MarkDownloadError / UpdateSongForAlternative ──────────────────────────

    [Fact]
    public void MarkDownloadError_SetsErrorOnUpcomingItem()
    {
        var svc = new QueueService();
        AddUser(svc, "s:1", "u1");

        svc.MarkDownloadError("s:1", "429 Too Many Requests");

        Assert.Equal("429 Too Many Requests", svc.GetUpcoming()[0].DownloadError);
    }

    [Fact]
    public void UpdateSongForAlternative_ReplacesSongAndClearsError()
    {
        var svc = new QueueService();
        AddUser(svc, "s:old", "u1");
        svc.MarkDownloadError("s:old", "error");

        var newSong = MakeSong("s:new", "New Title");
        svc.UpdateSongForAlternative("s:old", newSong);

        var upcoming = svc.GetUpcoming();
        Assert.Equal("s:new", upcoming[0].Song.SpotifyUri);
        Assert.Null(upcoming[0].DownloadError);
    }

    // ── GetState (background playlist synthesis) ──────────────────────────────

    [Fact]
    public void GetState_SynthesizesBackgroundPlaylistInUpcoming()
    {
        var svc = new QueueService();
        svc.SetBackgroundPlaylist([MakeSong("bg:1"), MakeSong("bg:2")]);

        var state = svc.GetState();

        // The two bg songs should appear in Upcoming even though they are not in _upcoming
        var bgItems = state.Upcoming.Where(i => i.IsPlaylistItem).ToList();
        Assert.Equal(2, bgItems.Count);
    }

    // ── UpdateLimits ──────────────────────────────────────────────────────────

    [Fact]
    public void UpdateLimits_NewLimitsAreEnforced()
    {
        var svc = new QueueService(maxPerUser: 5);
        svc.UpdateLimits(maxQueueSize: 1, maxSongsPerUser: 1);

        AddUser(svc, "s:1", "u1");

        Assert.Throws<InvalidOperationException>(() => AddUser(svc, "s:2", "u2")); // queue full
    }

    // ── Events ────────────────────────────────────────────────────────────────

    [Fact]
    public void OnSongAdded_FiresWhenSongEnqueued()
    {
        var svc   = new QueueService();
        QueueItem? fired = null;
        svc.OnSongAdded += item => fired = item;

        AddUser(svc, "s:1");

        Assert.NotNull(fired);
        Assert.Equal("s:1", fired!.Song.SpotifyUri);
    }

    [Fact]
    public void OnSongRemoved_FiresWhenSongRevoked()
    {
        var svc   = new QueueService();
        QueueItem? fired = null;
        svc.OnSongRemoved += item => fired = item;

        AddUser(svc, "s:1", "u1");
        svc.Revoke("u1");

        Assert.NotNull(fired);
        Assert.Equal("s:1", fired!.Song.SpotifyUri);
    }

    // ── Seed ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Seed_RestoresQueueState()
    {
        var svc     = new QueueService();
        var current = new QueueItem { Song = MakeSong("s:cur"), RequestedBy = "u0", Platform = "web" };
        var items   = new[] { new QueueItem { Song = MakeSong("s:1"), RequestedBy = "u1", Platform = "web" } };

        svc.Seed(current, items);

        Assert.Equal("s:cur", svc.GetCurrentItem()!.Song.SpotifyUri);
        Assert.Equal(1, svc.QueueLength);
    }

    // ── PromoteFromBackground ─────────────────────────────────────────────────

    [Fact]
    public void PromoteFromBackground_MovesFromPlaylistToUpcoming()
    {
        var svc = new QueueService();
        svc.SetBackgroundPlaylist([MakeSong("bg:1"), MakeSong("bg:2")]);

        var promoted = svc.PromoteFromBackground("bg:2");

        Assert.True(promoted);
        var (songs, _) = svc.GetBackgroundPlaylist();
        Assert.Single(songs);
        Assert.Equal("bg:1", songs[0].SpotifyUri);
        Assert.Equal(1, svc.QueueLength);
    }
}
