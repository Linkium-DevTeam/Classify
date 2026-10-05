using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Classify.Services;

namespace Classify.UI;

/// <summary>
/// 随时可出示的绑定二维码：已注册设备直接关联新教师（多教师共享），无需重新配对。
/// </summary>
public partial class QrBindWindow : Window
{
    private CancellationTokenSource? _cts;
    private int _generation;

    public QrBindWindow()
    {
        InitializeComponent();
        WindowUtil.DarkTitleBar(this);
        WindowUtil.RoundCorners(this);
        Width = 400;
        Height = 580;
        ResizeMode = ResizeMode.NoResize;
        Closed += (_, _) => _cts?.Cancel();
        Activated += (_, args) =>
        {
            if (args != null && _cts is null) Start();
        };
    }

    private void Start()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        int generation = ++_generation;
        // UI 线程读取后再进后台
        string name = string.IsNullOrWhiteSpace(SettingsService.Instance.Values.DeviceName)
            ? $"{Environment.MachineName}·一体机" : SettingsService.Instance.Values.DeviceName;
        string? existingId = SettingsService.Instance.Values.DeviceId;
        if (string.IsNullOrWhiteSpace(existingId)) existingId = null;

        _ = Task.Run(async () =>
        {
            try
            {
                var api = new ApiService();
                Update(() => StatusText.Text = "正在生成二维码…");
                string reqId = await api.CreatePairRequestAsync(name, existingId);
                if (generation != _generation) return;
                string bindUrl = $"{CloudConfig.DefaultServerUrl}/bind?req={Uri.EscapeDataString(reqId)}";
                Update(() => RenderQr(bindUrl));
                Update(() => StatusText.Text = "等待教师扫码确认…");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!token.IsCancellationRequested)
                {
                    var left = TimeSpan.FromMinutes(9) - sw.Elapsed;
                    if (left <= TimeSpan.Zero) { Update(() => Start()); return; }
                    Update(() => CountdownText.Text = $"二维码 {left.Minutes}:{left.Seconds:00} 后自动刷新");
                    try
                    {
                        var result = await api.PairWaitAsync(reqId, wait: 15, token);
                        if (generation != _generation) return;
                        if (result != null)
                        {
                            if (string.IsNullOrEmpty(result.Token))
                            {
                                // 关联到本机已有设备：令牌不变
                                if (SettingsService.Instance.Values.DeviceId == result.DeviceId)
                                {
                                    SettingsService.Instance.Values.DeviceName =
                                        string.IsNullOrEmpty(result.DeviceName) ? name : result.DeviceName;
                                    SettingsService.Instance.Save();
                                }
                                Update(() => StatusText.Text = $"✅ 新教师已关联「{result.DeviceName}」");
                            }
                            else
                            {
                                // 服务端自愈：设备曾被删并重新创建，保存新令牌
                                var s = SettingsService.Instance.Values;
                                s.ServerUrl = CloudConfig.DefaultServerUrl;
                                s.DeviceId = result.DeviceId;
                                s.DeviceToken = result.Token;
                                s.DeviceName = string.IsNullOrEmpty(result.DeviceName) ? name : result.DeviceName;
                                s.Cursor = 0;
                                SettingsService.Instance.Save();
                                Update(() => StatusText.Text = $"✅ 设备已重新绑定「{result.DeviceName}」");
                            }
                            Update(() => { QrImage.Source = null; CountdownText.Text = ""; });
                            await Task.Delay(2500, token);
                            Update(() => Close());
                            return;
                        }
                    }
                    catch (PairRequestExpiredException)
                    {
                        if (generation == _generation) Update(() => Start());
                        return;
                    }
                    catch (HttpRequestException ex)
                    {
                        Update(() => StatusText.Text = "网络异常，重试中…（" + ex.Message + "）");
                        await Task.Delay(3000, token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Log.Error("扫码邀请失败", ex);
                Update(() => StatusText.Text = "❌ " + ex.Message + "（可点击「刷新二维码」重试）");
            }
        }, token);
    }

    private void Update(Action action)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { action(); } catch (Exception ex) { App.Log.Error("QrBind UI 更新失败", ex); }
        }));
    }

    private void RenderQr(string url)
    {
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            QrImage.Source = bmp;
        }
        catch (Exception ex)
        {
            App.Log.Error("二维码渲染失败", ex);
        }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => Start();
}
