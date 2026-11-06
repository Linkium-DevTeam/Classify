using System.IO;
using System.Text;
using Classify.Models;
using Classify.Interop;
using Classify.UI;

namespace Classify.Services;

/// <summary>接收文件：下载到桌面（或数据目录）并弹 Toast 提示（任务栏进度）。</summary>
public sealed class FileReceiveService
{
    public static FileReceiveService Instance { get; } = new();

    private readonly ApiService _api = new();

    public async Task HandleAsync(InboxMessage msg)
    {
        string? dest = null;
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            if (string.IsNullOrEmpty(msg.FileId)) return;

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var useDesktop = SettingsService.Instance.Values.SaveToDesktop
                && !string.IsNullOrWhiteSpace(desktop)
                && Directory.Exists(desktop);
            var dir = useDesktop ? desktop : Path.Combine(AppPaths.DataDir, "received");
            Directory.CreateDirectory(dir);

            var safeName = SafeFileName(msg.FileName, $"received-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
            dest = UniquePath(dir, safeName);

            ReceiptService.Send(msg.Mid, "received");
            App.Log.Info($"开始下载文件 {msg.FileName} → {dest}");
            hwnd = App.MainHwnd;
            await _api.DownloadFileAsync(msg.FileId, dest, pct =>
                TaskbarProgress.SetProgress(hwnd, pct));
            ReceiptService.Send(msg.Mid, "displayed");
            App.Log.Info($"文件下载完成 {msg.FileName}");

            var where = useDesktop ? "桌面" : dir;
            _ = App.Ui.BeginInvoke(new Action(() =>
                ToastWindow.ShowToast("📥 收到文件",
                    $"{safeName}（{msg.FileSize / 1024.0 / 1024.0:F1} MB）\n已保存到{where}")));
        }
        catch (Exception ex)
        {
            ReceiptService.Send(msg.Mid, "failed");
            App.Log.Error("接收文件失败", ex);
            if (dest != null)
            {
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            }
            _ = App.Ui.BeginInvoke(new Action(() =>
                ToastWindow.ShowToast("❌ 接收文件失败", $"{msg.FileName}\n{ex.Message}")));
        }
        finally
        {
            if (hwnd != IntPtr.Zero) TaskbarProgress.Clear(hwnd);
        }
    }

    /// <summary>Windows 文件名安全化：去路径、非法字符、保留名、尾随点/空格，并限制长度。</summary>
    internal static string SafeFileName(string? raw, string fallback)
    {
        var name = Path.GetFileName((raw ?? "").Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return fallback;

        // CON/PRN/AUX/NUL/COM1-9/LPT1-9 是 Windows 保留设备名
        var stem = Path.GetFileNameWithoutExtension(name);
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
            "LPT8", "LPT9" };
        if (reserved.Any(r => string.Equals(stem, r, StringComparison.OrdinalIgnoreCase)))
            name = "_" + name;

        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            var keep = Math.Max(1, 120 - ext.Length);
            name = Path.GetFileNameWithoutExtension(name)[..Math.Min(keep, Path.GetFileNameWithoutExtension(name).Length)] + ext;
        }
        return name;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var fullDir = Path.GetFullPath(dir);
        var dest = Path.GetFullPath(Path.Combine(fullDir, fileName));
        if (!dest.StartsWith(fullDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            dest = Path.Combine(fullDir, "received.bin");

        var baseName = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        var candidate = dest;
        for (var i = 1; File.Exists(candidate); i++)
            candidate = Path.Combine(fullDir, $"{baseName} ({i}){ext}");
        return candidate;
    }
}
