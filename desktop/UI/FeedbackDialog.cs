using System.Windows;
using System.Windows.Controls;
using Classify.Services;

namespace Classify.UI;

/// <summary>
/// 反馈对话框（双通道）：一句话描述 + 可选附脱敏日志。
/// 云端通道直达管理员面板；邮件通道打开 mailto:SupportEmail（描述与版本预填）。
/// </summary>
public static class FeedbackDialog
{
    public static void Show(Window? owner)
    {
        var w = new Window
        {
            Title = "💬 反馈问题",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BrushSurface"],
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["AppFont"],
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        if (owner is { IsVisible: true }) w.Owner = owner;
        w.SourceInitialized += (_, _) => WindowUtil.DarkTitleBar(w);

        var stack = new StackPanel { Margin = new Thickness(22) };

        var cat = new ComboBox { Style = (Style)Application.Current.Resources["Combo"], Margin = new Thickness(0, 0, 0, 10) };
        foreach (var item in new[] { "", "功能异常", "体验建议", "连接问题", "看板/EasiCare", "其他" })
            cat.Items.Add(item.Length == 0 ? "选择类别（可选）" : item);
        cat.SelectedIndex = 0;

        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["InputBox"],
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = 500,
        };

        var logsCheck = new CheckBox
        {
            Style = (Style)Application.Current.Resources["Check"],
            Content = "附带脱敏日志（已自动清除令牌/密钥等凭据，仅管理员可见）",
            IsChecked = true,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var hint = new TextBlock
        {
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrushSub"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Text = "为保护隐私，请不要在描述中填写学生姓名等个人信息。",
        };

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var mailBtn = new Button { Style = (Style)Application.Current.Resources["BtnTonal"], Content = "📧 邮件反馈" };
        var cancel = new Button { Style = (Style)Application.Current.Resources["BtnTonal"], Content = "取消", Margin = new Thickness(10, 0, 0, 0) };
        var send = new Button { Style = (Style)Application.Current.Resources["BtnPrimary"], Content = "提交反馈", Margin = new Thickness(10, 0, 0, 0) };
        btns.Children.Add(mailBtn);
        btns.Children.Add(cancel);
        btns.Children.Add(send);

        stack.Children.Add(cat);
        stack.Children.Add(box);
        stack.Children.Add(logsCheck);
        stack.Children.Add(hint);
        stack.Children.Add(btns);
        w.Content = stack;

        cancel.Click += (_, _) => w.Close();
        mailBtn.Click += (_, _) =>
        {
            var subject = Uri.EscapeDataString($"[Classify {LogService.AppVersion}] 问题反馈");
            var body = Uri.EscapeDataString(
                $"\n\n----\n版本：{LogService.AppVersion}\n设备：{SettingsService.Instance.Values.DeviceName}\n描述：（请在此补充）");
            Interop.Shell.Open($"mailto:{CloudConfig.SupportEmail}?subject={subject}&body={body}");
        };
        send.Click += async (_, _) =>
        {
            var text = box.Text.Trim();
            if (text.Length == 0)
            {
                ToastWindow.ShowToast("请填写描述", "至少写一句话，方便定位问题");
                return;
            }
            send.IsEnabled = false;
            try
            {
                var logs = logsCheck.IsChecked == true ? App.Log.RedactedTail(6000) : "";
                var category = cat.SelectedIndex > 0 ? (string)cat.Items[cat.SelectedIndex] : "";
                var (ok, error) = await new ApiService().SendFeedbackAsync(text, category, logs);
                if (ok)
                {
                    ToastWindow.ShowToast("反馈已提交 🙏", "管理员将在面板中看到（可附脱敏日志）");
                    w.Close();
                }
                else
                {
                    ToastWindow.ShowToast("提交失败", $"{error}\n可改用邮件：{CloudConfig.SupportEmail}");
                    send.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast("提交失败", $"{ex.Message}\n可改用邮件：{CloudConfig.SupportEmail}");
                send.IsEnabled = true;
            }
        };

        w.Loaded += (_, _) => box.Focus();
        w.Show();
    }
}
