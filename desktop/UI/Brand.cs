using System.IO;
using System.Windows.Media.Imaging;

namespace Classify.UI;

/// <summary>应用内 Logo 装载：从 exe 旁 Assets 目录用绝对路径读取（XAML 相对 pack URI 对 Content 文件不可靠）。</summary>
public static class Brand
{
    public static BitmapImage Logo()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "logo256.png"));
        bmp.EndInit();
        return bmp;
    }
}
