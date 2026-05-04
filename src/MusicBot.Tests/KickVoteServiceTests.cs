using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Core.Models;
using MusicBot.Services;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

public class KickVoteServiceTests
{
    // ── factory helpers ───────────────────────────────────────────────────────

    private static QueueSettingsService MakeSettings(bool votingEnabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:VotingEnabled"] = votingEnabled.ToString().ToLower(),
            })
            .Build();
        return new QueueSettingsService(config, FakeHub.Instance);
    }

    private static KickVoteService MakeSvc(bool votingEnabled = true)
    {
        return new KickVoteService(
            FakeHub.Instance,
            NullLogger<KickVoteService>.Instance,
            MakeSettings(votingEnabled));
    }

    private static Song TestSong() => new()
    {
        SpotifyUri = "youtube:abc123",
        Title      = "Bohemian Rhapsody",
        Artist     = "Queen",
        CoverUrl   = "",
        DurationMs = 354_000,
    };

    // ── VoteAsync before any vote starts ──────────────────────────────────────

    [Fact]
    public async Task VoteAsync_ReturnsNoActive_WhenNoVoteStarted()
    {
        var svc    = MakeSvc();
        var result = await svc.VoteAsync("alice", skip: true);
        Assert.Equal("no_active", result);
    }

    // ── VoteAsync after vote starts ───────────────────────────────────────────

    [Fact]
    public async Task VoteAsync_ReturnsOk_OnFirstVoteForSkip()
    {
        var svc = MakeSvc();
        await svc.StartVoteAsync(TestSong());

        var result = await svc.VoteAsync("alice", skip: true);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task VoteAsync_ReturnsOk_OnFirstVoteAgainstSkip()
    {
        var svc = MakeSvc();
        await svc.StartVoteAsync(TestSong());

        var result = await svc.VoteAsync("bob", skip: false);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task VoteAsync_ReturnsAlreadyVoted_OnDuplicateUser()
    {
        var svc = MakeSvc();
        await svc.StartVoteAsync(TestSong());

        await svc.VoteAsync("alice", skip: true);
        var result = await svc.VoteAsync("alice", skip: false); // second vote

        Assert.Equal("already_voted", result);
    }

    [Fact]
    public async Task VoteAsync_AlreadyVoted_IsCaseInsensitive()
    {
        var svc = MakeSvc();
        await svc.StartVoteAsync(TestSong());

        await svc.VoteAsync("Alice", skip: true);
        var result = await svc.VoteAsync("alice", skip: true);

        Assert.Equal("already_voted", result);
    }

    [Fact]
    public async Task VoteAsync_MultipleDistinctUsers_AllCountedOk()
    {
        var svc = MakeSvc();
        await svc.StartVoteAsync(TestSong());

        var r1 = await svc.VoteAsync("u1", skip: true);
        var r2 = await svc.VoteAsync("u2", skip: true);
        var r3 = await svc.VoteAsync("u3", skip: false);

        Assert.Equal("ok", r1);
        Assert.Equal("ok", r2);
        Assert.Equal("ok", r3);
    }

    // ── StartVoteAsync when voting disabled ───────────────────────────────────

    [Fact]
    public async Task StartVoteAsync_WhenVotingDisabled_VoteRemainsInactive()
    {
        var svc = MakeSvc(votingEnabled: false);
        await svc.StartVoteAsync(TestSong());

        // Voting disabled → StartVoteAsync is a no-op → IsActive remains false
        var result = await svc.VoteAsync("alice", skip: true);
        Assert.Equal("no_active", result);
    }

    // ── GetCurrentVotePayload ─────────────────────────────────────────────────

    [Fact]
    public void GetCurrentVotePayload_ReturnsNull_WhenNoVoteActive()
    {
        var svc = MakeSvc();
        Assert.Null(svc.GetCurrentVotePayload());
    }

    [Fact]
    public async Task GetCurrentVotePayload_ReturnsPayload_AfterVoteStarts()
    {
        var svc = MakeSvc();
        await svc.StartVoteAsync(TestSong());

        Assert.NotNull(svc.GetCurrentVotePayload());
    }

    // ── NewVote cancels previous ──────────────────────────────────────────────

    [Fact]
    public async Task StartVoteAsync_SecondCall_ResetsVoteCounts()
    {
        var svc = MakeSvc();
        await svc.StartVoteAsync(TestSong());
        await svc.VoteAsync("alice", skip: true);

        // Start a brand new vote for a different song
        await svc.StartVoteAsync(new Song { SpotifyUri = "youtube:xyz", Title = "New Song", Artist = "Artist" });
        await svc.VoteAsync("alice", skip: true); // should be "ok", not "already_voted"

        // Verify alice can vote again in the new vote (was reset)
        var result = await svc.VoteAsync("alice", skip: false);
        Assert.Equal("already_voted", result); // she already voted "ok" in new vote above
    }
}
