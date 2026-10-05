using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Classify.Services;

/// <summary>
/// 硬件指纹设备 ID：MachineGuid + 主板序列号 + 机器名 做 SHA256，
/// 取 16 位大写去易混字符编码。重装系统/应用后保持不变（MachineGuid 随系统保留），
/// 用于设备身份稳定与支持请求定位。
/// </summary>
public static class HwIdService
{
    private static string? _cached;

    public static string Get()
    {
        if (_cached != null) return _cached;
        try
        {
            var mguid = (Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid", "") as string) ?? "";
            var board = "";
            try
            {
                using var q = new System.Management.ManagementObjectSearcher(
                    "SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (var o in q.Get())
                {
                    board = o["SerialNumber"]?.ToString() ?? "";
                    break;
                }
            }
            catch { }
            var sha = SHA256.HashData(Encoding.UTF8.GetBytes(mguid + "|" + board + "|" + Environment.MachineName));
            const string alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789"; // 去易混 I L O 0 1 U
            var sb = new StringBuilder(16);
            foreach (var b in sha)
            {
                if (sb.Length >= 16) break;
                sb.Append(alphabet[b % alphabet.Length]);
            }
            _cached = sb.ToString();
        }
        catch
        {
            // 兜底：随机一次性 ID（极少发生；此时退化为普通随机设备）
            _cached = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        }
        return _cached;
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
