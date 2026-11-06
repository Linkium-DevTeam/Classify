using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Classify.Services;

/// <summary>
/// 硬件指纹设备 ID：MachineGuid + 主板序列号 + 机器名 做 SHA256，
/// 取 16 位大写去易混字符编码。重装系统/应用后尽可能保持稳定（MachineGuid 随系统保留），
/// 用于设备身份稳定与支持请求定位。
/// </summary>
public static class HwIdService
{
    private static string? _cached;
    private static readonly object Gate = new();

    public static string Get()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        var id = Compute();
        lock (Gate)
        {
            _cached ??= id;
            return _cached;
        }
    }

    private static string Compute()
    {
        var mguid = "";
        try
        {
            mguid = (Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid", "") as string) ?? "";
        }
        catch { }

        // WMI 在少数教室机/还原卡环境会卡住；最多等 1.5 秒，失败就用稳定的
        // MachineGuid + 机器名摘要，绝不退化成每次启动都变化的随机 ID。
        var board = "";
        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    using var q = new System.Management.ManagementObjectSearcher(
                        "SELECT SerialNumber FROM Win32_BaseBoard");
                    foreach (var o in q.Get())
                        return o["SerialNumber"]?.ToString() ?? "";
                }
                catch { }
                return "";
            });
            if (task.Wait(TimeSpan.FromMilliseconds(1500))) board = task.Result;
        }
        catch { }

        try
        {
            var seed = (string.IsNullOrWhiteSpace(mguid)
                ? Environment.MachineName + "|" + Environment.UserName
                : mguid) + "|" + board + "|" + Environment.MachineName;
            var sha = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            const string alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789"; // 去易混 I L O 0 1 U
            var sb = new StringBuilder(16);
            foreach (var b in sha)
            {
                if (sb.Length >= 16) break;
                sb.Append(alphabet[b % alphabet.Length]);
            }
            return sb.ToString();
        }
        catch
        {
            // 极端的加密/编码异常：稳定字符串摘要兜底
            var sum = 0UL;
            foreach (var ch in Environment.MachineName + "|" + Environment.UserName)
                sum = sum * 31 + (ulong)ch;
            return sum.ToString("X16");
        }
    }

    /// <summary>确保设置中存有 HwId（首次计算后写入）。</summary>
    public static void EnsureStored()
    {
        var s = SettingsService.Instance.Values;
        if (string.IsNullOrWhiteSpace(s.HwId))
        {
            s.HwId = Get();
            SettingsService.Instance.Save();
        }
    }
}
