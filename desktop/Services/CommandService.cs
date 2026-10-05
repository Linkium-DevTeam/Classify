using Classify.Interop;
using Classify.UI;

namespace Classify.Services;

/// <summary>系统控制指令：音量 / 静音 / 锁屏 / 熄屏 / 重启 / 关机（均有本地开关约束）。</summary>
public sealed class CommandService
{
    public static CommandService Instance { get; } = new();

    public async Task ExecuteAsync(string name, string? value)
    {
        var s = SettingsService.Instance.Values;
        App.Log.Info($"执行系统指令 {name} value={value}");
        try
        {
            switch (name)
            {
                case "set_volume":
                    if (!s.AllowVolume) return;
                    Audio.SetMasterVolume(double.TryParse(value, out var v) ? v : 60);
                    break;
                case "mute":
                    if (!s.AllowVolume) return;
                    Audio.SetMute(true);
                    break;
                case "lock":
                    if (!s.AllowLock) return;
                    Win32.LockWorkStation();
                    break;
                case "screen_off":
                    if (!s.AllowLock) return;
                    Win32.SendMessageW(Win32.HWND_BROADCAST, Win32.WM_SYSCOMMAND,
                        (IntPtr)Win32.SC_MONITORPOWER, (IntPtr)2);
                    break;
                case "restart":
                    if (!s.AllowPower)
                    {
                        _ = App.Ui.BeginInvoke(new Action(() =>
                            ToastWindow.ShowToast("未开启远程重启",
                                "出于安全考虑此功能默认关闭，可在 设置 → 权限与隐私 中开启")));
                        return;
                    }
                    await Task.Delay(500);
                    if (!PowerOps.Restart()) App.Log.Error("重启指令未生效（缺权限？）", null);
                    break;
                case "shutdown":
                    if (!s.AllowPower)
                    {
                        _ = App.Ui.BeginInvoke(new Action(() =>
                            ToastWindow.ShowToast("未开启远程关机",
                                "出于安全考虑此功能默认关闭，可在 设置 → 权限与隐私 中开启")));
                        return;
                    }
                    await Task.Delay(500);
                    if (!PowerOps.Shutdown()) App.Log.Error("关机指令未生效（缺权限？）", null);
                    break;
                default:
                    App.Log.Error($"未知指令 {name}");
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Log.Error($"执行指令 {name} 失败", ex);
        }
    }
}
