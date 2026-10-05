using System.Runtime.InteropServices;

namespace Classify.Interop;

/// <summary>
/// ShellExecute 封装：以系统默认方式打开文档/文件夹/网址（等价资源管理器双击语义）。
/// 供候课操作集 "open" 动作使用——操作集为教师账号在云端配置的受信内容
/// （服务端按账号隔离、候课有学科权限门）。
/// </summary>
public static class Shell
{
    private const uint SEE_MASK_DEFAULT = 0x0;
    private const int SW_SHOWNORMAL = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    /// <summary>用系统默认程序打开目标（文件/文件夹/URL）。返回是否成功发起。</summary>
    public static bool Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_DEFAULT,
                lpVerb = "open",
                lpFile = target,
                nShow = SW_SHOWNORMAL,
            };
            return ShellExecuteEx(ref info);
        }
        catch (Exception ex)
        {
            App.Log.Error($"打开目标失败：{target}", ex);
            return false;
        }
    }
}
