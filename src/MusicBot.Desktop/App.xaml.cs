using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicBot.Data;
using MusicBot.Services;
using SysWin = System.Windows;

// Alias WinForms types to avoid ambiguity with WPF equivalents
using WFFont      = System.Drawing.Font;
using WFFontStyle = System.Drawing.FontStyle;
using WFSysFonts  = System.Drawing.SystemFonts;

namespace MusicBot.Desktop;

/// <summary>
/// WPF application root. Manages the system tray icon, the main browser window,
/// and the log viewer window. Receives the running WebApplication so it can stop
/// it on exit.
/// </summary>
public partial class App : SysWin.Application
{
    // Set by Program.cs before calling Run()
    public WebApplication Host { get; set; } = null!;

    private NotifyIcon         _tray        = null!;
    private MainWindow?        _mainWindow;
    private LogViewerWindow?   _logViewer;
    private TikTokLoginWindow? _tiktokLogin;
    private MediaKeyHook?      _mediaKeys;
    private bool               _trayHintShown;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        System.Windows.Forms.Application.EnableVisualStyles();

        // Subscribe to Web → Desktop events
        MusicBot.AppEvents.OnOpenLogRequested              += () => Dispatcher.Invoke(ShowLogs);
        MusicBot.AppEvents.OnOpenLogDirRequested           += () => Dispatcher.Invoke(OpenLogDir);
        MusicBot.AppEvents.OnTikTokLoginRequested          += () => Dispatcher.Invoke(ShowTikTokLogin);
        MusicBot.AppEvents.OnTikTokSessionRestoreRequested += () => Dispatcher.Invoke(RestoreTikTokSession);
        MusicBot.AppEvents.OnPlatformAuthForgotten         += ForgetPlatformSessionAsync;
        MusicBot.AppEvents.OnShutdownRequested             += () => Dispatcher.Invoke(ExitApp);
        MusicBot.AppEvents.OnUpdateReady                   += v  => Dispatcher.Invoke(() =>
            _tray.ShowBalloonTip(5_000, "MusicBot — Actualización lista",
                $"Versión {v} descargada. Se aplicará al reiniciar.",
                ToolTipIcon.Info));

        _tray      = BuildTray();
        _mediaKeys = new MediaKeyHook(
            Host.Services.GetRequiredService<MusicBot.Services.UserContextManager>(),
            Host.Services.GetRequiredService<MusicBot.Services.CommandRouterService>());
        ShowMainWindow();

        // Trigger TikTok session restore AFTER subscribing to events (avoids race condition
        // where TikTokAuthService fires the event before the WPF subscriber is registered).
        _ = Host.Services.GetRequiredService<MusicBot.Services.Platforms.TikTokAuthService>()
                         .InitAsync();

        // Auto-open log viewer if configured
        if (Host.Configuration.GetValue<bool>("Desktop:OpenLogOnStart", false))
            ShowLogs();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mediaKeys?.Dispose();
        _tray.Dispose();
        base.OnExit(e);
    }

    // ── Window management ─────────────────────────────────────────────────────

    private void ShowMainWindow()
    {
        if (_mainWindow == null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closing += (_, ev) =>
            {
                ev.Cancel = true;
                _mainWindow.Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloonTip(2_500, "MusicBot",
                        "Minimizado a la bandeja del sistema. Doble clic para volver.",
                        ToolTipIcon.Info);
                }
            };
        }

        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.WindowState = SysWin.WindowState.Normal;
    }

    private void ShowLogs()
    {
        _logViewer ??= new LogViewerWindow();
        if (!_logViewer.IsVisible) _logViewer.Show();
        _logViewer.Activate();
        _logViewer.WindowState = SysWin.WindowState.Normal;
    }

    private static void OpenLogDir()
    {
        var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        if (!System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private void RestoreTikTokSession()
    {
        if (_tiktokLogin != null && _tiktokLogin.IsLoaded) return;

        _tiktokLogin = new TikTokLoginWindow
        {
            Owner         = _mainWindow,
            WindowState   = SysWin.WindowState.Minimized,
            ShowInTaskbar = false,
        };
        _tiktokLogin.Show();   // needed so WebView2 can initialize with a window handle
        _tiktokLogin.Hide();   // immediately invisible
        _tiktokLogin.RestoreSession();
    }

    private void ShowTikTokLogin()
    {
        if (_tiktokLogin != null && _tiktokLogin.IsLoaded)
        {
            // Window already exists — reset it to the login page (may have been in silent-restore state)
            _tiktokLogin.ResetAndShowLogin();
            // Restore window to a visible, normal-sized state regardless of how it was created
            _tiktokLogin.ShowInTaskbar = true;
            _tiktokLogin.WindowState   = SysWin.WindowState.Normal;
            _tiktokLogin.Width         = 480;
            _tiktokLogin.Height        = 700;
            _tiktokLogin.Show();
            _tiktokLogin.Activate();
            return;
        }

        _tiktokLogin = new TikTokLoginWindow { Owner = _mainWindow };
        _tiktokLogin.Show();
        _tiktokLogin.Activate();
    }

    private Task ForgetPlatformSessionAsync(string platform)
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                switch (platform)
                {
                    case "tiktok":
                        // Clear TikTok cookies from the dedicated TikTok WebView2 profile
                        if (_tiktokLogin == null || !_tiktokLogin.IsLoaded)
                        {
                            _tiktokLogin = new TikTokLoginWindow
                            {
                                Owner         = _mainWindow,
                                WindowState   = SysWin.WindowState.Minimized,
                                ShowInTaskbar = false,
                            };
                            _tiktokLogin.SuppressAutoNavigation();
                            _tiktokLogin.Show();
                            _tiktokLogin.Hide();
                        }
                        await _tiktokLogin.LogoutAsync();
                        break;

                    case "twitch":
                    case "kick":
                        // OAuth token is cleared from DB by TwitchAuthService/KickAuthService.
                        // No WebView2 cookies to clear — auth happens in the system browser.
                        break;
                }
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "{Platform}: session clear failed", platform);
                tcs.SetResult();
            }
        });
        return tcs.Task;
    }

    private async void ExitApp()
    {
        _tray.Visible = false;
        _logViewer?.Hide();
        _mainWindow?.Hide();

        // Stop playback before shutting down
        try
        {
            var userContext = Host.Services.GetService<MusicBot.Services.UserContextManager>();
            if (userContext != null)
            {
                var services = userContext.GetOrCreate(MusicBot.LocalUser.Id);
                if (services.Player.IsPlaying)
                    await services.Player.StopAsync();
            }
        }
        catch { /* best effort */ }

        _tiktokLogin?.ForceClose();

        // Cleanup old logs and orphaned music files before stopping the host
        CleanupOldLogs();
        await CleanupOrphanedMusicFilesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try   { await Host.StopAsync(cts.Token); }
        catch { /* ignore shutdown errors */ }

        Shutdown();
    }

    /// <summary>Deletes log files older than 7 days from the logs/ directory.</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logsDir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.GetFiles(logsDir, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    try { File.Delete(file); } catch { }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Deletes files in the music-library directory that are NOT referenced by
    /// any CachedTrack in the database (orphaned downloads left by crashes or bugs).
    /// </summary>
    private async Task CleanupOrphanedMusicFilesAsync()
    {
        try
        {
            var settings = Host.Services.GetService<MusicLibrarySettings>();
            var libPath  = settings?.LibraryPath;
            if (string.IsNullOrEmpty(libPath) || !Directory.Exists(libPath)) return;

            using var scope = Host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicBotDbContext>();
            var knownPaths = (await db.CachedTracks.Select(t => t.FilePath).ToListAsync())
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(libPath))
            {
                if (!knownPaths.Contains(file))
                    try { File.Delete(file); } catch { }
            }
        }
        catch { /* best effort */ }
    }

    // ── Tray icon ─────────────────────────────────────────────────────────────

    private NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };
        menu.BackColor = Color.FromArgb(22, 27, 34);
        menu.ForeColor = Color.FromArgb(200, 210, 220);

        menu.Items.Add(new ToolStripMenuItem("♫  MusicBot")
        {
            Enabled   = false,
            Font      = new WFFont("Segoe UI", 9f, WFFontStyle.Bold),
            ForeColor = Color.FromArgb(140, 100, 240),
        });
        menu.Items.Add(new ToolStripSeparator());
        AddMenuItem(menu, "🌐  Abrir dashboard", () => ShowMainWindow());
        AddMenuItem(menu, "📋  Ver logs",          () => ShowLogs());
        menu.Items.Add(new ToolStripSeparator());
        AddMenuItem(menu, "✕  Salir",               () => ExitApp());

        var tray = new NotifyIcon
        {
            Icon             = CreateIcon(),
            Text             = "MusicBot",
            Visible          = true,
            ContextMenuStrip = menu,
        };

        tray.DoubleClick       += (_, _) => Safe(ShowMainWindow);
        tray.BalloonTipClicked += (_, _) => Safe(ShowMainWindow);

        tray.ShowBalloonTip(3_000, "MusicBot iniciado",
            "Doble clic para abrir el dashboard · Clic derecho para opciones",
            ToolTipIcon.Info);

        return tray;
    }

    private static void AddMenuItem(ContextMenuStrip menu, string text, Action action)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Color.FromArgb(200, 210, 220) };
        item.Click += (_, _) => Safe(action);
        menu.Items.Add(item);
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            SysWin.MessageBox.Show(ex.Message, "MusicBot — Error",
                SysWin.MessageBoxButton.OK, SysWin.MessageBoxImage.Warning);
        }
    }

    // ── Icon ──────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    internal static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            using var bg = new SolidBrush(Color.FromArgb(124, 58, 237));
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var font  = new WFFont("Segoe UI Emoji", 15f, WFFontStyle.Bold);
            using var brush = new SolidBrush(Color.White);
            var sz = g.MeasureString("♫", font);
            g.DrawString("♫", font, brush,
                (32 - sz.Width) / 2 - 1,
                (32 - sz.Height) / 2);
        }

        var hicon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hicon).Clone();
        DestroyIcon(hicon);
        return icon;
    }
}

// ── Dark context menu theme ────────────────────────────────────────────────────

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? Color.FromArgb(200, 210, 220)
            : Color.FromArgb(80, 90, 100);
        base.OnRenderItemText(e);
    }
}

internal sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color MenuItemSelected              => Color.FromArgb(48, 54, 61);
    public override Color MenuItemBorder                => Color.FromArgb(48, 54, 61);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(48, 54, 61);
    public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(48, 54, 61);
    public override Color MenuBorder                    => Color.FromArgb(48, 54, 61);
    public override Color ToolStripDropDownBackground   => Color.FromArgb(22, 27, 34);
    public override Color ImageMarginGradientBegin      => Color.FromArgb(22, 27, 34);
    public override Color ImageMarginGradientMiddle     => Color.FromArgb(22, 27, 34);
    public override Color ImageMarginGradientEnd        => Color.FromArgb(22, 27, 34);
    public override Color SeparatorDark                 => Color.FromArgb(48, 54, 61);
    public override Color SeparatorLight                => Color.FromArgb(48, 54, 61);
}
