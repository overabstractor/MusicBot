using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;
using Velopack;

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

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new LogSink())   // feeds the WPF log viewer in real time
            .CreateLogger();

        try
        {
            Log.Information("Iniciando MusicBot v1.0");

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
}
