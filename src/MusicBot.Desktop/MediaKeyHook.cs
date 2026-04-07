using System.Runtime.InteropServices;
using MusicBot.Core.Models;
using MusicBot.Services;

namespace MusicBot.Desktop;

/// <summary>
/// Installs a global low-level keyboard hook so media keys (play/pause, next, previous, stop)
/// control MusicBot playback regardless of which window is in focus.
/// </summary>
internal sealed class MediaKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;

    private const int VK_MEDIA_NEXT_TRACK = 0xB0;
    private const int VK_MEDIA_PREV_TRACK = 0xB1;
    private const int VK_MEDIA_STOP       = 0xB2;
    private const int VK_MEDIA_PLAY_PAUSE = 0xB3;

    [DllImport("user32.dll")]   private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]   private static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]   private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc; // must be kept alive to prevent GC collection
    private readonly IntPtr               _hook;
    private readonly UserContextManager   _userContext;
    private readonly CommandRouterService _router;

    public MediaKeyHook(UserContextManager userContext, CommandRouterService router)
    {
        _userContext = userContext;
        _router      = router;
        _proc        = HookCallback;

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module  = process.MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYDOWN)
        {
            switch (Marshal.ReadInt32(lParam))
            {
                case VK_MEDIA_PLAY_PAUSE: _ = TogglePlayPauseAsync(); break;
                case VK_MEDIA_NEXT_TRACK: _ = SkipAsync();            break;
                case VK_MEDIA_PREV_TRACK: RestartTrack();             break;
                case VK_MEDIA_STOP:       _ = StopAsync();            break;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private async Task TogglePlayPauseAsync()
    {
        try
        {
            var player = _userContext.GetOrCreate(LocalUser.Id).Player;
            if (player.IsPlaying)
                await player.PauseAsync();
            else
                await player.ResumeAsync();
        }
        catch { }
    }

    private async Task SkipAsync()
    {
        try
        {
            await _router.HandleAsync(new BotCommand
            {
                Type        = "skip",
                RequestedBy = "MediaKey",
                Platform    = "keyboard"
            }, _userContext.GetOrCreate(LocalUser.Id));
        }
        catch { }
    }

    private void RestartTrack()
    {
        try
        {
            var player = _userContext.GetOrCreate(LocalUser.Id).Player;
            // Seek to start if more than 3 s in, otherwise do nothing
            // (no "previous track" concept in a request queue)
            if (player.PositionMs > 3_000)
                player.SeekTo(0);
        }
        catch { }
    }

    private async Task StopAsync()
    {
        try
        {
            await _userContext.GetOrCreate(LocalUser.Id).Player.StopAsync();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
    }
}
