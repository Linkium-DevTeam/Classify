# Classify Lite

**Introducing Classify & Lite** 的 Lite 部分——极简远程喊话：教室电脑常驻托盘显示 8 位设备口令，手机输入口令即可全屏发送并语音朗读。免安装、免注册、零配置。

> 完整版（喊话/文件/看板/点名/候课全功能）与服务端在 [`main` 分支](../../tree/main)。

## 组成

| 目录 | 说明 | 技术 |
|---|---|---|
| `desktop/` | 教室机端：单文件 WPF 应用（约 300 行），托盘常驻显示设备口令，收到喊话全屏置顶展示 + 本地 TTS 朗读；设备口令由机器指纹派生，每台固定 | WPF (.NET 10) + System.Speech |
| `worker/` | 云端中继：register / poll / send / info 四个接口，KV 取走即删 | Cloudflare Workers + KV |

## 部署

### 云端

```bash
cd worker
npx wrangler login
npx wrangler kv namespace create KV    # 将输出的 id 填入 wrangler.toml
npx wrangler deploy
```

### 教室机

```bash
cd desktop
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# 产物：bin/Release/net10.0-windows/win-x64/publish/Clite.exe（单文件）
```

把 `Clite.exe` 放到教室电脑双击即可，托盘里能看到 8 位口令。手机打开 Worker 域名（或在 `desktop/Program.cs` 中修改 `SERVER` 常量指向自建服务端），输入口令发送。

## 支持

[请作者喝杯奶茶](https://afdian.com/a/linkium) ♥ · [Issues](../../issues)

## 许可证

[Apache Lisence 2.0](LICENSE)
