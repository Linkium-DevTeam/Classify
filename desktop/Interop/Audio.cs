using System.Runtime.InteropServices;

namespace Classify.Interop;

/// <summary>系统主音量控制（IAudioEndpointVolume COM 互操作）。返回 -1 表示没有可用输出端点（远程会话等）。</summary>
public static class Audio
{
    public static double GetMasterVolume()
    {
        try
        {
            using var ep = GetEndpoint();
            if (ep is null) return -1;
            ep.GetMasterVolumeLevelScalar(out float v);
            return v * 100.0;
        }
        catch { return -1; }
    }

    public static bool SetMasterVolume(double percent)
    {
        try
        {
            using var ep = GetEndpoint();
            if (ep is null) return false;
            ep.SetMasterVolumeLevelScalar((float)Math.Clamp(percent, 0, 100) / 100f, Guid.Empty);
            return true;
        }
        catch (Exception ex) { App.Log.Error("设置音量失败", ex); return false; }
    }

    public static bool SetMute(bool mute)
    {
        try
        {
            using var ep = GetEndpoint();
            if (ep is null) return false;
            ep.SetMute(mute, Guid.Empty);
            return true;
        }
        catch (Exception ex) { App.Log.Error("设置静音失败", ex); return false; }
    }

    private static EndpointVolume? GetEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev);
        if (dev is null) return null;
        dev.Activate(typeof(IAudioEndpointVolume).GUID, 0 /*CLSCTX_ALL*/, IntPtr.Zero, out object obj);
        return new EndpointVolume(obj);
    }

    private sealed class EndpointVolume : IDisposable
    {
        private readonly IAudioEndpointVolume _inner;
        public EndpointVolume(object o) => _inner = (IAudioEndpointVolume)o;
        public void GetMasterVolumeLevelScalar(out float v) => _inner.GetMasterVolumeLevelScalar(out v);
        public void SetMasterVolumeLevelScalar(float v, Guid ctx) => _inner.SetMasterVolumeLevelScalar(v, ctx);
        public void SetMute(bool m, Guid ctx) => _inner.SetMute(m, ctx);
        public void Dispose() { if (_inner is IDisposable d) d.Dispose(); }
    }

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

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, Guid ctx);
        int SetMasterVolumeLevelScalar(float level, Guid ctx);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint idx, float level, Guid ctx);
        int SetChannelVolumeLevelScalar(uint idx, float level, Guid ctx);
        int GetChannelVolumeLevel(uint idx, out float level);
        int GetChannelVolumeLevelScalar(uint idx, out float level);
        int SetMute(bool mute, Guid ctx);
        int GetMute(out bool mute);
    }
}
