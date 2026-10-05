using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Classify.Services;

namespace Classify.UI;

public partial class MainWindow : Window
{
    private bool _positionLoaded;
    private IntPtr _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        try { BrandLogo.Source = Brand.Logo(); } catch { }
        WindowUtil.DarkTitleBar(this);
        WindowUtil.RoundCorners(this);
        Interop.TaskbarProgress.SetAumid("LinkiumDevTeam.Classify");
        // 句柄在 UI 线程（构造时）一次性缓存：EnsureHandle 有线程亲和约束，
        // 静默自启时窗口不 Show 也照样有句柄，后台线程（FileReceiveService）直接读缓存
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        RestoreOrCenter();
        Activated += (_, _) => UpdateStatusSuffix();
        PollingService.Instance.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(UpdateStatusSuffix);
        NavHome.IsChecked = true;
    }

    public IntPtr Handle => _hwnd;

    private void RestoreOrCenter()
    {
        var s = SettingsService.Instance.Values;
        if (s.WindowWidth is int w && s.WindowHeight is int h &&
            s.WindowLeft is int l && s.WindowTop is int t &&
            w >= 500 && h >= 400)
        {
            // 恢复上次位置（钳制在虚拟桌面内，防止显示器变更后丢窗口）
            var va = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth;
            var vh = SystemParameters.VirtualScreenHeight;
            Left = Math.Clamp(l, va - w + 160, va + vw - 160);
            Top = Math.Clamp(t, vt, vt + vh - 80);
            Width = w;
            Height = h;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _positionLoaded = true;
    }

    private DateTime _lastMoveSave;

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_positionLoaded) SavePositionDebounced();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_positionLoaded) SavePositionDebounced();
    }

    private void SavePositionDebounced()
    {
        // 简单去抖：拖动过程中频繁写盘没有意义
        if ((DateTime.Now - _lastMoveSave).TotalMilliseconds < 600) return;
        _lastMoveSave = DateTime.Now;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var st = SettingsService.Instance.Values;
                st.WindowLeft = (int)Left;
                st.WindowTop = (int)Top;
                st.WindowWidth = (int)ActualWidth;
                st.WindowHeight = (int)ActualHeight;
                SettingsService.Instance.Save();
            }
            catch { }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>关闭窗口 = 隐藏到托盘，保持后台轮询。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        SettingsService.Instance.Save();
        base.OnClosing(e);
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false; // 拉到最前但不长期置顶
        Focus();
    }

    public void NavigateTo(string tag)
    {
        switch (tag)
        {
            case "settings": NavSettings.IsChecked = true; break;
            case "about": NavAbout.IsChecked = true; break;
            default: NavHome.IsChecked = true; break;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        var tag = rb.Tag as string;
        ContentHost.Content = tag switch
        {
            "settings" => new SettingsPage(),
            "about" => new AboutPage(),
            _ => (object)new HomePage(),
        };
    }

    private void UpdateStatusSuffix()
    {
        try
        {
            string cloud = PollingService.Instance.Status switch
            {
                CloudStatus.Online => "云端已连接",
                CloudStatus.Connecting => "云端连接中",
                CloudStatus.Offline => "云端离线",
                _ => "未配置云端",
            };
            var s = SettingsService.Instance.Values;
            var dev = string.IsNullOrEmpty(s.DeviceId) ? "" : $" · {s.DeviceName}";
            StatusText.Text = $"{cloud}{dev}";
            TrayService.Instance.SetStatusSuffix(cloud);
        }
        catch { }
    }
}
