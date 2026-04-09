using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;

namespace MusicBot.Desktop;

/// <summary>
/// Checks for the WebView2 Evergreen runtime and installs it silently if missing.
/// Uses the official Microsoft bootstrapper (~1.5 MB) which installs per-user
/// without requiring elevation.
/// </summary>
internal static class WebView2Setup
{
    // Microsoft's official redirect URL for the WebView2 Evergreen Bootstrapper
    private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static bool IsInstalled()
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// If WebView2 is not installed, prompts the user and installs it silently.
    /// Returns false if the user declined or installation failed (caller should exit).
    /// Returns true if WebView2 is (or becomes) available.
    /// </summary>
    public static bool EnsureInstalled()
    {
        if (IsInstalled()) return true;

        Serilog.Log.Warning("WebView2 runtime no encontrado — iniciando instalación automática");

        var consent = System.Windows.MessageBox.Show(
            "MusicBot requiere el runtime de WebView2 (el motor de navegador integrado de Microsoft Edge).\n\n" +
            "¿Descargar e instalar ahora? Es gratis, pesa ~2 MB y no requiere reiniciar el sistema.",
            "MusicBot — WebView2 requerido",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information);

        if (consent != System.Windows.MessageBoxResult.Yes)
        {
            Serilog.Log.Information("Usuario canceló la instalación de WebView2");
            return false;
        }

        bool installed = false;
        try
        {
            installed = DownloadAndInstall();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error al instalar WebView2");
        }

        if (!installed || !IsInstalled())
        {
            System.Windows.MessageBox.Show(
                "No se pudo instalar WebView2 automáticamente.\n\n" +
                "Descárgalo manualmente desde:\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                "MusicBot — Error al instalar WebView2",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return false;
        }

        Serilog.Log.Information("WebView2 instalado correctamente — reiniciando MusicBot");

        // Restart so WebView2 initializes cleanly from a fresh process
        var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return false; // caller must exit current process
    }

    private static bool DownloadAndInstall()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
        try
        {
            Serilog.Log.Information("Descargando WebView2 bootstrapper desde Microsoft…");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            // Blocking download on the STA thread is intentional here — WPF hasn't started yet
            var bytes = http.GetByteArrayAsync(BootstrapperUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(tempFile, bytes);

            Serilog.Log.Information("Bootstrapper descargado ({Bytes} bytes) — ejecutando instalación…", bytes.Length);

            using var proc = Process.Start(new ProcessStartInfo(tempFile, "/silent /install")
            {
                UseShellExecute = true,
            })!;
            proc.WaitForExit();

            Serilog.Log.Information("WebView2 bootstrapper terminó con código {Code}", proc.ExitCode);
            return proc.ExitCode == 0;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
