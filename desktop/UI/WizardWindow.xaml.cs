using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Classify.Models;
using Classify.Services;

namespace Classify.UI;

/// <summary>首次启动配置向导：协议 → 连接云端（扫码绑定/手动配对码） → 完成。</summary>
public partial class WizardWindow : Window
{
    private int _step;
    private bool _cloudBound;

    public WizardWindow()
    {
        InitializeComponent();
        WindowUtil.DarkTitleBar(this);
        WindowUtil.RoundCorners(this);

        AgreementText.Text = LoadAgreement();
        NameBox.Text = $"{Environment.MachineName}·一体机";
        Closed += (_, _) =>
        {
            SettingsService.Instance.Save();
            // 向导被直接关闭且未完成：退出应用，避免留下无界面进程
            if (!SettingsService.Instance.Values.WizardDone) Environment.Exit(0);
        };
        GoTo(0);
    }

    private static string LoadAgreement()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string a = File.Exists(Path.Combine(baseDir, "Assets\\用户协议.txt"))
                ? File.ReadAllText(Path.Combine(baseDir, "Assets\\用户协议.txt")) : "";
            string b = File.Exists(Path.Combine(baseDir, "Assets\\隐私政策.txt"))
                ? File.ReadAllText(Path.Combine(baseDir, "Assets\\隐私政策.txt")) : "";
            return a + "\n\n──────────────\n\n" + b;
        }
        catch (Exception ex)
        {
            App.Log.Error("读取协议失败", ex);
            return "（协议文件缺失，请重新安装软件）";
        }
    }

    /* ---------------- 步骤切换 ---------------- */

    private void GoTo(int step)
    {
        _step = step;
        if (step != 2) CancelQrBinding();
        Step0.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = step switch
        {
            0 => "第 1 步 / 共 4 步 · 用户协议与隐私政策",
            1 => "第 2 步 / 共 4 步 · 连接云端",
            2 => "第 3 步 / 共 4 步 · 完成配置",
            _ => "第 4 步 / 共 4 步 · 完成",
        };
        Dot0.Opacity = 1;
        Dot1.Opacity = step >= 1 ? 1 : 0.35;
        Dot2.Opacity = step >= 2 ? 1 : 0.35;
        Dot3.Opacity = step >= 3 ? 1 : 0.35;

        BackBtn.Visibility = step is > 0 and < 3 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.IsEnabled = step switch
        {
            0 => AgreeCheck.IsChecked == true,
            _ => true,
        };
        NextBtn.Content = step switch
        {
            0 => "开始配置",
            1 => "下一步",
            2 => _cloudBound ? "下一步" : "跳过（稍后配置）",
            _ => "进入主界面",
        };
    }

    private void AgreeCheck_Changed(object sender, RoutedEventArgs e) => GoTo(_step);

    private void BackBtn_Click(object sender, RoutedEventArgs e) => GoTo(Math.Max(0, _step - 1));

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0: GoTo(1); break;
            case 1:
                StartQrBinding();
                GoTo(2);
                break;
            case 2: ApplyStep2(); GoTo(3); break;
            case 3: Finish(); break;
        }
    }

    /* ---------------- Step 2 · 扫码绑定 ---------------- */

    private CancellationTokenSource? _qrCts;
    private int _qrGeneration;

    private void CancelQrBinding()
    {
        _qrCts?.Cancel();
        _qrCts = null;
    }

    private void UpdateUi(Action action)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { action(); } catch (Exception ex) { App.Log.Error("向导 UI 更新失败", ex); }
        }));
    }

    private void StartQrBinding()
    {
        CancelQrBinding();
        _qrCts = new CancellationTokenSource();
        var token = _qrCts.Token;
        int generation = ++_qrGeneration;
        // UI 控件只能在 UI 线程读取，先取好设备名再进后台任务
        string name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? $"{Environment.MachineName}·一体机" : NameBox.Text.Trim();

        _ = Task.Run(async () =>
        {
            try
            {
                var api = new ApiService();
                UpdateUi(() => BindStatus.Text = "正在生成绑定二维码…");
                // 已注册设备再次绑定（多教师共享）时携带原 deviceId
                string? existingId = SettingsService.Instance.Values.DeviceId;
                if (string.IsNullOrWhiteSpace(existingId)) existingId = null;
                string reqId = await api.CreatePairRequestAsync(name, existingId);
                if (generation != _qrGeneration) return;
                string bindUrl = $"{CloudConfig.DefaultServerUrl}/bind?req={Uri.EscapeDataString(reqId)}";
                UpdateUi(() => RenderQr(bindUrl));
                UpdateUi(() => BindStatus.Text = "请用手机「扫一扫」扫描左侧二维码，并在网页上点击「确认绑定」");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!token.IsCancellationRequested)
                {
                    var left = TimeSpan.FromMinutes(9) - sw.Elapsed;
                    if (left <= TimeSpan.Zero) { UpdateUi(() => StartQrBinding()); return; } // 自动刷新
                    UpdateUi(() => QrCountdown.Text = $"二维码 {left.Minutes}:{left.Seconds:00} 后自动刷新");
                    try
                    {
                        var result = await api.PairWaitAsync(reqId, wait: 15, token);
                        if (generation != _qrGeneration) return;
                        if (result != null)
                        {
                            var s = SettingsService.Instance.Values;
                            if (string.IsNullOrEmpty(result.Token))
                            {
                                // 关联到已注册设备（多教师共享）：设备令牌保持不变
                                if (s.DeviceId != result.DeviceId || string.IsNullOrEmpty(s.DeviceToken))
                                {
                                    UpdateUi(() => BindStatus.Text = "❌ 设备与本机记录不一致，请重新运行向导或联系管理员");
                                    return;
                                }
                                s.DeviceName = string.IsNullOrEmpty(result.DeviceName) ? name : result.DeviceName;
                                _cloudBound = true;
                                UpdateUi(() =>
                                {
                                    BindStatus.Text = $"✅ 已扫码关联「{s.DeviceName}」（多教师共享），点击下一步继续";
                                    QrCountdown.Text = "";
                                    QrImage.Source = null;
                                });
                                UpdateUi(() => GoTo(_step)); // 刷新"下一步"按钮状态
                                return;
                            }
                            s.ServerUrl = CloudConfig.DefaultServerUrl;
                            s.DeviceName = string.IsNullOrEmpty(result.DeviceName) ? name : result.DeviceName;
                            s.DeviceId = result.DeviceId;
                            s.DeviceToken = result.Token;
                            s.Cursor = 0;
                            SettingsService.Instance.Save();
                            _cloudBound = true;
                            UpdateUi(() =>
                            {
                                BindStatus.Text = $"✅ 已扫码绑定「{s.DeviceName}」，点击下一步继续";
                                QrCountdown.Text = "";
                                QrImage.Source = null;
                            });
                            UpdateUi(() => GoTo(_step));
                            return;
                        }
                    }
                    catch (PairRequestExpiredException)
                    {
                        if (generation == _qrGeneration) UpdateUi(() => StartQrBinding());
                        return;
                    }
                    catch (HttpRequestException ex)
                    {
                        UpdateUi(() => BindStatus.Text = "网络异常，重试中…（" + ex.Message + "）");
                        await Task.Delay(3000, token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Log.Error("扫码绑定失败", ex);
                UpdateUi(() => BindStatus.Text = "❌ " + ex.Message + "（可点击「刷新二维码」重试，或使用手动输入配对码）");
            }
        }, token);
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

    private void RefreshQrBtn_Click(object sender, RoutedEventArgs e) => StartQrBinding();

    private void ManualToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        ManualPanel.Visibility = ManualPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        string server = CloudConfig.DefaultServerUrl;
        string code = CodeBox.Text.Trim();
        string name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? $"{Environment.MachineName}·一体机" : NameBox.Text.Trim();

        ConnectHint.Text = "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^\d{6}$"))
        {
            ConnectHint.Text = "❌ 请输入手机端生成的 6 位配对码";
            return;
        }

        // 暂停二维码长轮询，避免两条绑定流并发打到服务器/域名切换逻辑
        CancelQrBinding();

        ConnectBtn.IsEnabled = false;
        try
        {
            var api = new ApiService();
            ConnectHint.Text = "① 正在检查服务器可达…";
            _ = await api.GetVersionAsync(server).WaitAsync(TimeSpan.FromSeconds(12));
            ConnectHint.Text = "② 服务器可达，正在兑换配对码…";
            var result = await api.RegisterDeviceAsync(server, code, name).WaitAsync(TimeSpan.FromSeconds(12));

            var s = SettingsService.Instance.Values;
            s.ServerUrl = server;
            s.DeviceName = name;
            s.DeviceId = result.DeviceId;
            s.DeviceToken = result.Token;
            s.Cursor = 0;
            SettingsService.Instance.Save();

            _cloudBound = true;
            ConnectHint.Text = "✅ 绑定成功！设备已出现在手机端「设备」列表。"
                + (string.IsNullOrEmpty(result.ResetCode)
                    ? ""
                    : $"{Environment.NewLine}🔑 密码重置码（网页端忘记密码时使用，请截图保存，仅展示一次）：{result.ResetCode}");
            GoTo(_step); // 刷新按钮状态
        }
        catch (TimeoutException)
        {
            ConnectHint.Text = "❌ 连接超时（12 秒无响应）。请检查网络后重试，或改用扫码绑定。";
            StartQrBinding(); // 恢复二维码通道
        }
        catch (Exception ex)
        {
            ConnectHint.Text = "❌ " + ex.Message + "（请检查地址与配对码，或稍后重试）";
            StartQrBinding(); // 恢复二维码通道
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
        }
    }

    private void ApplyStep2()
    {
        var s = SettingsService.Instance.Values;
        if (_cloudBound)
        {
            PollingService.Instance.Start();
        }
        SettingsService.Instance.Save();
    }

    /* ---------------- Step 3 ---------------- */

    private void TtsTryBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = TtsService.Instance.SpeakAsync("同学们好，这是 Classify 的语音试听。");
    }

    private void TestAnnounceBtn_Click(object sender, RoutedEventArgs e)
    {
        // 本地测试喊话：完整走一遍全屏展示 + 朗读，让教师在绑定后立刻看到效果
        AnnouncementService.Instance.Enqueue(new AnnItem
        {
            Text = "绑定成功！这就是教室大屏喊话的效果 ✅",
            Speak = true,
            From = "本机",
        });
    }

    private void Finish()
    {
        var s = SettingsService.Instance.Values;
        s.AllowPower = AllowPowerCheck.IsChecked == true;
        s.WizardDone = true;
        s.AgreedVersion = "2026-10";

        if (AutoStartCheck.IsChecked == true)
        {
            try { AutoStartService.Enable(); }
            catch (Exception ex) { App.Log.Error("配置自启失败", ex); }
        }

        SettingsService.Instance.Save();
        App.Log.Info("向导完成");
        App.OnWizardFinished();
    }
}
