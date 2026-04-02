using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MusicBot.Services.Player;

/// <summary>
/// Sets a fixed GroupingParam GUID on all WASAPI audio sessions from this process
/// so that audio routers (Mixline, VoiceMeeter, etc.) always identify MusicBot
/// as the same application across restarts and debug sessions.
/// </summary>
internal static class AudioSessionHelper
{
    /// <summary>
    /// Fixed GroupingParam GUID. Audio routers group sessions that share the same
    /// GroupingParam and remember routing rules for that group across restarts.
    /// </summary>
    public static readonly Guid MusicBotGroupingGuid = new("5A3F1C2D-8E4B-4F9A-B621-3D7E5A2C1F08");

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
    /// Iterates all WASAPI sessions on the device belonging to this process
    /// and sets their GroupingParam to <see cref="MusicBotGroupingGuid"/>.
    /// Call after WasapiOut.Play() to ensure the session is registered.
    /// </summary>
    public static void ApplyGroupingParam(MMDevice device)
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

                SetGroupingParamOnSession(session, ref gp, ref ctx);
            }
        }
        catch { /* best-effort, never throw */ }
    }

    private static void SetGroupingParamOnSession(AudioSessionControl session, ref Guid gp, ref Guid ctx)
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

        // QueryInterface for our IAudioSessionControl (same IID) to access SetGroupingParam
        IntPtr pUnk = IntPtr.Zero;
        try
        {
            pUnk = Marshal.GetIUnknownForObject(rawCom);
            var iid = typeof(IAudioSessionControl).GUID;
            if (Marshal.QueryInterface(pUnk, ref iid, out IntPtr pIface) != 0) return;

            try
            {
                var iface = (IAudioSessionControl)Marshal.GetObjectForIUnknown(pIface);
                iface.SetGroupingParam(ref gp, ref ctx);
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
