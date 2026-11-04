using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Classify.UI;

/// <summary>捐助页：支付宝 + 微信二维码。</summary>
public partial class DonateWindow : Window
{
    public DonateWindow()
    {
        InitializeComponent();
        WindowUtil.DarkTitleBar(this);
        try
        {
            AlipayImg.Source = LoadAsset("donate-alipay.jpg");
            WechatImg.Source = LoadAsset("donate-wechat.png");
        }
        catch { }
    }

    private static BitmapImage LoadAsset(string name)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", name));
        bmp.EndInit();
        return bmp;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
