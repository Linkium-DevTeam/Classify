using System.Runtime.InteropServices;

namespace Classify.Interop;

/// <summary>
/// 按应用压音（真·ducking）：朗读期间把其他应用的会话音量压低，Classify 自己的
/// 朗读不受影响（走系统总音量，保持大声）。结束后逐一恢复各应用原音量。
/// 与旧的"压系统总音量"不同——那种实现会把自己也压哑。
/// </summary>
public static class AudioSessions
{
    private static readonly Dictionary<uint, float> Saved = new();
    private static readonly object Gate = new();

    /// <summary>压低其他应用音量。返回是否至少压了一个应用。</summary>
    public static bool DuckOthers(float level = 0.15f)
    {
        try
        {
            lock (Gate)
            {
                RestoreLocked();
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev);
                dev.Activate(typeof(IAudioSessionManager2).GUID, 0 /*CLSCTX_ALL*/, IntPtr.Zero, out var mgrObj);
                var mgr = (IAudioSessionManager2)mgrObj;
                mgr.GetSessionEnumerator(out var enumObj);
                var en = (IAudioSessionEnumerator)enumObj;
                en.GetCount(out var count);
                var hit = false;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        en.GetSession(i, out var ctlObj);
                        if (ctlObj is null) continue;
                        var ctl2 = (IAudioSessionControl2)ctlObj;
                        ctl2.GetProcessId(out var pid);
                        if (pid == 0 || pid == Environment.ProcessId) continue;
                        var simple = (ISimpleAudioVolume)ctlObj;
                        simple.GetMasterVolume(out var vol);
                        Saved[pid] = vol;
                        simple.SetMasterVolume(level, Guid.Empty);
                        hit = true;
                    }
                    catch { /* 单个会话失败不影响其余 */ }
                }
                if (hit) App.Log.Info($"已压低 {Saved.Count} 个其他应用的音量（ducking）");
                return hit;
            }
        }
        catch (Exception ex)
        {
            App.Log.Error("按应用压音失败", ex);
            return false;
        }
    }

    /// <summary>恢复全部被压的应用音量。</summary>
    public static void Restore()
    {
        try
        {
            lock (Gate)
            {
                RestoreLocked();
            }
        }
        catch { }
    }

    private static void RestoreLocked()
    {
        if (Saved.Count == 0) return;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDefaultAudioEndpoint(0, 1, out var dev);
            dev.Activate(typeof(IAudioSessionManager2).GUID, 0, IntPtr.Zero, out var mgrObj);
            var mgr = (IAudioSessionManager2)mgrObj;
            mgr.GetSessionEnumerator(out var enumObj);
            var en = (IAudioSessionEnumerator)enumObj;
            en.GetCount(out var count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    en.GetSession(i, out var ctlObj);
                    if (ctlObj is null) continue;
                    var ctl2 = (IAudioSessionControl2)ctlObj;
                    ctl2.GetProcessId(out var pid);
                    if (!Saved.TryGetValue(pid, out var vol)) continue;
                    var simple = (ISimpleAudioVolume)ctlObj;
                    simple.SetMasterVolume(vol, Guid.Empty);
                    Saved.Remove(pid);
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            // 已结束的会话无法恢复（其音量随进程消亡），直接清账
            Saved.Clear();
        }
    }

    /* ---- COM interop（vtable 顺序敏感，基接口方法必须完整声明） ---- */

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out ISimpleAudioVolume volume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification(IntPtr sessionGuid);
        [PreserveSig] int UnregisterDuckNotification(IntPtr sessionGuid);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl2 session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl（vtable 顺序）
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid ctx);
        [PreserveSig] int GetMute(out bool mute);
    }
}
