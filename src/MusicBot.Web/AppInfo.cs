namespace MusicBot;

/// <summary>
/// Runtime app metadata set by the Desktop layer at startup.
/// Accessible from any layer without a Velopack dependency.
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// Current installed version (e.g. "1.2.0").
    /// Set by Program.cs from UpdateManager.CurrentVersion.
    /// Defaults to "dev" when running outside a Velopack installation.
    /// </summary>
    public static string Version { get; private set; } = "dev";

    public static void SetVersion(string version) => Version = version;
}
