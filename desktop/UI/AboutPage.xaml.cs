using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Classify.Services;

namespace Classify.UI;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {LogService.AppVersion} · WPF / .NET 10";
        try
        {
            DevLogo.Source = new BitmapImage(new Uri(
                Path.Combine(AppContext.BaseDirectory, "Assets", "devteam.png")));
            BrandLogo.Source = Brand.Logo();
        }
        catch { }
        LoadCommunity();
    }

    private void AgreementBtn_Click(object sender, RoutedEventArgs e) =>
        ShowLegal("用户协议", "Assets\\用户协议.txt");

    private void PrivacyBtn_Click(object sender, RoutedEventArgs e) =>
        ShowLegal("隐私政策", "Assets\\隐私政策.txt");

    private async void ShowLegal(string title, string relative)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, relative);
            string text = File.Exists(path) ? await File.ReadAllTextAsync(path) : "未找到文件：" + path;
            await Dialogs.ShowText(title, text);
        }
        catch (Exception ex)
        {
            ToastWindow.ShowToast("无法打开", ex.Message);
        }
    }

    /// <summary>开源版启用 EasiCare 联动前的闭源组件提醒。返回是否继续。</summary>
    internal static bool EasiCareHelperClosedSourceWarning()
    {
        var r = System.Windows.MessageBox.Show(
            "EasiCareHelper 是一个闭源组件（第三方课堂评价工具的本地联动代理）。\n\n启用后，早读看板的加分将尝试写入 EasiCare。是否继续？",
            "即将启用闭源组件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    private void RepoBtn_Click(object sender, RoutedEventArgs e)
    {
        Interop.Shell.Open("https://github.com/Linkium-DevTeam/Classify");
    }

    private void AfdianBtn_Click(object sender, RoutedEventArgs e)
    {
        Interop.Shell.Open("https://afdian.com/a/linkium");
    }

    private async void LoadCommunity()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Classify");
            var json = await http.GetStringAsync("https://api.github.com/repos/Linkium-DevTeam/Classify/contributors?per_page=50");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var names = new List<string>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var login = c.GetProperty("login").GetString() ?? "";
                var n = c.GetProperty("contributions").GetInt32();
                names.Add($"{login} ×{n}");
            }
            ContributorsText.Text = names.Count > 0 ? string.Join(" · ", names) : "还没有贡献者，欢迎提交 PR";
        }
        catch { ContributorsText.Text = "贡献者名单加载失败（网络受限）"; }
    }

    private void FeedbackBtn_Click(object sender, RoutedEventArgs e)
    {
        FeedbackDialog.Show(Window.GetWindow(this));
    }

    private void LogsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!Interop.Shell.Open(App.Log.LogDirectory))
            ToastWindow.ShowToast("无法打开日志目录", App.Log.LogDirectory);
    }
}
