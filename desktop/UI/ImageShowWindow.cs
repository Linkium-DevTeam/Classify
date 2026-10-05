using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Classify.UI;

/// <summary>
/// 图片快投（全屏）：教师从手机投来的图片全屏展示；
/// 多张自动排队，点击左右半屏翻页，Esc 或右上 ✕ 关闭。壁纸底 + 居中适配。
/// </summary>
public class ImageShowWindow : Window
{
    public static ImageShowWindow? Current { get; private set; }
    private static readonly List<string> Queue = new();

    private readonly Image _view = new() { Stretch = Stretch.Uniform, Margin = new Thickness(40) };
    private readonly TextBlock _hint = new()
    {
        Text = "",
        FontSize = 14,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xBC)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 24),
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
    };
    private int _index;

    private ImageShowWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Title = "Classify 图片快投";
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x20));
        var grid = new Grid();
        grid.Children.Add(_view);
        grid.Children.Add(_hint);
        var close = new Button
        {
            Content = "✕ 关闭",
            Style = (Style)Application.Current.Resources["BtnTonal"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 18, 0),
            Opacity = 0.75,
        };
        close.Click += (_, _) => StopShow();
        grid.Children.Add(close);
        Content = grid;
        MouseDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (e.GetPosition(this).X < ActualWidth / 2) ShowAt(_index - 1);
                else StopShow();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) StopShow();
            else if (e.Key == Key.Left) ShowAt(_index - 1);
            else if (e.Key == Key.Right) ShowAt(_index + 1);
        };
        Current = this;
    }

    /// <summary>入队并显示。路径为已下载到本地的图片文件。</summary>
    public static void Enqueue(string path)
    {
        if (!File.Exists(path)) return;
        if (!Queue.Contains(path)) Queue.Add(path);
        if (Current is null) Current = new ImageShowWindow();
        Current.ShowAtInternal(Queue.Count - 1);
    }

    public static void StopShow()
    {
        Queue.Clear();
        Current?.Close();
        Current = null;
    }

    private void ShowAtInternal(int index)
    {
        WindowUtil.EnterFullScreen(this);
        if (!IsLoaded) Show();
        else WindowUtil.ShowWithoutActivate(this);
        ShowAt(index);
    }

    private void ShowAt(int index)
    {
        if (Queue.Count == 0) { StopShow(); return; }
        _index = ((index % Queue.Count) + Queue.Count) % Queue.Count;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Queue[_index]);
            bmp.EndInit();
            _view.Source = bmp;
            _hint.Text = Queue.Count > 1 ? $"{_index + 1} / {Queue.Count} · 点击右半屏下一张，✕ 关闭" : "";
        }
        catch (Exception ex)
        {
            App.Log.Error("图片快投加载失败", ex);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Queue.Clear();
        if (Current == this) Current = null;
        base.OnClosed(e);
    }
}
