using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Classify.Models;
using Classify.Services;

namespace Classify.UI;

public partial class HomePage : UserControl
{
    public ObservableCollection<ActivityItem> Activities => App.Activities;

    public HomePage()
    {
        InitializeComponent();
        DataContext = this;
        UpdateStatus();

        PollingService.Instance.PropertyChanged += (_, _) => EnqueueUpdate();
        ClassDetectService.Instance.PropertyChanged += (_, _) => EnqueueUpdate();
        App.Activities.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(RefreshActivityHint), DispatcherPriority.Background);
        RefreshActivityHint();
    }

    private int _pending;

    private void EnqueueUpdate()
    {
        // 服务事件可能高频，简单合并
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                UpdateStatus();
            }), DispatcherPriority.Background);
    }

    private void UpdateStatus()
    {
        var s = SettingsService.Instance.Values;
        var poll = PollingService.Instance;

        CloudText.Text = poll.Status == CloudStatus.Disabled
            ? "未配置云端"
            : $"{poll.StatusText} · {s.ServerUrl}";
        ClassText.Text = ClassDetectService.Instance.StateText
            + (ClassDetectService.Instance.InClass ? "" : $"（前台：{ClassDetectService.Instance.ForegroundProcess}）");
    }

    private void RefreshActivityHint()
    {
        EmptyHint.Visibility = App.Activities.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        AnnouncementService.Instance.Enqueue(new AnnItem
        {
            Text = "这是一条测试喊话 ✅ Classify 工作正常。",
            Speak = true,
            From = "本机",
        });
    }

    private void CopyUrlBtn_Click(object sender, RoutedEventArgs e)
    {
        var url = SettingsService.Instance.Values.ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ToastWindow.ShowToast("无法复制", "尚未配置云端服务");
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(url);
            ToastWindow.ShowToast("已复制 📋", url);
        }
        catch (Exception ex)
        {
            App.Log.Error("复制失败", ex);
            ToastWindow.ShowToast("复制失败", url);
        }
    }

    private void InviteBtn_Click(object sender, RoutedEventArgs e)
    {
        // 出示二维码让其他教师关联此设备（多教师共享）
        new QrBindWindow().Show();
    }

    private void WizardBtn_Click(object sender, RoutedEventArgs e)
    {
        new WizardWindow().Show();
    }
}
