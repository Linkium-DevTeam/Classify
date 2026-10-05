using System.IO;
using System.Windows;
using System.Windows.Shell;
using Classify.Models;
using Classify.Interop;
using Classify.UI;

namespace Classify.Services;

/// <summary>接收文件：下载到桌面并弹 Toast 提示（任务栏进度）。</summary>
public sealed class FileReceiveService
{
    public static FileReceiveService Instance { get; } = new();

    private readonly ApiService _api = new();

    public async Task HandleAsync(InboxMessage msg)
    {
        try
        {
            if (string.IsNullOrEmpty(msg.FileId)) return;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dir = SettingsService.Instance.Values.SaveToDesktop
                ? desktop
                : Path.Combine(AppContext.BaseDirectory, "received");
            Directory.CreateDirectory(dir);

            string baseName = Path.GetFileNameWithoutExtension(msg.FileName);
            string ext = Path.GetExtension(msg.FileName);
            string dest = Path.Combine(dir, msg.FileName);
            for (int i = 1; File.Exists(dest); i++)
                dest = Path.Combine(dir, $"{baseName} ({i}){ext}");

            ReceiptService.Send(msg.Mid, "received");
            App.Log.Info($"开始下载文件 {msg.FileName} → {dest}");
            IntPtr hwnd = App.MainHwnd;
            await _api.DownloadFileAsync(msg.FileId, dest, pct =>
                TaskbarProgress.SetProgress(hwnd, pct));
            TaskbarProgress.Clear(hwnd);
            ReceiptService.Send(msg.Mid, "displayed");
            App.Log.Info($"文件下载完成 {msg.FileName}");

            _ = App.Ui.BeginInvoke(new Action(() =>
                ToastWindow.ShowToast("📥 收到文件",
                    $"{msg.FileName}（{msg.FileSize / 1024.0 / 1024.0:F1} MB）\n已保存到桌面")));
        }
        catch (Exception ex)
        {
            ReceiptService.Send(msg.Mid, "failed");
            App.Log.Error("接收文件失败", ex);
            _ = App.Ui.BeginInvoke(new Action(() =>
                ToastWindow.ShowToast("❌ 接收文件失败", $"{msg.FileName}\n{ex.Message}")));
        }
    }
}
