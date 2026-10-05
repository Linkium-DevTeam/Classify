using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Classify.Models;
using Classify.Services;

namespace Classify.UI;

public partial class SettingsPage : UserControl
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var s = SettingsService.Instance.Values;

        ServerBox.Text = s.ServerUrl;
        DeviceIdBox.Text = string.IsNullOrEmpty(s.DeviceId)
            ? "（未配对）"
            : $"{s.DeviceName} · {s.DeviceId}";
        CloudHint.Text = PollingService.Instance.StatusText;

        var voices = TtsService.GetVoices();
        VoiceCombo.ItemsSource = voices;
        NaturalVoiceBtn.Visibility = SapiVoiceBridge.IsAvailable()
            ? Visibility.Visible : Visibility.Collapsed;
        var defId = string.IsNullOrEmpty(s.TtsVoiceId) ? TtsService.DefaultVoiceId() : s.TtsVoiceId;
        VoiceCombo.SelectedIndex = voices.FindIndex(v => v.Id == defId);

        RateSlider.Value = s.TtsRate;
        RateText.Text = $"{s.TtsRate:0.0}×";
        TtsToggle.IsChecked = s.TtsEnabled;
        ChimeToggle.IsChecked = s.ChimeEnabled;
        ChimePathBox.Text = s.ChimePath;
        OverlaySecBox.Text = s.OverlaySeconds.ToString();
        PopupSecBox.Text = s.PopupSeconds.ToString();
        SaveToggle.IsChecked = s.SaveToDesktop;
        ProcBox.Text = s.FullscreenProcesses;

        AllowVolumeToggle.IsChecked = s.AllowVolume;
        AllowLockToggle.IsChecked = s.AllowLock;
        AllowPowerToggle.IsChecked = s.AllowPower;
        AllowCameraToggle.IsChecked = s.AllowCamera;
        AllowClipboardToggle.IsChecked = s.AllowClipboard;
        DuckingToggle.IsChecked = s.DuckingEnabled;
        TelemetryToggle.IsChecked = s.TelemetryEnabled;

        AzureToggle.IsChecked = s.AzureEnabled;
        AzureRegionBox.Text = string.IsNullOrWhiteSpace(s.AzureRegion) ? "eastasia" : s.AzureRegion;
        AzureKeyBox.Password = s.AzureKey;

        AutoStartToggle.IsChecked = AutoStartService.IsEnabled;
        UpdateAutoStartHint();

        _loading = false;
    }

    private static AppSettings S => SettingsService.Instance.Values;
    private void Save() => SettingsService.Instance.Save();
    private static bool On(CheckBox? cb) => cb?.IsChecked == true;

    private void RewizardBtn_Click(object sender, RoutedEventArgs e) => new WizardWindow().Show();

    private async void ReconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        S.ServerUrl = ServerBox.Text.Trim();
        Save();
        CloudHint.Text = "正在连接…";
        try
        {
            var ver = await new ApiService().GetVersionAsync(S.ServerUrl);
            PollingService.Instance.Start();
            CloudHint.Text = $"已连接：{ver.GetProperty("name").GetString()} v{ver.GetProperty("version").GetString()}";
        }
        catch (Exception ex)
        {
            CloudHint.Text = "连接失败：" + ex.Message;
        }
    }

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (VoiceCombo.SelectedItem is VoiceItem v)
        {
            S.TtsVoiceId = v.Id;
            Save();
        }
    }

    private void AzureVoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AzureVoiceCombo.SelectedItem is VoiceItem v)
        {
            S.TtsVoiceId = v.Id; // azure: 前缀语音走 Azure 引擎
            S.AzureVoice = v.Id.StartsWith("azure:", StringComparison.Ordinal) ? v.Id["azure:".Length..] : S.AzureVoice;
            Save();
        }
    }

    private void TtsTestBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = TtsService.Instance.SpeakAsync("同学们好，这是 Classify 的语音试听。");
    }

    private void NaturalVoiceBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NaturalVoiceBtn.IsEnabled = false;
            int added = SapiVoiceBridge.CopyMissingTokens();
            var voices = TtsService.GetVoices();
            VoiceCombo.ItemsSource = voices;
            NaturalVoiceBtn.Visibility = SapiVoiceBridge.IsAvailable()
                ? Visibility.Visible : Visibility.Collapsed;
            if (added > 0)
            {
                // 自动选中第一个中文 SAPI 语音（通常是晓晓自然语音）
                var pick = voices.FirstOrDefault(v => v.Engine == "sapi" && v.Language.StartsWith("zh"));
                if (pick != null) VoiceCombo.SelectedItem = pick;
                ToastWindow.ShowToast("自然语音已启用", $"新增 {added} 个语音，已为你选中中文语音");
            }
            else
            {
                ToastWindow.ShowToast("未新增语音", "讲述人自然语音可能尚未下载，或已全部可用");
            }
        }
        catch (Exception ex)
        {
            ToastWindow.ShowToast("启用失败", ex.Message);
        }
        finally
        {
            NaturalVoiceBtn.IsEnabled = true;
        }
    }

    private void RateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        S.TtsRate = Math.Round(e.NewValue, 1);
        if (RateText != null) RateText.Text = $"{S.TtsRate:0.0}×";
        Save();
    }

    private void TtsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.TtsEnabled = On(TtsToggle);
        Save();
    }

    private void ChimeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ChimeEnabled = On(ChimeToggle);
        Save();
    }

    private void ChimePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        S.ChimePath = ChimePathBox.Text.Trim();
        Save();
    }

    private void ChimePickBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Filter = "WAV 音频 (*.wav)|*.wav",
                Title = "选择提示音",
            };
            if (dlg.ShowDialog() != true) return;
            // 拷贝到应用数据目录，避免依赖原位置（如"下载"文件夹被清理）
            var dest = Path.Combine(AppPaths.DataDir, "chime-custom.wav");
            File.Copy(dlg.FileName, dest, overwrite: true);
            ChimePathBox.Text = dest;
            try
            {
                using var player = new System.Media.SoundPlayer(dest);
                player.Play();
            }
            catch { }
            ToastWindow.ShowToast("提示音已更新", Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            ToastWindow.ShowToast("选择提示音失败", ex.Message);
        }
    }

    private void OverlaySecBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(OverlaySecBox.Text, out var v))
        {
            S.OverlaySeconds = Math.Clamp(v, 5, 300);
            Save();
        }
    }

    private void PopupSecBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(PopupSecBox.Text, out var v))
        {
            S.PopupSeconds = Math.Clamp(v, 5, 300);
            Save();
        }
    }

    private void SaveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.SaveToDesktop = On(SaveToggle);
        Save();
    }

    private void ProcBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        S.FullscreenProcesses = ProcBox.Text.Trim();
        Save();
    }

    private void AllowVolumeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AllowVolume = On(AllowVolumeToggle);
        Save();
    }

    private void AllowLockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AllowLock = On(AllowLockToggle);
        Save();
    }

    private void AllowPowerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AllowPower = On(AllowPowerToggle);
        Save();
        ToastWindow.ShowToast(S.AllowPower ? "已允许远程关机/重启" : "已禁止远程关机/重启",
            "能力状态会同步到手机端（心跳上报，约 1 分钟内生效）");
    }

    private void AllowCameraToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AllowCamera = On(AllowCameraToggle);
        Save();
        ToastWindow.ShowToast(S.AllowCamera ? "已允许远程拍照回传" : "已禁止远程拍照回传（默认）",
            "能力状态会同步到手机端（心跳上报，约 1 分钟内生效）");
    }

    private void AllowClipboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AllowClipboard = On(AllowClipboardToggle);
        Save();
    }

    private void DuckingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.DuckingEnabled = On(DuckingToggle);
        Save();
    }

    private void TelemetryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.TelemetryEnabled = On(TelemetryToggle);
        Save();
    }

    private void AzureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.AzureEnabled = On(AzureToggle);
        SaveAzure();
        if (S.AzureEnabled)
            ToastWindow.ShowToast("Azure 神经语音已启用", "喊话将默认使用晓晓 Online（失败自动回退本地引擎）");
    }

    private void SaveAzure()
    {
        S.AzureRegion = string.IsNullOrWhiteSpace(AzureRegionBox.Text) ? "eastasia" : AzureRegionBox.Text.Trim();
        S.AzureKey = AzureKeyBox.Password.Trim();
        Save();
    }

    private async void AzureSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveAzure();
        await RefreshAzureVoicesAsync();
    }

    private async void AzureVoiceRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAzureVoicesAsync();

    private async Task RefreshAzureVoicesAsync()
    {
        try
        {
            AzureVoiceRefresh.IsEnabled = false;
            SaveAzure();
            var voices = await TtsService.GetAzureVoicesAsync(S.AzureRegion, S.AzureKey);
            if (voices.Count == 0)
            {
                ToastWindow.ShowToast("未获取到语音", "请检查区域与密钥");
                return;
            }
            var combined = new List<VoiceItem>(voices);
            combined.AddRange(TtsService.GetVoices());
            var pick = combined.FirstOrDefault(v => v.Id == "azure:zh-CN-XiaoxiaoNeural")
                     ?? combined.FirstOrDefault(v => v.Id.StartsWith("azure:"));
            AzureVoiceCombo.ItemsSource = combined;
            if (pick != null) AzureVoiceCombo.SelectedItem = pick;
            ToastWindow.ShowToast("Azure 语音列表已刷新", $"{voices.Count} 个中文神经语音可用");
        }
        catch (Exception ex)
        {
            ToastWindow.ShowToast("获取 Azure 语音失败", ex.Message);
        }
        finally
        {
            AzureVoiceRefresh.IsEnabled = true;
        }
    }

    private async void AzureTestBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveAzure();
        await TtsService.Instance.SpeakAsync("同学们好，我是晓晓，Azure 神经语音测试成功。");
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            string result;
            if (On(AutoStartToggle)) result = AutoStartService.Enable();
            else { AutoStartService.Disable(); result = "已关闭自启"; }
            ToastWindow.ShowToast("开机自启", result);
        }
        catch (Exception ex)
        {
            App.Log.Error("设置自启失败", ex);
            ToastWindow.ShowToast("设置自启失败", ex.Message);
        }
        AutoStartToggle.IsChecked = AutoStartService.IsEnabled;
        UpdateAutoStartHint();
    }

    private async void ResetCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResetCodeBtn.IsEnabled = false;
            var rc = await new ApiService().GetDeviceResetCodeAsync();
            await Dialogs.ShowText($"🔑 密码重置码（{rc.Username}）",
                $"为教师「{rc.Username}」生成的一次性密码重置码：\n\n{rc.Code}\n\n" +
                "30 分钟内有效、用后即废。在手机/网页端「忘记密码」处输入此码即可设置新密码。");
        }
        catch (Exception ex)
        {
            App.Log.Error("生成重置码失败", ex);
            ToastWindow.ShowToast("生成失败", ex.Message);
        }
        finally
        {
            ResetCodeBtn.IsEnabled = true;
        }
    }

    private void UpdateAutoStartHint()
    {
        AutoStartHint.Text = AutoStartService.IsEnabled
            ? $"当前方式：{AutoStartService.MethodText}。开机后仅驻留托盘不弹窗口；进程被结束后伴生进程会自动恢复。"
            : "未启用。开启后开机仅驻留托盘；被杀自动恢复由伴生进程承担。";
    }
}
