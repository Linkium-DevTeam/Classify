using System.Text.Json;
using Classify.Models;

namespace Classify.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Values { get; private set; } = new();
    public string FilePath { get; }

    private SettingsService(string path) => FilePath = path;

    public static SettingsService Instance { get; private set; } = null!;

    public static SettingsService Load()
    {
        var path = Path.Combine(AppPaths.DataDir, "settings.json");
        var svc = new SettingsService(path);
        try
        {
            if (File.Exists(path))
            {
                var raw = File.ReadAllText(path);
                svc.Values = JsonSerializer.Deserialize<AppSettings>(raw) ?? new AppSettings();
            }
            else App.Log.Info("设置文件不存在，使用默认值");
        }
        catch (Exception ex)
        {
            App.Log.Error("读取设置失败，使用默认值", ex);
            try
            {
                // 保留损坏文件供排查，避免下一次保存直接覆盖
                File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            }
            catch { }
            svc.Values = new AppSettings();
        }
        Instance = svc;
        return svc;
    }

    /// <summary>
    /// 原子保存：先写临时文件再替换，避免进程被杀时 settings.json 半写
    /// （历史上引发过 DeviceToken/DeviceId 不一致断流）。杀软/索引器短暂占用时重试。
    /// </summary>
    public void Save()
    {
        var tmp = FilePath + ".tmp";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(Values, JsonOpts));
                File.Move(tmp, FilePath, overwrite: true);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                {
                    App.Log.Error("保存设置失败", ex);
                    // 最后尝试非原子覆盖，至少保住这次修改
                    try { File.Copy(tmp, FilePath, overwrite: true); File.Delete(tmp); } catch { }
                }
                else Thread.Sleep(50);
            }
        }
    }
}
