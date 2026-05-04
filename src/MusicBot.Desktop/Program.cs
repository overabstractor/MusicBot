using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace MusicBot.Desktop;

public static class Program
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]   private static extern bool  ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]   private static extern bool  SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]   private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")]   private static extern bool  IsIconic(IntPtr hWnd);

    /// <summary>
    /// Sets a fixed AppUserModelID for this process so Windows and audio routers
    /// (Mixline, etc.) always identify MusicBot as the same application regardless
    /// of executable path, PID, or whether running in debug or release mode.
    /// </summary>
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    // Held for the lifetime of the process to enforce single-instance
    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // ── Single instance guard ─────────────────────────────────────────────
        _instanceMutex = new Mutex(initiallyOwned: true, @"Global\MusicBot.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is running — bring its window to the foreground and exit
            var hWnd = FindWindow(null, "MusicBot");
            if (hWnd != IntPtr.Zero)
            {
                if (IsIconic(hWnd)) ShowWindow(hWnd, 9); // SW_RESTORE
                SetForegroundWindow(hWnd);
            }
            _instanceMutex.Dispose();
            return;
        }

        // Fix process identity for audio routers and taskbar grouping
        SetCurrentProcessExplicitAppUserModelID("MusicBot.Player");

        // Raise process priority so the NAudio playback thread isn't starved
        // by other apps (OBS, browser, etc.) under typical streaming load.
        // AboveNormal is safe — High/Realtime can interfere with system responsiveness.
        System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
            System.Diagnostics.ProcessPriorityClass.AboveNormal;

        // Hide any residual console window immediately
        ShowWindow(GetConsoleWindow(), 0);

        // Bootstrap Serilog before the host is built
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile(
                $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json",
                optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Logs go to %AppData%\MusicBot\logs\ so they survive Velopack updates
        // and don't block the installer from removing the app directory.
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MusicBot", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDir, "musicbot-.log"),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(new LogSink())   // feeds the WPF log viewer in real time
            .CreateLogger();

        // ── Version + auto-update ─────────────────────────────────────────────
        // Set the version string immediately (used by the API /version endpoint).
        // Then kick off the update check in the background — it never blocks startup.
        SetVersionAndScheduleUpdateCheck();

        try
        {
            Log.Information("Iniciando MusicBot");

            // ── WebView2 check ────────────────────────────────────────────────
            // Must run before WPF starts. If WebView2 is missing it installs it
            // silently and relaunches the process, so we exit here.
            if (!WebView2Setup.EnsureInstalled())
                return;

            // ── Build web host ────────────────────────────────────────────────
            // CreateBuilder registers all application services.
            // We inject TrayLifetime before building so the host never allocates
            // a console window — the WPF message loop owns application lifetime.
            var builder = WebHost.CreateBuilder(args);
            builder.Services.Replace(
                ServiceDescriptor.Singleton<IHostLifetime, TrayLifetime>());

            var webApp = WebHost.Configure(builder);

            // ── Start Kestrel on the thread pool ─────────────────────────────
            var hostTask = webApp.RunAsync();

            // ── Start WPF on the STA thread ───────────────────────────────────
            var wpfApp = new App { Host = webApp };
            wpfApp.Run();

            // Wait for the host to stop cleanly after WPF exits
            hostTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "La aplicación terminó de forma inesperada");
            System.Windows.MessageBox.Show(
                $"Error fatal al iniciar MusicBot:\n\n{ex.Message}",
                "MusicBot — Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            Log.CloseAndFlush();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
    }

    private static string FormatVersion(NuGet.Versioning.SemanticVersion v)
        => $"{v.Major}.{v.Minor}.{v.Patch}";

    private static void SetVersionAndScheduleUpdateCheck()
    {
        var mgr = new UpdateManager(
            new GithubSource("https://github.com/overabstractor/MusicBot", null, false));

        if (mgr.IsInstalled && mgr.CurrentVersion != null)
            MusicBot.AppInfo.SetVersion(FormatVersion(mgr.CurrentVersion));

        Log.Information("MusicBot {Version}", MusicBot.AppInfo.Version);

        _ = Task.Run(() => CheckAndDownloadUpdateAsync(mgr));
    }

    private static async Task CheckAndDownloadUpdateAsync(UpdateManager mgr)
    {
        try
        {
            if (!mgr.IsInstalled) return;

            Log.Information("Verificando actualizaciones…");
            var updateInfo = await mgr.CheckForUpdatesAsync();
            if (updateInfo == null)
            {
                Log.Information("MusicBot está actualizado ({Version})", MusicBot.AppInfo.Version);
                return;
            }

            var newVersion = FormatVersion(updateInfo.TargetFullRelease.Version);
            Log.Information("Nueva versión disponible: {Version} — descargando en segundo plano…", newVersion);

            await mgr.DownloadUpdatesAsync(updateInfo);

            Log.Information("Actualización {Version} descargada. Se aplicará al reiniciar.", newVersion);
            mgr.WaitExitThenApplyUpdates(updateInfo.TargetFullRelease, silent: true, restart: true);
            MusicBot.AppEvents.NotifyUpdateReady(newVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "No se pudo verificar actualizaciones");
        }
    }
}
