using System.Text;
using System.Windows;
using Classify.Models;
using Classify.UI;

namespace Classify.Services;

/// <summary>剪贴板双向同步：电脑 ↔ 教师 WebUI（WPF 剪贴板，无 WinRT）。</summary>
public static class ClipboardSyncService
{
    private static readonly ApiService Api = new();

    /// <summary>教师端发来文本 → 写入本机剪贴板 + Toast（UI 线程调用）。</summary>
    public static void ApplyFromTeacher(InboxMessage msg)
    {
        try
        {
            if (!SettingsService.Instance.Values.AllowClipboard) return;
            if (string.IsNullOrEmpty(msg.Text)) return;

            Clipboard.SetText(msg.Text);

            ToastWindow.ShowToast("📋 电脑剪贴板已更新",
                $"{(string.IsNullOrEmpty(msg.From) ? "教师" : msg.From)} 推送：{msg.Text[..Math.Min(60, msg.Text.Length)]}" +
                (msg.Text.Length > 60 ? "…" : ""));
            ReceiptService.Send(msg.Mid, "displayed");
        }
        catch (Exception ex)
        {
            App.Log.Error("应用剪贴板失败", ex);
            ReceiptService.Send(msg.Mid, "failed");
        }
    }

    /// <summary>读取本机剪贴板文本并推送给教师（Ctrl+Alt+V / 托盘触发）。</summary>
    public static async void PushLocalToTeacher()
    {
        try
        {
            string? text = null;
            if (Clipboard.ContainsText())
                text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastWindow.ShowToast("剪贴板为空", "没有可推送的文本内容");
                return;
            }
            await Api.PushClipboardToTeacherAsync(text);
            ToastWindow.ShowToast("已推送到手机 WebUI ✅",
                text[..Math.Min(60, text.Length)] + (text.Length > 60 ? "…" : ""));
        }
        catch (Exception ex)
        {
            App.Log.Error("推送剪贴板失败", ex);
            ToastWindow.ShowToast("推送失败", ex.Message);
        }
    }
}
