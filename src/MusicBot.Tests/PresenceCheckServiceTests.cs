using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MusicBot.Services;
using MusicBot.Services.Platforms;
using MusicBot.Tests.TestHelpers;
using Xunit;

namespace MusicBot.Tests;

/// <summary>
/// Tests para la máquina de estados de PresenceCheckService:
/// confirmación, cancelación, keep, aviso único por canción.
/// Los tests usan ChatResponseService real (sin senders registrados — descarta mensajes).
/// </summary>
public class PresenceCheckServiceTests
{
    // ── factory ───────────────────────────────────────────────────────────────

    private static QueueSettingsService MakeSettings(bool presenceEnabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:PresenceCheckEnabled"]        = presenceEnabled.ToString().ToLower(),
                ["Queue:PresenceCheckWarningSeconds"]  = "30",
                ["Queue:PresenceCheckConfirmSeconds"]  = "15",
            })
            .Build();
        return new QueueSettingsService(config, FakeHub.Instance);
    }

    private static PresenceCheckService MakeSvc(bool presenceEnabled = true)
    {
        var chat = new ChatResponseService(NullLogger<ChatResponseService>.Instance);
        return new PresenceCheckService(
            NullLogger<PresenceCheckService>.Instance,
            chat,
            MakeSettings(presenceEnabled));
    }

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void ConfirmPresence_ReturnsFalse_WhenNoUserExpected()
    {
        var svc = MakeSvc();
        Assert.False(svc.ConfirmPresence("alice"));
    }

    [Fact]
    public void KeepSong_ReturnsFalse_WhenNoUserExpected()
    {
        var svc = MakeSvc();
        Assert.False(svc.KeepSong());
    }

    [Fact]
    public void CancelCheck_DoesNotThrow_WhenNothingActive()
    {
        var svc = MakeSvc();
        var ex  = Record.Exception(() => svc.CancelCheck());
        Assert.Null(ex);
    }

    // ── IssueWarningForNext sets the expected user ────────────────────────────

    [Fact]
    public void ConfirmPresence_ReturnsTrue_AfterIssueWarningForNext()
    {
        var svc = MakeSvc();
        svc.IssueWarningForNext("alice");

        Assert.True(svc.ConfirmPresence("alice"));
    }

    [Fact]
    public void ConfirmPresence_ReturnsFalse_ForDifferentUser()
    {
        var svc = MakeSvc();
        svc.IssueWarningForNext("alice");

        Assert.False(svc.ConfirmPresence("bob"));
    }

    [Fact]
    public void ConfirmPresence_IsCaseInsensitive()
    {
        var svc = MakeSvc();
        svc.IssueWarningForNext("Alice");

        Assert.True(svc.ConfirmPresence("alice"));
    }

    // ── IssueWarningForNext is idempotent ─────────────────────────────────────

    [Fact]
    public void IssueWarningForNext_CalledTwice_OnlyFiresOnce()
    {
        var svc = MakeSvc();
        // The second call should not reset the expected user or the warning flag
        svc.IssueWarningForNext("alice");
        svc.IssueWarningForNext("bob");   // should be ignored

        // "alice" should still be the expected user
        Assert.True(svc.ConfirmPresence("alice"));
        // "bob" should NOT be the expected user
        Assert.False(svc.ConfirmPresence("bob"));
    }

    // ── KeepSong ──────────────────────────────────────────────────────────────

    [Fact]
    public void KeepSong_ReturnsTrue_WhenUserExpected()
    {
        var svc = MakeSvc();
        svc.IssueWarningForNext("alice");

        Assert.True(svc.KeepSong());
    }

    [Fact]
    public void KeepSong_SetsConfirmedSoCheckPasses()
    {
        var svc = MakeSvc();
        svc.IssueWarningForNext("alice");
        svc.KeepSong();

        // After KeepSong, ConfirmPresence should still return true (already confirmed)
        Assert.True(svc.ConfirmPresence("alice"));
    }

    // ── CancelCheck resets state ──────────────────────────────────────────────

    [Fact]
    public void CancelCheck_ResetsExpectedUser()
    {
        var svc = MakeSvc();
        svc.IssueWarningForNext("alice");
        svc.CancelCheck();

        Assert.False(svc.ConfirmPresence("alice"));
    }

    [Fact]
    public void CancelCheck_AllowsNewWarningToFireAfterReset()
    {
        var svc = MakeSvc();
        svc.IssueWarningForNext("alice");
        svc.CancelCheck();

        // After cancel, a new warning for bob should take effect
        svc.IssueWarningForNext("bob");
        Assert.True(svc.ConfirmPresence("bob"));
    }

    // ── Disabled presence check ───────────────────────────────────────────────

    [Fact]
    public void IssueWarningForNext_NoOp_WhenPresenceCheckDisabled()
    {
        var svc = MakeSvc(presenceEnabled: false);
        svc.IssueWarningForNext("alice");

        // Disabled → _expectedUser never set → ConfirmPresence returns false
        Assert.False(svc.ConfirmPresence("alice"));
    }
}
