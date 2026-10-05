using System.Runtime.InteropServices;

namespace Classify.Interop;

/// <summary>
/// 关闭 Windows 11 的 EcoQoS 效率模式节流：
/// 主窗口隐藏到托盘后，系统会把进程降为"效率模式"拖慢轮询定时器，
/// 这里显式声明全速运行，保证喊话/心跳及时。
/// </summary>
public static class PowerThrottling
{
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint VersionMask;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int infoClass,
        ref PROCESS_POWER_THROTTLING_STATE info, int size);

    public static void DisableEcoQoS()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                VersionMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0, // 0 = 不进入效率模式（全速）
            };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { /* 不支持则忽略 */ }
    }
}
