using Classify.Interop;
using Classify.UI;

namespace Classify.Services;

/// <summary>系统控制指令：音量 / 静音 / 锁屏 / 熄屏 / 重启 / 关机（均有本地开关约束）。</summary>
public sealed class CommandService
{
    public static CommandService Instance { get; } = new();

    /// <summary>执行一条系统指令；返回是否实际执行成功（用于给教师端回执）。</summary>
    public async Task<bool> ExecuteAsync(string name, string? value)
    {
        var s = SettingsService.Instance.Values;
        App.Log.Info($"执行系统指令 {name} value={value}");
        try
        {
            switch (name)
            {
                case "set_volume":
                    if (!s.AllowVolume) return false;
                    return Audio.SetMasterVolume(double.TryParse(value, out var v) ? v : 60);
                case "mute":
                    if (!s.AllowVolume) return false;
                    return Audio.SetMute(true);
                case "lock":
                    if (!s.AllowLock) return false;
                    return Win32.LockWorkStation();
                case "screen_off":
                    if (!s.AllowLock) return false;
                    Win32.SendMessageW(Win32.HWND_BROADCAST, Win32.WM_SYSCOMMAND,
                        (IntPtr)Win32.SC_MONITORPOWER, (IntPtr)2);
                    return true;
                case "restart":
                    if (!s.AllowPower)
                    {
                        _ = App.Ui.BeginInvoke(new Action(() =>
                            ToastWindow.ShowToast("未开启远程重启",
                                "出于安全考虑此功能默认关闭，可在 设置 → 权限与隐私 中开启")));
                        return false;
                    }
                    await Task.Delay(500);
                    if (PowerOps.Restart()) return true;
                    App.Log.Error("重启指令未生效（缺权限？）", null);
                    return false;
                case "shutdown":
                    if (!s.AllowPower)
                    {
                        _ = App.Ui.BeginInvoke(new Action(() =>
                            ToastWindow.ShowToast("未开启远程关机",
                                "出于安全考虑此功能默认关闭，可在 设置 → 权限与隐私 中开启")));
                        return false;
                    }
                    await Task.Delay(500);
                    if (PowerOps.Shutdown()) return true;
                    App.Log.Error("关机指令未生效（缺权限？）", null);
                    return false;
                default:
                    App.Log.Error($"未知指令 {name}");
                    return false;
            }
        }
        catch (Exception ex)
        {
            App.Log.Error($"执行指令 {name} 失败", ex);
            return false;
        }
    }
}
