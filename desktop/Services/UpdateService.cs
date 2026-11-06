using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Classify.Services;

public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>
/// 自动更新（下载侧）：
/// - 默认“询问”：发现新版本 → 展示 changelog → 不更新 / 闲时更新 / 立即更新；
/// - 托盘开启“自动更新（静默）”后：发现即下载，校验通过后写 pending 并退出换装；
/// - 下载 → SHA256 校验 → 安全解压到 staging → 写 pending → 退出；
///   换装由伴生进程（或下次启动的主进程）完成，主进程零 shell、零子进程。
/// </summary>
public sealed class UpdateService
{
    private static readonly ApiService Api = new();
    private static CancellationTokenSource? _cts;
    private static int _checking;
    private static volatile bool _dialogOpen;

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
            var postponed = false;
            try { postponed = await CheckOnceAsync(manual: false, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { App.Log.Aggregate("自动更新", ex); }

            // 低于服务端最低版本 → 15 分钟；课堂中发现了更新 → 20 分钟后再试；否则 6 小时
            var belowMin = ApiService.ServerMinVersion is { } min
                && Version.TryParse(min, out var mv)
                && Version.TryParse(LogService.AppVersion, out var cv)
                && cv < mv;
            var delay = belowMin ? TimeSpan.FromMinutes(15)
                : postponed ? TimeSpan.FromMinutes(20)
                : TimeSpan.FromHours(6);
            try { await Task.Delay(delay, ct); } catch { return; }
        }
    }

    /// <summary>检查一次。返回 true 表示“有更新但因课堂中而推迟提示”。</summary>
    public static async Task<bool> CheckOnceAsync(bool manual = false, CancellationToken ct = default)
    {
        if (_dialogOpen) return false;
        if (Interlocked.Exchange(ref _checking, 1) == 1) return false;
        try
        {
            // 同步服务端最低版本要求（决定后续检查节奏）；自定义部署走当前端点
            try { await Api.GetVersionAsync(ServerEndpoints.Current); } catch { }

            var manifest = await Api.GetUpdateLatestAsync();
            if (manifest is null)
            {
                if (manual) Toast("检查更新失败", "无法获取版本信息");
                return false;
            }
            var current = LogService.AppVersion;
            if (!Version.TryParse(manifest.Version, out var newer) ||
                !Version.TryParse(current, out var cur) || newer <= cur)
            {
                if (manual) Toast("检查更新", "已是最新版本 " + current);
                return false;
            }
            if (!manual && manifest.Version == SettingsService.Instance.Values.SkippedVersion) return false;
            if (UpdateSwap.Pending()) return false; // 上一包未消费完，不叠加
            if (!IsSafeUrl(manifest.Url))
            {
                App.Log.Error("更新清单 URL 不是 https，已拒绝");
                return false;
            }
            if (!IsSha256(manifest.Sha256))
            {
                App.Log.Error("更新清单 sha256 格式无效，已拒绝");
                return false;
            }
            if (!CanWriteAppDir(out var writeError))
            {
                App.Log.Error("安装目录不可写，无法更新：" + writeError, null);
                if (manual) Toast("无法更新", "安装目录不可写，请以管理员身份运行安装程序或修复目录权限。");
                return false;
            }
            if (!HasFreeSpace(600L * 1024 * 1024, out var freeError))
            {
                App.Log.Error("磁盘空间不足，无法更新：" + freeError, null);
                if (manual) Toast("无法更新", freeError);
                return false;
            }

            if (SettingsService.Instance.Values.AutoUpdateSilent)
            {
                App.Log.Info($"发现新版本 {manifest.Version}（当前 {current}），静默下载…");
                if (await DownloadAndStageAsync(manifest, ct))
                {
                    if (WritePendingSafe(manifest.Version)) ExitForUpdate(manifest.Version);
                }
                return false;
            }

            // 上课中不打断：等下一轮（全屏课件状态由 2 秒轮询维护）
            if (!manual && ClassDetectService.Instance.InClass)
            {
                App.Log.Info($"发现新版本 {manifest.Version}，当前正在上课，推迟提示");
                return true;
            }

            try { App.Current.Dispatcher.Invoke(() => ShowUpdateDialog(manifest, manual)); }
            catch (Exception ex) { App.Log.Aggregate("更新弹窗", ex); }
            return false;
        }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    private static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static bool IsSha256(string value) =>
        !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    /// <summary>更新前先验证安装目录可写；Program Files 未授权时给出明确提示，而不是复制到一半失败。</summary>
    private static bool CanWriteAppDir(out string error)
    {
        error = "";
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, ".update-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool HasFreeSpace(long requiredBytes, out string error)
    {
        error = "";
        try
        {
            var root = Path.GetPathRoot(UpdateSwap.WorkDir);
            if (string.IsNullOrEmpty(root)) return true;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free >= requiredBytes) return true;
            error = $"磁盘剩余空间不足（需要约 {requiredBytes / 1024 / 1024} MB，当前 {free / 1024 / 1024} MB）。";
            return false;
        }
        catch { return true; } // 无法读取时不要阻止更新
    }

    private static bool WritePendingSafe(string version)
    {
        try
        {
            UpdateSwap.WritePending(UpdateSwap.Staging, AppContext.BaseDirectory, version);
            return true;
        }
        catch (Exception ex)
        {
            App.Log.Error("写入更新待换装标记失败", ex);
            Toast("更新失败", "无法写入更新标记：" + ex.Message);
            return false;
        }
    }

    private static async Task<bool> DownloadAndStageAsync(UpdateManifest manifest, CancellationToken ct)
    {
        App.Log.Info($"下载新版本 {manifest.Version}…");
        var workDir = UpdateSwap.WorkDir;
        var zipPath = Path.Combine(workDir, "package.zip");
        try
        {
            Directory.CreateDirectory(workDir);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var resp = await http.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var length = resp.Content.Headers.ContentLength;
            if (length is > 1_500L * 1024 * 1024)
            {
                App.Log.Error($"更新包过大（{length} bytes），已拒绝");
                return false;
            }
            var required = (length ?? 0) * 3 + 200L * 1024 * 1024;
            if (!HasFreeSpace(required, out var spaceError))
            {
                App.Log.Error("磁盘空间不足：" + spaceError, null);
                return false;
            }
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            await src.CopyToAsync(dst, ct);
        }
        catch (Exception ex)
        {
            App.Log.Aggregate("更新下载", ex);
            try { File.Delete(zipPath); } catch { }
            return false;
        }

        try
        {
            await using var fs = File.OpenRead(zipPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!string.Equals(hash, manifest.Sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                File.Delete(zipPath);
                App.Log.Error("更新包校验失败（sha256 不匹配），已丢弃");
                return false;
            }

            var staging = UpdateSwap.Staging;
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ExtractZipSafely(zipPath, staging);
            App.Log.Info($"更新 {manifest.Version} 已就绪（staging）。");
            return true;
        }
        catch (Exception ex)
        {
            App.Log.Error("更新包解压失败，已丢弃", ex);
            try { if (Directory.Exists(UpdateSwap.Staging)) Directory.Delete(UpdateSwap.Staging, true); } catch { }
            return false;
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    /// <summary>防 Zip Slip：逐个条目校验目标路径必须落在 staging 内。</summary>
    private static void ExtractZipSafely(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (stateIsDirectory(entry)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包含非法路径：" + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }

        static bool stateIsDirectory(ZipArchiveEntry e) =>
            e.FullName.EndsWith('/') || e.FullName.EndsWith('\\') || string.IsNullOrEmpty(e.Name);
    }

    /// <summary>非静默更新对话框：changelog + 不更新 / 闲时更新 / 立即更新。</summary>
    private static void ShowUpdateDialog(UpdateManifest manifest, bool manual)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;

        var changelog = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(manifest.Notes) ? "（无更新说明）" : manifest.Notes,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 210,
            FontSize = 12.5,
            Background = Brushes.White,
            Foreground = Brushes.Black,
        };
        var status = new TextBlock
        {
            Text = "闲时更新 = 现在下载，下次启动时自动换装，不打断当前课堂。",
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        Button B(string content, bool isDefault = false) => new()
        {
            Content = content,
            Width = 116,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0),
            IsDefault = isDefault,
        };
        var bSkip = B("不更新");
        var bIdle = B("闲时更新");
        var bNow = B("立即更新", true);
        row.Children.Add(bSkip);
        row.Children.Add(bIdle);
        row.Children.Add(bNow);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = $"发现新版本 v{manifest.Version}（当前 v{LogService.AppVersion}）",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "更新说明：",
            Margin = new Thickness(0, 12, 0, 6),
            FontSize = 12.5,
            Foreground = Brushes.Gray,
        });
        panel.Children.Add(changelog);
        panel.Children.Add(row);
        panel.Children.Add(status);

        var win = new Window
        {
            Title = "Classify · 软件更新",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            Content = panel,
        };
        win.Closed += (_, _) => _dialogOpen = false;

        void SetBusy(string text)
        {
            bSkip.IsEnabled = bIdle.IsEnabled = bNow.IsEnabled = false;
            status.Text = text;
        }

        void SetIdle(string text)
        {
            bSkip.IsEnabled = bIdle.IsEnabled = bNow.IsEnabled = true;
            status.Text = text;
        }

        bSkip.Click += (_, _) =>
        {
            SettingsService.Instance.Values.SkippedVersion = manifest.Version;
            SettingsService.Instance.Save();
            win.Close();
        };

        bIdle.Click += async (_, _) =>
        {
            SetBusy("正在下载…完成后将在下次启动 Classify 时换装。");
            try
            {
                if (await DownloadAndStageAsync(manifest, CancellationToken.None))
                {
                    if (!WritePendingSafe(manifest.Version)) { SetIdle("无法写入更新标记，请检查磁盘/权限。"); return; }
                    Toast("更新已就绪", "v" + manifest.Version + " 将在下次启动 Classify 时生效");
                    win.Close();
                }
                else SetIdle("下载或校验失败，请稍后重试。");
            }
            catch (Exception ex)
            {
                App.Log.Aggregate("闲时更新", ex);
                SetIdle("更新失败：" + ex.Message);
            }
        };

        bNow.Click += async (_, _) =>
        {
            SetBusy("正在下载…完成后将自动重启换装。");
            try
            {
                if (await DownloadAndStageAsync(manifest, CancellationToken.None))
                {
                    if (!WritePendingSafe(manifest.Version)) { SetIdle("无法写入更新标记，请检查磁盘/权限。"); return; }
                    ExitForUpdate(manifest.Version);
                }
                else SetIdle("下载或校验失败，请稍后重试。");
            }
            catch (Exception ex)
            {
                App.Log.Aggregate("立即更新", ex);
                SetIdle("更新失败：" + ex.Message);
            }
        };

        win.Show();
    }

    private static void Toast(string title, string text) =>
        App.Current.Dispatcher.Invoke(() => Classify.UI.ToastWindow.ShowToast(title, text));

    /// <summary>计划内退出：pending 已写好，伴生/下次启动完成换装。</summary>
    private static void ExitForUpdate(string version)
    {
        App.Log.Info($"更新换装：退出以应用新版本 {version}。");
        MetricsService.MarkCleanExit(); // 计划内退出，不报崩溃
        Environment.Exit(0);
    }
}

/// <summary>
/// 更新交换（执行侧，纯托管文件操作，零 shell、零子进程）：
/// pending.json 记录 staging 与安装目录。伴生进程每轮检查标记并执行换装；
/// 无伴生时（自启关闭）由下次启动的 App 在初始化前消费。
/// 运行中的 exe 无法被覆盖但可以被“改名”，故对主程序文件先改名腾出路径再复制；
/// 复制失败会回滚改名，避免把安装目录留在不可启动状态。
/// </summary>
public static class UpdateSwap
{
    public sealed record PendingUpdate(string Staging, string Target, string Version, long CreatedAt);

    public static string WorkDir => Path.Combine(AppPaths.DataDir, "update");
    public static string Staging => Path.Combine(WorkDir, "staging");
    private static string PendingPath => Path.Combine(WorkDir, "pending.json");
    private static string DoingPath => PendingPath + ".doing";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static bool Pending() => File.Exists(PendingPath) || File.Exists(DoingPath);

    public static PendingUpdate? ReadPending()
    {
        try { return JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingPath)); }
        catch { return null; }
    }

    public static void WritePending(string staging, string target, string version)
    {
        Directory.CreateDirectory(WorkDir);
        File.WriteAllText(PendingPath,
            JsonSerializer.Serialize(new PendingUpdate(staging, target, version,
                DateTimeOffset.Now.ToUnixTimeSeconds()), JsonOpts));
    }

    /// <summary>执行换装。返回是否完成（无标记/被他人认领/失败返回 false）。</summary>
    public static bool Apply(Action<string>? log = null)
    {
        var say = log ?? (_ => { });

        // 原子认领 pending.json → pending.json.doing，主进程与伴生不会同时换装。
        // 若只剩 .doing（上次换装中途崩溃），继续完成它。
        if (File.Exists(PendingPath))
        {
            try { File.Move(PendingPath, DoingPath, overwrite: true); }
            catch { return false; }
        }
        else if (!File.Exists(DoingPath)) return false;

        var pending = ReadPendingFrom(DoingPath);
        if (pending is null)
        {
            TryDelete(DoingPath);
            return false;
        }

        try
        {
            if (!Directory.Exists(pending.Staging))
            {
                say("staging 不存在，清除标记");
                TryDelete(DoingPath);
                return false;
            }

            // 等主进程退出（互斥体释放），最多 20 秒
            for (var i = 0; i < 40; i++)
            {
                if (MainGone()) break;
                Thread.Sleep(500);
            }

            var targetExe = Path.Combine(pending.Target, "Classify.exe");
            var oldExe = targetExe + ".old";
            try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }

            var copied = 0;
            var failed = 0;
            foreach (var src in Directory.EnumerateFiles(pending.Staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(pending.Staging, src);
                var dst = Path.Combine(pending.Target, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (string.Equals(dst, targetExe, StringComparison.OrdinalIgnoreCase))
                    {
                        // 运行中的主程序：先改名，复制成功才算数；失败立即回滚
                        if (!TryMoveRunningExe(targetExe, say)) { failed++; continue; }
                        try { File.Copy(src, dst, overwrite: true); copied++; }
                        catch
                        {
                            TryRestoreRunningExe(targetExe, say);
                            failed++;
                        }
                        continue;
                    }
                    File.Copy(src, dst, overwrite: true);
                    copied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    say($"copy failed {rel}: {ex.Message}");
                }
            }
            say($"update {pending.Version}: copied={copied} failed={failed}");

            // 主程序缺失时优先恢复旧镜像，绝不把安装目录留在半残状态
            if (!File.Exists(targetExe))
            {
                TryRestoreRunningExe(targetExe, say);
                if (!File.Exists(targetExe))
                {
                    say("FATAL: Classify.exe 缺失，保留 staging 供排查");
                    RestorePending();
                    return false;
                }
            }

            if (failed > 0)
            {
                say("存在复制失败，保留 staging 并重新排队，稍后重试");
                RestorePending();
                return false;
            }

            try { Directory.Delete(pending.Staging, true); } catch { }
            TryDelete(DoingPath);
            try { File.WriteAllText(Path.Combine(pending.Target, "UPDATE-SWAP-OK.txt"), pending.Version); } catch { }
            say("swap done");
            return true;
        }
        catch (Exception ex)
        {
            say($"swap error: {ex.Message}");
            TryRestoreRunningExe(Path.Combine(pending.Target, "Classify.exe"), say);
            RestorePending();
            return false;
        }
    }

    private static PendingUpdate? ReadPendingFrom(string path)
    {
        try { return JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(path)); }
        catch { return null; }
    }

    /// <summary>失败时把 .doing 放回 pending，让伴生/下次启动可以重试。</summary>
    private static void RestorePending()
    {
        try
        {
            if (File.Exists(DoingPath) && !File.Exists(PendingPath))
                File.Move(DoingPath, PendingPath, overwrite: true);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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

    private static void TryRestoreRunningExe(string exePath, Action<string> say)
    {
        try
        {
            var old = exePath + ".old";
            if (!File.Exists(exePath) && File.Exists(old))
            {
                File.Move(old, exePath);
                say("restored previous exe from .old");
            }
        }
        catch (Exception ex) { say($"restore exe failed: {ex.Message}"); }
    }
}
