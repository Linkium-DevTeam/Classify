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
            svc.Values = new AppSettings();
        }
        Instance = svc;
        return svc;
    }

    /// <summary>原子保存：先写临时文件再替换，避免进程被杀时 settings.json 半写（历史上引发过 DeviceToken/DeviceId 不一致断流）。</summary>
    public void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Values, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log.Error("保存设置失败", ex);
        }
    }
}
