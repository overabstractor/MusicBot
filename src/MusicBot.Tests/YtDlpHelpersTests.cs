using MusicBot.Services.Downloader;
using Xunit;

namespace MusicBot.Tests;

/// <summary>
/// Tests para los helpers estáticos de YtDlpDownloaderService:
/// SplitVideoTitle, NormalizeYouTubeUrl, IsUnavailablePlaylistEntry.
/// </summary>
public class YtDlpHelpersTests
{
    // ── SplitVideoTitle ───────────────────────────────────────────────────────

    [Fact]
    public void SplitVideoTitle_ArtistDashTitle_SplitsCorrectly()
    {
        var (title, artist) = YtDlpDownloaderService.SplitVideoTitle("Queen - Bohemian Rhapsody", "Queen - Topic");
        Assert.Equal("Bohemian Rhapsody", title);
        Assert.Equal("Queen",             artist);
    }

    [Fact]
    public void SplitVideoTitle_StripsSuffixInParentheses()
    {
        var (title, _) = YtDlpDownloaderService.SplitVideoTitle("Queen - Bohemian Rhapsody (Official Video)", "Queen");
        Assert.Equal("Bohemian Rhapsody", title);
    }

    [Fact]
    public void SplitVideoTitle_StripsSuffixInBrackets()
    {
        var (title, _) = YtDlpDownloaderService.SplitVideoTitle("Queen - Bohemian Rhapsody [Remastered]", "Queen");
        Assert.Equal("Bohemian Rhapsody", title);
    }

    [Fact]
    public void SplitVideoTitle_NoDashSeparator_UsesChannelAsArtist()
    {
        var (title, artist) = YtDlpDownloaderService.SplitVideoTitle("Bohemian Rhapsody", "Queen Official");
        Assert.Equal("Bohemian Rhapsody", title);
        Assert.Equal("Queen Official",    artist);
    }

    [Fact]
    public void SplitVideoTitle_SplitsOnFirstDash_WhenMultipleDashes()
    {
        var (title, artist) = YtDlpDownloaderService.SplitVideoTitle("AC/DC - Back In Black - Live", "AC/DC");
        Assert.Equal("AC/DC",                artist);
        Assert.Equal("Back In Black - Live", title);
    }

    [Fact]
    public void SplitVideoTitle_EmptyTitle_ReturnsEmpty()
    {
        var (title, artist) = YtDlpDownloaderService.SplitVideoTitle("", "Channel");
        Assert.Equal("",        title);
        Assert.Equal("Channel", artist);
    }

    // ── NormalizeYouTubeUrl ───────────────────────────────────────────────────

    [Fact]
    public void NormalizeYouTubeUrl_RegularYouTubeUrl_Unchanged()
    {
        const string url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        Assert.Equal(url, YtDlpDownloaderService.NormalizeYouTubeUrl(url));
    }

    [Fact]
    public void NormalizeYouTubeUrl_MusicPlaylistWithList_ConvertedToStandardPlaylist()
    {
        const string input    = "https://music.youtube.com/playlist?list=PLtest123";
        const string expected = "https://www.youtube.com/playlist?list=PLtest123";
        Assert.Equal(expected, YtDlpDownloaderService.NormalizeYouTubeUrl(input));
    }

    [Fact]
    public void NormalizeYouTubeUrl_MusicWatchWithList_ExtractsPlaylist()
    {
        const string input    = "https://music.youtube.com/watch?v=abc&list=PLtest456";
        const string expected = "https://www.youtube.com/playlist?list=PLtest456";
        Assert.Equal(expected, YtDlpDownloaderService.NormalizeYouTubeUrl(input));
    }

    [Fact]
    public void NormalizeYouTubeUrl_MusicUrlWithoutList_FallbackHostSwap()
    {
        const string input = "https://music.youtube.com/watch?v=abc123";
        var result = YtDlpDownloaderService.NormalizeYouTubeUrl(input);
        Assert.Contains("www.youtube.com", result);
        Assert.DoesNotContain("music.youtube.com", result);
    }

    // ── IsUnavailablePlaylistEntry ────────────────────────────────────────────

    [Theory]
    [InlineData("private video",  "public")]
    [InlineData("deleted video",  "public")]
    [InlineData("[Private Video]","public")]
    [InlineData("[Deleted Video]","public")]
    public void IsUnavailable_KnownPlaceholderTitles_ReturnTrue(string title, string availability)
    {
        Assert.True(YtDlpDownloaderService.IsUnavailablePlaylistEntry(title, availability));
    }

    [Theory]
    [InlineData("normal title", "private")]
    [InlineData("normal title", "premium_only")]
    [InlineData("normal title", "subscriber_only")]
    [InlineData("normal title", "needs_auth")]
    public void IsUnavailable_RestrictedAvailability_ReturnTrue(string title, string availability)
    {
        Assert.True(YtDlpDownloaderService.IsUnavailablePlaylistEntry(title, availability));
    }

    [Theory]
    [InlineData("Bohemian Rhapsody", "public")]
    [InlineData("Bohemian Rhapsody", "NA")]
    [InlineData("Bohemian Rhapsody", "")]
    public void IsUnavailable_NormalSong_ReturnFalse(string title, string availability)
    {
        Assert.False(YtDlpDownloaderService.IsUnavailablePlaylistEntry(title, availability));
    }
}
