using System.Runtime.InteropServices;

namespace Classify.Interop;

/// <summary>任务栏进度条（ITaskbarList3）：文件下载时在任务栏图标上显示进度。</summary>
public static class TaskbarProgress
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90cd-2e52056f6b05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private sealed class TaskbarListCom { }

    private const int TBPF_NOPROGRESS = 0x0;
    private const int TBPF_NORMAL = 0x2;

    private static ITaskbarList3? _taskbar;
    private static readonly object Gate = new();

    private static ITaskbarList3? Get()
    {
        if (_taskbar is null)
        {
            try
            {
                _taskbar = (ITaskbarList3)(object)new TaskbarListCom();
                _taskbar.HrInit();
            }
            catch
            {
                _taskbar = null;
            }
        }
        return _taskbar;
    }

    public static void SetProgress(IntPtr hwnd, int percent)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (Gate)
        {
            var tb = Get();
            if (tb is null) return;
            try
            {
                tb.SetProgressState(hwnd, TBPF_NORMAL);
                tb.SetProgressValue(hwnd, (ulong)Math.Clamp(percent, 0, 100), 100);
            }
            catch { }
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appid);

    /// <summary>未打包应用设置 AUMID（JumpList / 任务栏分组需要）。</summary>
    public static void SetAumid(string aumid)
    {
        try { SetCurrentProcessExplicitAppUserModelID(aumid); } catch { }
    }

    public static void Clear(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (Gate)
        {
            var tb = Get();
            if (tb is null) return;
            try { tb.SetProgressState(hwnd, TBPF_NOPROGRESS); } catch { }
        }
    }
}
