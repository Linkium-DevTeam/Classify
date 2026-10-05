using Microsoft.Win32;

namespace Classify.Services;

/// <summary>
/// 讲述人自然语音桥接：把 Windows「讲述人」下载的自然语音（如 晓晓 Natural HD）
/// 的注册表令牌从 Speech_OneCore 复制到 SAPI5 体系，使经典 SAPI 引擎
/// （System.Speech）能够加载与讲述人完全同款的语音。
/// SAPI5 枚举语音时同时查找 HKLM 与 HKCU 的 Tokens，因此写入 HKCU 即可生效，
/// 全程托管注册表复制——无提权、无子进程、无命令行拼接。
/// </summary>
public static class SapiVoiceBridge
{
    private const string OneCorePath = @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens";
    private const string SapiMachinePath = @"SOFTWARE\Microsoft\Speech\Voices\Tokens";
    private const string SapiUserPath = @"Software\Microsoft\Speech\Voices\Tokens";

    /// <summary>两个 SAPI 位置都缺少、但 OneCore 里存在的语音令牌名。</summary>
    public static List<string> MissingTokens()
    {
        var result = new List<string>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var one = root.OpenSubKey(OneCorePath);
            if (one is null) return result;

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var sapiM = root.OpenSubKey(SapiMachinePath))
                foreach (var n in sapiM?.GetSubKeyNames() ?? Array.Empty<string>())
                    existing.Add(n);
            using (var sapiU = Registry.CurrentUser.OpenSubKey(SapiUserPath))
                foreach (var n in sapiU?.GetSubKeyNames() ?? Array.Empty<string>())
                    existing.Add(n);

            foreach (var name in one.GetSubKeyNames())
                if (!existing.Contains(name)) result.Add(name);
        }
        catch (Exception ex)
        {
            App.Log.Error("枚举 OneCore 语音令牌失败", ex);
        }
        return result;
    }

    public static bool IsAvailable() => MissingTokens().Count > 0;

    /// <summary>复制缺失令牌到 HKCU 的 SAPI5 位置（用户级，无需管理员）。返回新增数量。</summary>
    public static int CopyMissingTokens()
    {
        var missing = MissingTokens();
        if (missing.Count == 0) return 0;
        var copied = 0;
        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var one = root.OpenSubKey(OneCorePath))
        {
            if (one is null) return 0;
            using var sapiU = Registry.CurrentUser.CreateSubKey(SapiUserPath, writable: true);
            if (sapiU is null) return 0;
            foreach (var name in missing)
            {
                using var src = one.OpenSubKey(name);
                if (src is null) continue;
                // 已存在则跳过（并发点击等）；DeleteSubKeyTree 重写亦可，跳过更保守
                using var probe = sapiU.OpenSubKey(name);
                if (probe != null) continue;
                CopyTree(src, sapiU.CreateSubKey(name, writable: true));
                copied++;
            }
        }
        if (copied > 0)
            App.Log.Info($"自然语音桥接：已复制 {copied} 个语音令牌到 HKCU（讲述人自然语音）");
        return copied;
    }

    private static void CopyTree(RegistryKey src, RegistryKey dst)
    {
        using (dst)
        {
            foreach (var name in src.GetValueNames())
            {
                var val = src.GetValue(name, null);
                if (val is null) continue;
                try { dst.SetValue(name, val, src.GetValueKind(name)); }
                catch { /* 个别非常规值类型跳过 */ }
            }
            foreach (var sub in src.GetSubKeyNames())
            {
                using var s = src.OpenSubKey(sub);
                if (s != null) CopyTree(s, dst.CreateSubKey(sub, writable: true));
            }
        }
    }
}
