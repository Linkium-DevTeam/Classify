using System.Collections.Generic;

namespace Classify.Models;

/// <summary>settings.json（%AppData%\Classify）——字段与 2.x 完全一致，升级零迁移、免重新绑定。</summary>
public sealed class AppSettings
{
    public bool WizardDone { get; set; }
    public string AgreedVersion { get; set; } = "";

    // 云端
    public string ServerUrl { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public long Cursor { get; set; }

    // 语音（Azure 神经语音优先，本地 SAPI5 兜底）
    public bool TtsEnabled { get; set; } = true;
    public string TtsVoiceId { get; set; } = "";
    public double TtsRate { get; set; } = 1.0;
    public bool ChimeEnabled { get; set; } = true;
    /// <summary>自定义提示音 WAV 路径；留空使用内置 chime.wav。</summary>
    public string ChimePath { get; set; } = "";

    // 行为
    public int OverlaySeconds { get; set; } = 20;
    public int PopupSeconds { get; set; } = 12;
    public bool SaveToDesktop { get; set; } = true;
    public bool AllowVolume { get; set; } = true;
    public bool AllowLock { get; set; } = true;
    public bool AllowPower { get; set; }
    /// <summary>远程拍照回传（隐私敏感，默认关闭；开启后能力位随心跳同步给 WebUI）。</summary>
    public bool AllowCamera { get; set; }
    public string FullscreenProcesses { get; set; } = "POWERPNT,wpp,wps,et,EasiNote5";

    // 自启动（task / task-ps / registry / ""）
    public string AutoStartMethod { get; set; } = "";

    // 剪贴板同步
    public bool AllowClipboard { get; set; } = true;

    // 灵动岛常驻通知条
    public bool IslandEnabled { get; set; } = true;

    // EasiCare 评价联动（依赖闭源组件 EasiCareHelper，默认关闭）
    public bool EasiCareEnabled { get; set; } = false;

    // 自动更新：默认非静默（弹窗三按钮）；开启后回到“发现即下载换装”的静默模式
    public bool AutoUpdateSilent { get; set; } = false;

    // “不更新”跳过的版本号（每次新版本重置）
    public string SkippedVersion { get; set; } = "";

    // 硬件指纹设备 ID（16 位，重装不变）
    public string HwId { get; set; } = "";

    // 匿名遥测（可开关）
    public bool TelemetryEnabled { get; set; } = true;

    // 喊话时压低其他声音（ducking）——可能打断课件声音，默认关闭
    public bool DuckingEnabled { get; set; }

    // Azure 神经语音（晓晓 Online Natural）
    public bool AzureEnabled { get; set; }
    public string AzureRegion { get; set; } = "eastasia";
    public string AzureKey { get; set; } = "";
    public string AzureVoice { get; set; } = "zh-CN-XiaoxiaoNeural";

    // 主窗口位置记忆（null = 居中默认）
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    // 已展示过的服务公告（新 id 才弹托盘气泡）
    public string LastBroadcastId { get; set; } = "";

    // 勿扰模式截止时间（期间喊话只展示不朗读、无提示音；托盘开关，30 分钟一档）
    public DateTime DndUntil { get; set; }
}

public sealed class InboxMessage
{
    public long Seq { get; set; }
    public string Mid { get; set; } = "";
    public string RawPayload { get; set; } = "{}";
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Speak { get; set; } = true;
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string CmdName { get; set; } = "";
    public string? CmdValue { get; set; }
    public string From { get; set; } = "";
    public long Created { get; set; }
}

public sealed class AnnItem
{
    public string Mid { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Speak { get; set; } = true;
    public string From { get; set; } = "";
}

public sealed class ActivityItem
{
    public string Icon { get; set; } = "✨";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class VoiceItem
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Language { get; set; } = "";
    /// <summary>sapi = 本地 SAPI5（含讲述人自然语音）；azure = Azure 神经语音。</summary>
    public string Engine { get; set; } = "sapi";
    public override string ToString() =>
        $"{DisplayName} ({Language}){(Engine == "azure" ? " · Azure" : "")}";
}
