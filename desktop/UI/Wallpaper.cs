using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Classify.UI;

/// <summary>
/// 全屏界面统一背景：桌面壁纸 + 高斯模糊 + 暗色蒙版。
/// 看板/时钟/喊话/点名共用同一观感。
/// </summary>
public static class Wallpaper
{
    public static string? Path()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("Wallpaper") as string;
        }
        catch { return null; }
    }

    /// <summary>把壁纸（缩放解码 + 模糊）装进 Image 元素并调整不透明度。</summary>
    public static void Apply(Image target, double opacity = 0.35, double blurRadius = 30)
    {
        try
        {
            var path = Path();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 200; // 小图放大 + 模糊 = 天然高斯观感，内存也小
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            target.Source = bmp;
            target.Stretch = Stretch.UniformToFill;
            target.Opacity = opacity;
            target.Effect = new BlurEffect { Radius = blurRadius };
        }
        catch (Exception ex)
        {
            App.Log.Aggregate("壁纸加载", ex);
        }
    }
}
