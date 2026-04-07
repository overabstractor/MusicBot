using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MusicBot.Services.Player;

/// <summary>
/// Configures WASAPI audio sessions so audio routers (Logitech G HUB, Mixline,
/// VoiceMeeter, etc.) always see MusicBot as a single, stable application entry
/// across restarts, song changes, and Velopack version updates.
///
/// Root cause of the "multiple instances" problem:
///   WasapiOut without an explicit audioSessionGuid creates a new WASAPI session
///   for every song. Each new session registers as a separate entry in audio mixers.
///
/// Fix: pass <see cref="MusicBotSessionGuid"/> to the WasapiOut constructor so
///   WASAPI always reuses the same session. The GroupingParam is set as a secondary
///   safeguard. The DisplayName is pinned to "MusicBot" so it never shows the
///   exe path (which changes on every Velopack update).
/// </summary>
internal static class AudioSessionHelper
{
    /// <summary>
    /// Fixed GUID passed as the <c>audioSessionGuid</c> parameter of WasapiOut.
    /// WASAPI reuses the same audio session for every instance that shares this GUID,
    /// so audio routers see exactly one "MusicBot" entry regardless of how many songs
    /// have been played or how many times the app has been restarted.
    /// </summary>
    public static readonly Guid MusicBotSessionGuid = new("5A3F1C2D-8E4B-4F9A-B621-3D7E5A2C1F08");

    /// <summary>
    /// GroupingParam GUID (same value — sessions sharing both the session GUID and the
    /// GroupingParam are guaranteed to appear as one logical unit in all compliant mixers).
    /// </summary>
    public static readonly Guid MusicBotGroupingGuid = MusicBotSessionGuid;

    // IAudioSessionControl IID — same as NAudio's internal interface
    [ComImport]
    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
    }

    /// <summary>
    /// Iterates all WASAPI sessions on <paramref name="device"/> belonging to this
    /// process and pins their GroupingParam and DisplayName.  This is a secondary
    /// safeguard — the primary fix is passing <see cref="MusicBotSessionGuid"/> to
    /// the WasapiOut constructor so WASAPI never creates a new session to begin with.
    /// </summary>
    public static void ApplySessionMetadata(MMDevice device)
    {
        try
        {
            var pid      = (uint)Environment.ProcessId;
            var sessions = device.AudioSessionManager.Sessions;
            var gp       = MusicBotGroupingGuid;
            var ctx      = Guid.Empty;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.GetProcessID != pid) continue;
                ApplyOnSession(session, ref gp, ref ctx);
            }
        }
        catch { /* best-effort, never throw */ }
    }

    private static void ApplyOnSession(AudioSessionControl session, ref Guid gp, ref Guid ctx)
    {
        // Locate the private field that holds NAudio's internal IAudioSessionControl COM object.
        // The field name varies by NAudio version (commonly "ctl" or "audioSessionControl").
        object? rawCom = null;
        foreach (var field in typeof(AudioSessionControl)
                     .GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var val = field.GetValue(session);
            if (val == null || !val.GetType().IsCOMObject) continue;
            rawCom = val;
            break;
        }

        if (rawCom == null) return;

        IntPtr pUnk = IntPtr.Zero;
        try
        {
            pUnk = Marshal.GetIUnknownForObject(rawCom);
            var iid = typeof(IAudioSessionControl).GUID;
            if (Marshal.QueryInterface(pUnk, in iid, out IntPtr pIface) != 0) return;

            try
            {
                var iface = (IAudioSessionControl)Marshal.GetObjectForIUnknown(pIface);
                iface.SetGroupingParam(ref gp, ref ctx);
                iface.SetDisplayName("MusicBot", ref ctx);  // prevent exe-path default (changes per Velopack update)
                Marshal.ReleaseComObject(iface);
            }
            finally { Marshal.Release(pIface); }
        }
        finally
        {
            if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
        }
    }
}
