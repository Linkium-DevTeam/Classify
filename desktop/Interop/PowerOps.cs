using System.Runtime.InteropServices;

namespace Classify.Interop;

/// <summary>
/// 关机/重启：直接走 Win32 InitiateSystemShutdownEx（先启用 SeShutdownPrivilege），
/// 不经任何子进程与命令行。
/// </summary>
public static class PowerOps
{
    private const string SeShutdownPrivilege = "SeShutdownPrivilege";
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;
    private const int TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const int TOKEN_QUERY = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool InitiateSystemShutdownEx(string? machineName, string message,
        uint timeoutSeconds, bool forceAppsClosed, bool rebootAfterShutdown, uint reason);

    private static bool EnableShutdownPrivilege()
    {
        try
        {
            if (!LookupPrivilegeValue(null, SeShutdownPrivilege, out var luid)) return false;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
            try
            {
                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
                };
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    /// <summary>重启（5 秒倒计时，强制关闭应用）。返回是否已发起。</summary>
    public static bool Restart(uint timeoutSeconds = 5)
    {
        EnableShutdownPrivilege();
        try
        {
            return InitiateSystemShutdownEx(null, "教师发起了重启指令", timeoutSeconds, true, true, SHTDN_REASON_FLAG_PLANNED);
        }
        catch (Exception ex) { App.Log.Error("重启失败", ex); return false; }
    }

    /// <summary>关机（5 秒倒计时，强制关闭应用）。返回是否已发起。</summary>
    public static bool Shutdown(uint timeoutSeconds = 5)
    {
        EnableShutdownPrivilege();
        try
        {
            return InitiateSystemShutdownEx(null, "教师发起了关机指令", timeoutSeconds, true, false, SHTDN_REASON_FLAG_PLANNED);
        }
        catch (Exception ex) { App.Log.Error("关机失败", ex); return false; }
    }
}
