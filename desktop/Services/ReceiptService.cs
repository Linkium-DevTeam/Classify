namespace Classify.Services;

/// <summary>回执上报：received（设备收到）/ displayed（已展示或文件已落盘）/ failed。失败静默（聚合日志）。</summary>
public static class ReceiptService
{
    private static readonly ApiService Api = new();

    public static void Send(string? mid, string status)
    {
        if (string.IsNullOrWhiteSpace(mid)) return;
        _ = Task.Run(async () =>
        {
            try { await Api.SendReceiptAsync(mid, status); }
            catch (Exception ex) { App.Log.Aggregate("回执上报", ex); }
        });
    }
}
