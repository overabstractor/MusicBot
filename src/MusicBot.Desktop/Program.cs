using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
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

    // Set after a Velopack update is downloaded. Consumed by the update dialog
    // when the user clicks "Actualizar ahora" — not applied automatically on exit.
    private static UpdateManager? _pendingMgr;
    private static Velopack.VelopackAsset? _pendingRelease;

    /// <summary>
    /// Queues the downloaded update to be applied when the process exits.
    /// Called only when the user explicitly confirms the update dialog.
    /// </summary>
    public static void QueuePendingUpdate()
    {
        if (_pendingMgr != null && _pendingRelease != null)
            // restart: false — Velopack applies the update silently on exit but does NOT
            // relaunch the process. Relaunching via Velopack's Update.exe terminates
            // WebView2 runtime processes system-wide, which crashes other apps (Teams)
            // that share the same Evergreen WebView2 runtime. The user opens MusicBot
            // manually after the update is applied.
            _pendingMgr.WaitExitThenApplyUpdates(_pendingRelease, silent: true, restart: false);
    }

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // Si fuimos invocados como hook de Velopack (--velopack-install,
        // --velopack-updated, etc.) salir aquí. Algunas versiones de Velopack
        // no llaman Environment.Exit() después de procesar el hook, lo que dejaba
        // el proceso siguiendo con todo el startup completo (Kestrel, WPF, chequeo
        // de WebView2). En contexto de installer eso bloqueaba el hook hasta el
        // timeout y rompía la instalación con "install hook failed".
        if (args.Length > 0 && IsVelopackHookArg(args[0]))
            return;

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

            // ── User-overridable settings file (Desktop is the entry point, so
            // path resolution lives here, not in the shared WebHost library). ──
            // Loaded as the LAST JSON config source so its values override the
            // bundled appsettings.json defaults. Lives in %AppData%\MusicBot\ so
            // it survives dotnet rebuilds (which overwrite bin/appsettings.json)
            // and Velopack updates (which wipe the install dir).
            //
            // We mount it via an explicit PhysicalFileProvider anchored to the
            // user data directory — NOT via AddJsonFile(absolutePath) — because
            // the default file provider is rooted at ContentRoot, which varies
            // depending on how the app is launched (dotnet run vs .exe vs VS)
            // and would silently ignore absolute paths in some scenarios.
            var userDataDir = builder.Configuration["DataDirectory"]
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MusicBot");
            Directory.CreateDirectory(userDataDir);
            var userConfigProvider = new PhysicalFileProvider(userDataDir);
            builder.Configuration.AddJsonFile(
                userConfigProvider,
                "appsettings.user.json",
                optional: true,
                reloadOnChange: true);
            Log.Information("Configuración de usuario montada desde {Path}",
                Path.Combine(userDataDir, "appsettings.user.json"));

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

    /// <summary>
    /// Detecta cualquier argumento usado por Velopack para invocar la app como hook
    /// del ciclo de vida de instalación: --velopack-* (versiones nuevas), --veloapp-*
    /// y --squirrel-* (compatibilidad histórica).
    /// </summary>
    private static bool IsVelopackHookArg(string arg)
    {
        var a = arg.ToLowerInvariant();
        return a.StartsWith("--velopack-") || a.StartsWith("--veloapp-") || a.StartsWith("--squirrel-");
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
            if (!mgr.IsInstalled)
            {
                await CheckPortableUpdateAsync();
                return;
            }

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

            // Store for later — only applied if the user confirms in the dialog.
            _pendingMgr     = mgr;
            _pendingRelease = updateInfo.TargetFullRelease;
            Log.Information("Actualización {Version} descargada. Esperando confirmación del usuario.", newVersion);

            var notes = await FetchReleaseNotesAsync(newVersion);
            MusicBot.AppEvents.NotifyUpdateReadyWithNotes(newVersion, notes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "No se pudo verificar actualizaciones");
        }
    }

    private static async Task CheckPortableUpdateAsync()
    {
        try
        {
            Log.Information("Modo portable: verificando actualizaciones vía GitHub Releases…");
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MusicBot");
            http.Timeout = TimeSpan.FromSeconds(10);

            var json = await http.GetStringAsync(
                "https://api.github.com/repos/overabstractor/MusicBot/releases/latest");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var notes   = root.GetProperty("body").GetString() ?? "";

            var zipUrl  = "";
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(tagName)) return;

            var currentVersionStr = MusicBot.AppInfo.Version;
            if (currentVersionStr == "dev") return;

            if (!NuGet.Versioning.SemanticVersion.TryParse(tagName, out var latestVersion)) return;
            if (!NuGet.Versioning.SemanticVersion.TryParse(currentVersionStr, out var currentVersion)) return;
            if (latestVersion <= currentVersion)
            {
                Log.Information("Modo portable: MusicBot está actualizado ({Version})", currentVersionStr);
                return;
            }

            Log.Information("Modo portable: nueva versión {Latest} disponible (actual: {Current})",
                tagName, currentVersionStr);
            MusicBot.AppEvents.NotifyPortableUpdateAvailable(tagName, notes, zipUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "No se pudo verificar actualizaciones (modo portable)");
        }
    }

    private static async Task<string> FetchReleaseNotesAsync(string version)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MusicBot");
            http.Timeout = TimeSpan.FromSeconds(10);
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/overabstractor/MusicBot/releases/tags/v{version}");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("body").GetString() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
