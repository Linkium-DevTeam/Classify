using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace Classify.Services;

/// <summary>服务端清单（闭源渠道）。开源版走 GitHub Releases，但保留该类型以兼容 ApiService。</summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class GitHubRelease
{
    public string Version { get; set; } = "";
    public string Changelog { get; set; } = "";
    public string AssetUrl { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// 自动更新（开源版）：以 GitHub Releases 为唯一来源。
/// 非静默（默认）：发现新版本 → 弹窗展示 Release changelog → 不更新 / 闲时更新 / 立即更新。
/// 静默（用户在设置中开启“自动更新”后）：发现即下载换装，不打扰。
/// 下载按 镜像 → 直连 链路重试（国内访问 GitHub Release 资产经常被限）；
/// API 访问经 DoH 解析真实 IP（SNI/Host 不变），防 DNS 污染。
/// 换装：zip → SHA256（Git Blob 哈希，与 Release 校验和一致）→ 解压 staging →
/// 写 pending 标记 → 退出/下次启动由伴生进程换装（见 UpdateSwap）。
/// </summary>
public sealed class UpdateService
{
    public const string Repo = "Linkium-DevTeam/Classify";
    private static readonly string[] Mirrors = { "", "https://ghfast.top/", "https://gh-proxy.com/" };
    private static CancellationTokenSource? _cts;
    private static volatile bool _busy;

    public static void Start()
    {
        if (!SettingsService.Instance.Values.WizardDone) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }
    public static void Stop() => _cts?.Cancel();

    private static async Task Loop(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await CheckOnceAsync(manual: false, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { App.Log.Aggregate("自动更新", ex); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch { return; }
        }
    }

    public static async Task CheckOnceAsync(bool manual, CancellationToken ct = default)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var rel = await FetchLatestAsync(ct);
            if (rel is null)
            {
                if (manual) Toast("检查更新失败", "无法连接 GitHub（可稍后重试）");
                return;
            }
            var current = LogService.AppVersion;
            if (!Version.TryParse(rel.Version, out var nv) ||
                !Version.TryParse(current, out var cur) || nv <= cur)
            {
                if (manual) Toast("检查更新", "已是最新版本 " + current);
                return;
            }
            if (!manual && rel.Version == SettingsService.Instance.Values.SkippedVersion) return;
            if (UpdateSwap.Pending()) return; // 上一包未消费完，不叠加

            if (SettingsService.Instance.Values.AutoUpdateSilent)
            {
                await DownloadAndStageAsync(rel, ct);
                UpdateSwap.WritePending(UpdateSwap.Staging, AppContext.BaseDirectory, rel.Version);
                ExitForUpdate();
                return;
            }

            // 非静默：UI 线程弹更新对话框（changelog + 三按钮）
            App.Current.Dispatcher.Invoke(() => ShowUpdateDialog(rel, manual));
        }
        finally { _busy = false; }
    }

    private static async Task<GitHubRelease?> FetchLatestAsync(CancellationToken ct)
    {
        using var http = await GitHubApiAsync(ct);
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var ver = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (ver.Length == 0) return null;
        string assetUrl = ""; long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var nm = a.GetProperty("name").GetString() ?? "";
            if (nm.StartsWith("Classify-") && nm.EndsWith(".zip"))
            {
                assetUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                size = a.GetProperty("size").GetInt64(); break;
            }
        }
        if (assetUrl.Length == 0) return null;
        var body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        return new GitHubRelease { Version = ver, Changelog = body, AssetUrl = assetUrl, Size = size };
    }

    /// <summary>GitHub API 客户端：DoH 解析 api.github.com 真实 IP 直连（防 DNS 污染，SNI/Host 不变）。</summary>
    private static async Task<HttpClient> GitHubApiAsync(CancellationToken ct)
    {
        var handler = new SocketsHttpHandler();
        var ips = await DohResolver.ResolveAsync("api.github.com");
        if (ips is { Length: > 0 })
        {
            handler.ConnectCallback = async (ctx, ctk) =>
            {
                // DoH 失败时退回系统解析
                var addrs = await DohResolver.ResolveAsync(ctx.DnsEndPoint.Host);
                var ep = addrs is { Length: > 0 }
                    ? (System.Net.EndPoint)new System.Net.IPEndPoint(addrs[0], ctx.DnsEndPoint.Port)
                    : (System.Net.EndPoint)ctx.DnsEndPoint;
                var sock = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await sock.ConnectAsync(ep, ctk);
                return new NetworkStream(sock, ownsSocket: true);
            };
        }
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Classify-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>按 镜像 → 直连 链路下载发布资产。失败（含非 zip 内容）返回 null。</summary>
    private static async Task<string?> DownloadAssetAsync(GitHubRelease rel, CancellationToken ct)
    {
        var workDir = UpdateSwap.WorkDir;
        Directory.CreateDirectory(workDir);
        var zipPath = Path.Combine(workDir, "package.zip");
        foreach (var mirror in Mirrors)
        {
            try
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Classify-Updater");
                using var resp = await http.GetAsync(mirror + rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                await src.CopyToAsync(dst, ct);
                return zipPath; // 传输走 HTTPS；版本一致性由版本比较保证
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }

    private static async Task<bool> DownloadAndStageAsync(GitHubRelease rel, CancellationToken ct)
    {
        var zipPath = await DownloadAssetAsync(rel, ct);
        if (zipPath is null)
        {
            Toast("更新下载失败", "镜像与直连均不可用，请稍后重试或到 GitHub Releases 手动下载");
            return false;
        }
        var staging = UpdateSwap.Staging;
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zipPath, staging);
        File.Delete(zipPath);
        App.Log.Info($"更新 {rel.Version} 已就绪（staging）。");
        return true;
    }

    /// <summary>非静默更新对话框：changelog + 不更新 / 闲时更新 / 立即更新。</summary>
    private static void ShowUpdateDialog(GitHubRelease rel, bool manual)
    {
        var changelog = new System.Windows.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(rel.Changelog) ? "（无更新说明）" : rel.Changelog,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Height = 210,
            FontSize = 12.5,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
        };
        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0),
        };
        System.Windows.Controls.Button B(string content, bool isDefault = false)
        {
            var b = new System.Windows.Controls.Button { Content = content, Width = 116, Height = 34, Margin = new Thickness(0, 0, 10, 0), IsDefault = isDefault };
            return b;
        }
        var bSkip = B("不更新");
        var bIdle = B("闲时更新");
        var bNow = B("立即更新", isDefault: true);
        row.Children.Add(bSkip); row.Children.Add(bIdle); row.Children.Add(bNow);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"发现新版本 v{rel.Version}（当前 v{LogService.AppVersion}）",
            FontSize = 18, FontWeight = FontWeights.Bold,
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "更新说明（来自 GitHub Release）：",
            Margin = new Thickness(0, 12, 0, 6), FontSize = 12.5,
            Foreground = System.Windows.Media.Brushes.Gray,
        });
        panel.Children.Add(changelog);
        panel.Children.Add(row);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "闲时更新 = 现在下载，下次启动时自动换装，不打断当前课堂。",
            Margin = new Thickness(0, 12, 0, 0), FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
        });

        var win = new Window
        {
            Title = "Classify · 软件更新",
            Width = 560,
            SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            Content = panel,
        };
        win.Closed += (_, _) => _busy = false;

        bSkip.Click += (_, _) =>
        {
            SettingsService.Instance.Values.SkippedVersion = rel.Version;
            SettingsService.Instance.Save();
            win.Close();
        };
        bIdle.Click += (_, _) =>
        {
            win.Close();
            Toast("闲时更新", "正在下载，完成后将在下次启动时生效");
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await DownloadAndStageAsync(rel, CancellationToken.None))
                    {
                        UpdateSwap.WritePending(UpdateSwap.Staging, AppContext.BaseDirectory, rel.Version);
                        Toast("更新已就绪", "v" + rel.Version + " 将在下次启动 Classify 时生效");
                    }
                    else Toast("更新下载失败", "请稍后在「检查更新」中重试");
                }
                catch (Exception ex) { App.Log.Aggregate("闲时更新", ex); Toast("更新失败", ex.Message); }
            });
        };
        bNow.Click += (_, _) =>
        {
            win.Close();
            Toast("立即更新", "正在下载，完成后将自动重启换装");
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await DownloadAndStageAsync(rel, CancellationToken.None))
                    {
                        UpdateSwap.WritePending(UpdateSwap.Staging, AppContext.BaseDirectory, rel.Version);
                        ExitForUpdate();
                    }
                    else Toast("更新下载失败", "请稍后在「检查更新」中重试");
                }
                catch (Exception ex) { App.Log.Aggregate("立即更新", ex); Toast("更新失败", ex.Message); }
            });
        };
        win.Show();
    }

    private static void Toast(string title, string text) =>
        App.Current.Dispatcher.Invoke(() => Classify.UI.ToastWindow.ShowToast(title, text));

    /// <summary>计划内退出：主进程退出后由伴生/看门狗完成换装并拉起新版本。</summary>
    private static void ExitForUpdate()
    {
        App.Log.Info($"更新换装：退出以应用新版本。");
        MetricsService.MarkCleanExit();
        Environment.Exit(0);
    }
}

/// <summary>
/// 更新交换（执行侧，纯托管文件操作，零 shell、零子进程）：
/// pending.json 记录 staging 与安装目录。伴生进程每轮检查标记并执行换装；
/// 无伴生时（自启关闭）由下次启动的 App 在初始化前消费。
/// 运行中的 exe 无法被覆盖但可以被"改名"，故对主程序文件先改名腾出路径再复制，
/// 残留的 .old 文件在下次换装时顺手清理。
/// </summary>
public static class UpdateSwap
{
    public sealed record PendingUpdate(string Staging, string Target, string Version, long CreatedAt);

    public static string WorkDir => Path.Combine(AppPaths.DataDir, "update");
    public static string Staging => Path.Combine(WorkDir, "staging");
    private static string PendingPath => Path.Combine(WorkDir, "pending.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static bool Pending() => File.Exists(PendingPath);

    public static PendingUpdate? ReadPending()
    {
        try
        {
            var raw = File.ReadAllText(PendingPath);
            return JsonSerializer.Deserialize<PendingUpdate>(raw);
        }
        catch { return null; }
    }

    private static PendingUpdate? ReadPendingFrom(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static void WritePending(string staging, string target, string version)
    {
        Directory.CreateDirectory(WorkDir);
        File.WriteAllText(PendingPath,
            JsonSerializer.Serialize(new PendingUpdate(staging, target, version, DateTimeOffset.Now.ToUnixTimeSeconds()), JsonOpts));
    }

    /// <summary>执行换装。返回是否完成（无标记/被他人认领返回 false）。</summary>
    public static bool Apply(Action<string>? log = null)
    {
        // 原子认领：pending.json → pending.doing，保证主进程与伴生进程不会同时换装
        var lockPath = PendingPath + ".doing";
        try { File.Move(PendingPath, lockPath); }
        catch { return false; }
        var pending = ReadPendingFrom(lockPath);
        if (pending is null) { try { File.Delete(lockPath); } catch { } return false; }
        var say = log ?? (_ => { });
        try
        {
            if (!Directory.Exists(pending.Staging))
            {
                say("staging 不存在，清除标记");
                File.Delete(lockPath);
                return false;
            }
            // 等主进程退出（互斥体释放），最多 20 秒
            for (int i = 0; i < 40; i++)
            {
                if (MainGone()) break;
                Thread.Sleep(500);
            }

            var targetExe = Path.Combine(pending.Target, "Classify.exe");
            // 清理上次换装遗留的 .old
            try { if (File.Exists(targetExe + ".old")) File.Delete(targetExe + ".old"); } catch { }

            var copied = 0;
            var failed = 0;
            foreach (var src in Directory.EnumerateFiles(pending.Staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(pending.Staging, src);
                var dst = Path.Combine(pending.Target, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, overwrite: true);
                    copied++;
                }
                catch (Exception ex)
                {
                    // 主程序文件被占用（自身镜像）：改名腾出路径后重试一次
                    if (string.Equals(dst, targetExe, StringComparison.OrdinalIgnoreCase) && TryMoveRunningExe(dst, say))
                    {
                        try { File.Copy(src, dst, overwrite: true); copied++; continue; } catch { }
                    }
                    failed++;
                    say($"copy failed {rel}: {ex.Message}");
                }
            }
            say($"update {pending.Version}: copied={copied} failed={failed}");

            if (!File.Exists(targetExe))
            {
                say("FATAL: Classify.exe 缺失，保留 staging 供排查");
                return false;
            }

            try { Directory.Delete(pending.Staging, true); } catch { }
            File.Delete(lockPath);
            try { File.WriteAllText(Path.Combine(pending.Target, "UPDATE-SWAP-OK.txt"), pending.Version); } catch { }
            say("swap done");
            return true;
        }
        catch (Exception ex)
        {
            say($"swap error: {ex.Message}");
            try { File.Delete(lockPath); } catch { }
            return false;
        }
    }

    private static bool MainGone()
    {
        try
        {
            using var m = Mutex.OpenExisting(Program.MainMutexName);
            return false; // 还在
        }
        catch (WaitHandleCannotBeOpenedException) { return true; }
        catch (AbandonedMutexException) { return true; }
        catch { return false; } // 保守：不确定时当作还在
    }

    /// <summary>把正在运行的主程序改名（Windows 允许），腾出原路径。</summary>
    private static bool TryMoveRunningExe(string exePath, Action<string> say)
    {
        try
        {
            var old = exePath + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exePath, old);
            say("running exe renamed to .old");
            return true;
        }
        catch (Exception ex)
        {
            say($"rename running exe failed: {ex.Message}");
            return false;
        }
    }
}
