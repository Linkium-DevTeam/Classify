using System.ComponentModel;
using System.Media;
using System.Net.Http;
using System.Text;
using Classify.Models;

namespace Classify.Services;

/// <summary>
/// 语音朗读，双引擎：
///  1) Azure 神经语音（晓晓 Online Natural，配置后默认）——返回 WAV 流直接播放；
///  2) SAPI5（System.Speech）——经 SapiVoiceBridge 桥接后可用讲述人同款自然语音。
/// v3 不再使用 WinRT MediaPlayer（无音频端点时的原生崩溃路径，历史 F9），统一走 winmm。
/// </summary>
public sealed class TtsService : INotifyPropertyChanged
{
    public static TtsService Instance { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SoundPlayer? _player;
    private System.Speech.Synthesis.SpeechSynthesizer? _sapi;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _speaking;
    public bool Speaking
    {
        get => _speaking;
        private set
        {
            if (_speaking == value) return;
            _speaking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Speaking)));
        }
    }

    /// <summary>枚举本地 SAPI5 语音（含讲述人自然语音）。</summary>
    public static List<VoiceItem> GetVoices()
    {
        var list = new List<VoiceItem>();
        try
        {
            using var sapi = new System.Speech.Synthesis.SpeechSynthesizer();
            foreach (var v in sapi.GetInstalledVoices())
            {
                var info = v.VoiceInfo;
                list.Add(new VoiceItem
                {
                    Id = "sapi:" + info.Name,
                    DisplayName = info.Name,
                    Language = info.Culture?.Name ?? "",
                    Engine = "sapi",
                });
            }
        }
        catch (Exception ex) { App.Log.Error("枚举 SAPI 语音失败", ex); }

        return list
            .OrderByDescending(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            .ThenBy(v => v.DisplayName)
            .ToList();
    }

    public static string DefaultVoiceId()
    {
        var settings = SettingsService.Instance.Values;
        var voices = GetVoices();
        if (!string.IsNullOrEmpty(settings.TtsVoiceId) && voices.Any(v => v.Id == settings.TtsVoiceId))
            return settings.TtsVoiceId;
        var zh = voices.FirstOrDefault(v => v.Language.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase))
              ?? voices.FirstOrDefault(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        return (zh ?? voices.FirstOrDefault())?.Id ?? "";
    }

    /// <summary>朗读文本，完成（或出错）时返回。可随时 Stop()。</summary>
    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        await _gate.WaitAsync();
        Speaking = true;
        try
        {
            var settings = SettingsService.Instance.Values;

            // 无可用音频输出设备（远程会话/端点被禁用）时直接跳过播放（历史 F9 守卫）
            if (Interop.Audio.GetMasterVolume() < 0)
            {
                App.Log.Info("没有可用的音频输出设备，跳过语音播放");
                return;
            }

            var voiceId = settings.TtsVoiceId;
            if (string.IsNullOrEmpty(voiceId)) voiceId = DefaultVoiceId();

            // Azure 神经语音优先（开启且配置完整时，除非用户明确选了 SAPI 语音）
            if (settings.AzureEnabled && !string.IsNullOrWhiteSpace(settings.AzureKey)
                && !voiceId.StartsWith("sapi:", StringComparison.Ordinal))
            {
                var azureVoice = voiceId.StartsWith("azure:", StringComparison.Ordinal)
                    ? voiceId["azure:".Length..]
                    : (string.IsNullOrWhiteSpace(settings.AzureVoice) ? "zh-CN-XiaoxiaoNeural" : settings.AzureVoice);
                var ok = await SpeakAzureAsync(text, azureVoice, settings.AzureRegion, settings.TtsRate);
                if (ok) { MetricsService.Incr("tts_azure"); return; }
                App.Log.Error("Azure 语音失败，回退本地引擎", null);
            }

            if (voiceId.StartsWith("azure:", StringComparison.Ordinal))
            {
                // 设置里明确选了 Azure 语音但 Azure 未启用/失败：临时启用直连一次
                var ok2 = await SpeakAzureAsync(text, voiceId["azure:".Length..],
                    settings.AzureRegion, settings.TtsRate);
                if (ok2) return;
            }

            if (voiceId.StartsWith("sapi:", StringComparison.Ordinal))
            {
                bool ok = await SpeakSapiAsync(text, voiceId["sapi:".Length..], settings.TtsRate);
                if (ok) { MetricsService.Incr("tts_sapi"); return; }
                App.Log.Error("SAPI 语音失败，回退默认语音", null);
            }

            // 最后兜底：任一可用中文 SAPI 语音
            if (await SpeakSapiAsync(text, "", settings.TtsRate)) MetricsService.Incr("tts_sapi");
        }
        catch (Exception ex)
        {
            App.Log.Error("TTS 异常", ex);
        }
        finally
        {
            Speaking = false;
            _gate.Release();
        }
    }

    public void Stop()
    {
        try { _player?.Stop(); } catch { }
        try { _sapi?.SpeakAsyncCancelAll(); } catch { }
    }

    /* ---------------- Azure 神经语音 ---------------- */

    private static readonly HttpClient AzureHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>拉取 Azure 语音清单（中文 Neural），设置页选择用。</summary>
    public static async Task<List<VoiceItem>> GetAzureVoicesAsync(string region, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
        req.Headers.Add("Ocp-Apim-Subscription-Key", key);
        using var resp = await AzureHttp.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var list = new List<VoiceItem>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var shortName = v.TryGetProperty("ShortName", out var sn) ? sn.GetString() : null;
            var voiceType = v.TryGetProperty("VoiceType", out var vt) ? vt.GetString() : "";
            if (shortName is null || voiceType != "Neural") continue;
            var locale = v.TryGetProperty("Locale", out var lc) ? lc.GetString() ?? "" : "";
            if (!locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) continue;
            var display = v.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? "" : "";
            var localName = v.TryGetProperty("LocalName", out var ln) ? ln.GetString() ?? "" : "";
            list.Add(new VoiceItem
            {
                Id = "azure:" + shortName,
                DisplayName = string.IsNullOrEmpty(localName) ? display : $"{localName} ({display})",
                Language = locale,
                Engine = "azure",
            });
        }
        return list.OrderBy(v => v.DisplayName).ToList();
    }

    private async Task<bool> SpeakAzureAsync(string text, string voice, string region, double rate)
    {
        var key = SettingsService.Instance.Values.AzureKey;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region)) return false;

        var escaped = System.Security.SecurityElement.Escape(text) ?? "";
        var ratePct = Math.Clamp((int)Math.Round(rate * 100), 50, 200);
        var ssml = $"<speak version='1.0' xml:lang='zh-CN'><voice name='{System.Security.SecurityElement.Escape(voice)}'>" +
                   $"<prosody rate='{ratePct}%'>{escaped}</prosody></voice></speak>";

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");
        req.Headers.Add("Ocp-Apim-Subscription-Key", key);
        req.Headers.TryAddWithoutValidation("Content-Type", "application/ssml+xml");
        req.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
        req.Headers.TryAddWithoutValidation("User-Agent", "Classify");
        req.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

        using var resp = await AzureHttp.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            App.Log.Aggregate($"Azure TTS {(int)resp.StatusCode}",
                new HttpRequestException(await resp.Content.ReadAsStringAsync()));
            return false;
        }
        var wav = await resp.Content.ReadAsByteArrayAsync();
        App.Log.Info($"Azure 语音合成成功（{voice}，{wav.Length} bytes）");
        return await PlayWavAsync(wav, text);
    }

    /// <summary>用 winmm（SoundPlayer）播放 WAV 字节。超时按字数兜底，可被 Stop() 打断。</summary>
    private async Task<bool> PlayWavAsync(byte[] wav, string text)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new SoundPlayer(new MemoryStream(wav));
        _player = player;
        var play = Task.Run(() =>
        {
            try
            {
                player.PlaySync(); // 阻塞至播放结束；Stop() 可中断
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                App.Log.Error("WAV 播放失败", ex);
                tcs.TrySetResult(false);
            }
        });
        // 超时兜底：按字数估算（中文约 4 字/秒，加 2 秒余量）
        var timeout = Task.Delay(TimeSpan.FromSeconds(4 + text.Length / 4.0));
        var winner = await Task.WhenAny(tcs.Task, timeout);
        try { player.Stop(); } catch { }
        try { await play; } catch { }
        if (_player == player) _player = null;
        player.Dispose();
        return winner == tcs.Task && await tcs.Task;
    }

    /* ---------------- SAPI5（讲述人自然语音） ---------------- */

    /// <summary>选择 SAPI 语音；目标语音不可用时按中文优先回退到任一可用语音（部分机器存在令牌损坏的语音，如 Huihui Desktop）。</summary>
    private static void SelectSapiVoice(System.Speech.Synthesis.SpeechSynthesizer synth, string voiceName)
    {
        if (voiceName.Length > 0)
        {
            try { synth.SelectVoice(voiceName); return; } catch { }
        }
        foreach (var v in synth.GetInstalledVoices())
        {
            if (!v.Enabled || v.VoiceInfo.Culture is null) continue;
            if (!v.VoiceInfo.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                synth.SelectVoice(v.VoiceInfo.Name);
                if (voiceName.Length > 0)
                    App.Log.Info($"SAPI 语音 {voiceName} 不可用，已回退 {v.VoiceInfo.Name}");
                return;
            }
            catch { }
        }
        if (voiceName.Length > 0)
            throw new InvalidOperationException($"没有可用的中文 SAPI 语音（目标 {voiceName}）");
    }

    private async Task<bool> SpeakSapiAsync(string text, string voiceName, double rate)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        _sapi = synth;
        try
        {
            SelectSapiVoice(synth, voiceName);
        }
        catch (Exception ex)
        {
            App.Log.Error($"SAPI 选择语音失败: {voiceName}", ex);
            synth.Dispose();
            _sapi = null;
            return false;
        }
        synth.Volume = 100; // 大声朗读：满音量
        synth.Rate = Math.Clamp((int)Math.Round((rate - 1) * 10), -10, 10);
        synth.SpeakCompleted += (_, e) =>
        {
            if (e.Error is not null) App.Log.Error("SAPI 朗读出错", e.Error);
            tcs.TrySetResult(e.Error is null);
            synth.Dispose();
            if (_sapi == synth) _sapi = null;
        };
        try
        {
            synth.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            App.Log.Error("SAPI 启动朗读失败", ex);
            synth.Dispose();
            _sapi = null;
            return false;
        }
        // 超时兜底（按字数估算）
        var timeout = Task.Delay(TimeSpan.FromSeconds(5 + text.Length / 8.0));
        var winner = await Task.WhenAny(tcs.Task, timeout);
        if (winner != tcs.Task)
        {
            try { synth.SpeakAsyncCancelAll(); } catch { }
            return false;
        }
        return await tcs.Task;
    }
}
