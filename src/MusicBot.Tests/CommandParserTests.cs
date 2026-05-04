using MusicBot.Services;
using Xunit;

namespace MusicBot.Tests;

/// <summary>
/// Tests para los helpers estáticos de CommandRouterService:
/// extracción de IDs de YouTube/Spotify y comparación de títulos.
/// </summary>
public class CommandParserTests
{
    // ── TryExtractYouTubeVideoId ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void TryExtractYouTubeVideoId_WatchUrl_ReturnsId(string url, string expected)
    {
        var result = CommandRouterService.TryExtractYouTubeVideoId(url, out var id);
        Assert.True(result);
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc123", "dQw4w9WgXcQ")]
    public void TryExtractYouTubeVideoId_ShortUrl_ReturnsId(string url, string expected)
    {
        var result = CommandRouterService.TryExtractYouTubeVideoId(url, out var id);
        Assert.True(result);
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("https://www.youtube.com/shorts/abc123XYZ")]
    [InlineData("https://youtube.com/shorts/abc123XYZ")]
    public void TryExtractYouTubeVideoId_ShortsUrl_ReturnsId(string url)
    {
        var result = CommandRouterService.TryExtractYouTubeVideoId(url, out var id);
        Assert.True(result);
        Assert.Equal("abc123XYZ", id);
    }

    [Theory]
    [InlineData("Bohemian Rhapsody Queen")]
    [InlineData("https://open.spotify.com/track/7tFiyTwD0nx5a1eklYtX2J")]
    [InlineData("https://www.youtube.com/playlist?list=PLabc123")]
    [InlineData("")]
    public void TryExtractYouTubeVideoId_NonVideoUrl_ReturnsFalse(string input)
    {
        var result = CommandRouterService.TryExtractYouTubeVideoId(input, out _);
        Assert.False(result);
    }

    // ── TryExtractSpotifyTrackId ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://open.spotify.com/track/7tFiyTwD0nx5a1eklYtX2J", "7tFiyTwD0nx5a1eklYtX2J")]
    [InlineData("https://open.spotify.com/track/7tFiyTwD0nx5a1eklYtX2J?si=abc", "7tFiyTwD0nx5a1eklYtX2J")]
    public void TryExtractSpotifyTrackId_ValidUrl_ReturnsId(string url, string expected)
    {
        var result = CommandRouterService.TryExtractSpotifyTrackId(url, out var id);
        Assert.True(result);
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("https://open.spotify.com/album/abc123")]
    [InlineData("https://open.spotify.com/playlist/abc123")]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("Bohemian Rhapsody")]
    public void TryExtractSpotifyTrackId_NonTrackUrl_ReturnsFalse(string input)
    {
        var result = CommandRouterService.TryExtractSpotifyTrackId(input, out _);
        Assert.False(result);
    }

    // ── IsTitleSimilar ────────────────────────────────────────────────────────

    [Fact]
    public void IsTitleSimilar_ExactMatch_ReturnsTrue()
    {
        Assert.True(CommandRouterService.IsTitleSimilar("Bohemian Rhapsody", "Bohemian Rhapsody"));
    }

    [Fact]
    public void IsTitleSimilar_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(CommandRouterService.IsTitleSimilar("BOHEMIAN RHAPSODY", "bohemian rhapsody"));
    }

    [Fact]
    public void IsTitleSimilar_YtContainsMeta_ReturnsTrue()
    {
        // YouTube title has extra text but contains the metadata title
        Assert.True(CommandRouterService.IsTitleSimilar("Queen - Bohemian Rhapsody (Official Video)", "bohemian rhapsody"));
    }

    [Fact]
    public void IsTitleSimilar_MetaContainsYt_ReturnsTrue()
    {
        Assert.True(CommandRouterService.IsTitleSimilar("Rhapsody", "Bohemian Rhapsody (Remastered)"));
    }

    [Fact]
    public void IsTitleSimilar_Unrelated_ReturnsFalse()
    {
        Assert.False(CommandRouterService.IsTitleSimilar("Bohemian Rhapsody", "Stairway to Heaven"));
    }

    [Theory]
    [InlineData(null, "title")]
    [InlineData("title", null)]
    [InlineData("", "title")]
    [InlineData("title", "")]
    public void IsTitleSimilar_NullOrEmpty_ReturnsFalse(string? a, string? b)
    {
        Assert.False(CommandRouterService.IsTitleSimilar(a!, b!));
    }
}
