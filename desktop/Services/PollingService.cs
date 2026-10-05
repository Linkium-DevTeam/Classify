using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;
using Classify.Models;
using Classify.UI;

namespace Classify.Services;

public enum CloudStatus { Disabled, Connecting, Online, Offline }

/// <summary>
/// 云端实时连接：WebSocket 优先（毫秒级推送），连续失败自动降级长轮询，
/// 3 分钟后重试 WS。心跳上报版本号。送达的消息由调用方（App.MessageRouter）分发。
/// </summary>
public sealed class PollingService : INotifyPropertyChanged
{
    public static PollingService Instance { get; } = new();

    private readonly ApiService _api = new();
    private CancellationTokenSource? _cts;

    public event Action<InboxMessage>? MessageReceived;
    public event PropertyChangedEventHandler? PropertyChanged;

    private CloudStatus _status = CloudStatus.Disabled;
    public CloudStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public string StatusText => Status switch
    {
        CloudStatus.Disabled => "未配置",
        CloudStatus.Connecting => "连接中…",
        CloudStatus.Online => "已连接",
        _ => "离线",
    };

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SettingsService.Instance.Values.ServerUrl)
        && !string.IsNullOrEmpty(SettingsService.Instance.Values.DeviceToken);

    public void Start()
    {
        if (!IsConfigured) return;
        ServerEndpoints.InitFrom(SettingsService.Instance.Values.ServerUrl);
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Run(_cts.Token));
        App.Log.Info("云端实时连接已启动");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Status = IsConfigured ? CloudStatus.Offline : CloudStatus.Disabled;
    }

    /// <summary>WS 实时通道开关：毫秒级推送；连续 3 次失败自动降级长轮询 3 分钟。</summary>
    public static bool WsEnabled = true;

    private async Task Run(CancellationToken ct)
    {
        Status = CloudStatus.Connecting;
        var wsFailures = 0;
        var longPollUntil = DateTime.MinValue;
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!WsEnabled || DateTime.UtcNow < longPollUntil)
                {
                    Status = CloudStatus.Online;
                    await LongPollCycle(ct);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct); // 轮询节流：稳定性优先
                    continue;
                }

                var connected = await WsSessionAsync(ct);
                if (ct.IsCancellationRequested) break;

                if (connected)
                {
                    wsFailures = 0;
                    backoff = TimeSpan.FromSeconds(2);
                    Status = CloudStatus.Connecting; // 会话结束，准备重连
                }
                else
                {
                    wsFailures++;
                    if (wsFailures >= 3)
                    {
                        wsFailures = 0;
                        longPollUntil = DateTime.UtcNow.AddMinutes(3);
                        App.Log.Info("WebSocket 连续失败，降级长轮询（3 分钟后重试 WS）");
                    }
                    Status = CloudStatus.Offline;
                    await Task.Delay(backoff, ct);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            // 注意：HttpClient 40s 超时抛的 TaskCanceledException 也是 OCE 子类，
            // 若无条件 break 会让轮询循环静默死亡（表现为"永远收不到消息"）
            catch (Exception ex)
            {
                App.Log.Aggregate("实时连接", ex);
                Status = CloudStatus.Offline;
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>一条 WebSocket 会话：连上后收推送直到断开。返回是否成功建立过连接。</summary>
    private async Task<bool> WsSessionAsync(CancellationToken ct)
    {
        var s = SettingsService.Instance.Values;
        var token = s.DeviceToken;
        if (string.IsNullOrEmpty(token)) return false;

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.SetRequestHeader("authorization", "Bearer " + token);

        var baseUri = new Uri(ServerEndpoints.Current);
        var wsScheme = baseUri.Scheme == "https" ? "wss" : "ws";
        var uri = new Uri($"{wsScheme}://{baseUri.Host}{(baseUri.IsDefaultPort ? "" : ":" + baseUri.Port)}/api/device/ws?after={s.Cursor}");
        App.Log.Info($"WS 连接（cursor={s.Cursor}）");
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await ws.ConnectAsync(uri, connectCts.Token);
        }
        catch (Exception ex)
        {
            App.Log.Aggregate("WS 连接", ex);
            return false;
        }

        Status = CloudStatus.Online;
        App.Log.Info("WebSocket 已连接（实时通道）");

        // WS 会话期间独立心跳（上报版本、维持在线状态）
        var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(() => HeartbeatLoop(heartbeatCts.Token));

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionCts.CancelAfter(TimeSpan.FromSeconds(40)); // 会话限长轮换
        try
        {
            var buffer = new byte[256 * 1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), sessionCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                    catch { }
                    return true;
                }
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                HandleServerMessage(text, ws);
            }
        }
        catch (OperationCanceledException) { /* 会话到期或服务停止 */ }
        catch (Exception ex)
        {
            App.Log.Aggregate("WS 会话", ex);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; } catch { }
        }
        return true;
    }

    private void HandleServerMessage(string json, ClientWebSocket ws)
    {
        try
        {
            App.Log.Info($"WS 帧到达：{json[..Math.Min(120, json.Length)]}");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "messages" && type != "backlog") return; // ready/pong 等

            var (msgs, maxSeq) = ApiService.ParseMessages(root);
            foreach (var m in msgs)
            {
                try { MessageReceived?.Invoke(m); }
                catch (Exception ex) { App.Log.Error("处理消息失败", ex); }
            }
            if (maxSeq > SettingsService.Instance.Values.Cursor)
            {
                SettingsService.Instance.Values.Cursor = maxSeq;
                SettingsService.Instance.Save();
                if (ws.State == WebSocketState.Open)
                {
                    var ack = Encoding.UTF8.GetBytes("{\"type\":\"ack\",\"after\":" + maxSeq + "}");
                    // 会话轮换会随时关闭 socket，ack 发送失败静默即可（下轮 backlog 兜底）
                    _ = ws.SendAsync(ack, WebSocketMessageType.Text, true, CancellationToken.None)
                        .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log.Error("WS 消息解析失败", ex);
        }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _api.HeartbeatAsync(); }
            catch { App.Log.Aggregate("心跳", null); }
            try { await Task.Delay(TimeSpan.FromSeconds(45), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private int _lpNoMessageLoops;
    private int _authFails;

    /// <summary>设备被解绑/账号注销后的自愈：连续 3 次 401 → 停止轮询并提示重新绑定。</summary>
    private void NoteAuthFailureIf401(Exception ex)
    {
        if (ex.Message?.Contains("401") != true) { _authFails = 0; return; }
        if (++_authFails < 3) return;
        _authFails = 0;
        Stop();
        App.Log.Error("设备已被解绑或账号已注销，轮询停止", null);
        App.Ui.BeginInvoke(new Action(() =>
        {
            TrayService.Instance.ShowBalloon("设备已解绑", "设备已被解绑或账号已注销。请打开 Classify 重新运行向导完成绑定。");
            ToastWindow.ShowToast("设备已解绑", "请打开 Classify 主界面 → 重新运行配置向导完成绑定");
        }));
    }

    /// <summary>长轮询单轮（WS 降级模式）。</summary>
    private async Task LongPollCycle(CancellationToken ct)
    {
        try
        {
            var (msgs, maxSeq) = await _api.GetMessagesAsync(SettingsService.Instance.Values.Cursor, wait: 15);
            foreach (var m in msgs)
            {
                try { MessageReceived?.Invoke(m); }
                catch (Exception ex) { App.Log.Error("处理消息失败", ex); }
            }
            if (maxSeq > SettingsService.Instance.Values.Cursor)
            {
                SettingsService.Instance.Values.Cursor = maxSeq;
                SettingsService.Instance.Save();
                await _api.AckAsync(maxSeq);
            }
            else if (++_lpNoMessageLoops >= 2)
            {
                _lpNoMessageLoops = 0;
                await _api.HeartbeatAsync();
            }
            _authFails = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            App.Log.Aggregate("云端轮询", ex);
            NoteAuthFailureIf401(ex);
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { }
        }
    }
}
