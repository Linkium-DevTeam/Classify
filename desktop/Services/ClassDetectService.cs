using System.ComponentModel;
using System.Windows.Threading;
using Classify.Interop;

namespace Classify.Services;

/// <summary>
/// 上课检测：前台窗口是否为 PPT/WPS/希沃白板 的全屏状态。
/// 检测到上课时，喊话由全屏改为弹窗，避免打断课件。
/// </summary>
public sealed class ClassDetectService : INotifyPropertyChanged
{
    public static ClassDetectService Instance { get; } = new();

    private DispatcherTimer? _timer;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _inClass;
    public bool InClass
    {
        get => _inClass;
        private set
        {
            if (_inClass == value) return;
            _inClass = value;
            App.Log.Info($"上课检测：{(value ? "进入上课状态（全屏课件）" : "退出上课状态")}");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InClass)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
        }
    }

    public string StateText => InClass ? "正在上课（全屏课件）" : "未检测到全屏课件";

    private string _foreground = "";
    public string ForegroundProcess
    {
        get => _foreground;
        private set
        {
            if (_foreground == value) return;
            _foreground = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ForegroundProcess)));
        }
    }

    public void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Check();
    }

    public void Check()
    {
        try
        {
            var proc = Win32.GetForegroundProcessName();
            ForegroundProcess = proc;
            InClass = IsClassProcess(proc) && Win32.IsForegroundFullScreen();
        }
        catch (Exception ex)
        {
            App.Log.Error("上课检测异常", ex);
        }
    }

    private bool IsClassProcess(string proc)
    {
        if (string.IsNullOrEmpty(proc)) return false;
        foreach (var raw in SettingsService.Instance.Values.FullscreenProcesses.Split(','))
        {
            if (string.Equals(proc, raw.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
