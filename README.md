# Classify

**Your Class, More Classify.** 面向教室一体机的免费开源课堂助手。

手机变成教室的遥控器：**远程喊话**（大屏全屏展示 + 语音朗读）、**文件快传**、**图片快投**、**早读看板**、**随机点名**、**课堂时钟**、**智能候课**。为学生隐私设计——学生姓名不出教室电脑，云端只存座位与计分。

> 本仓库为开源版。**Lite 极简版**（免安装、8 位口令、零配置）在 [`Lite` 分支](../../tree/Lite)。

## 组成

| 目录 | 说明 | 技术 |
|---|---|---|
| `server/` | 云端：实时通道（WebSocket + 长轮询降级）、设备绑定、消息收件箱（Durable Objects）、文件存储（R2）、教师 WebUI（PWA） | Cloudflare Workers + KV + Durable Objects + R2 |
| `desktop/` | 教室机客户端：托盘常驻、全屏喊话 + TTS 朗读、灵动岛通知条、早读看板、智能候课、自动更新 | WPF (.NET 10) |

## 自建部署

### 1. 云端（Cloudflare Workers）

```bash
cd server
npm install -g wrangler        # 或使用 npx
wrangler login
# 创建绑定资源（按 wrangler.toml 中的占位替换 ID）：
wrangler kv namespace create KV
wrangler r2 bucket create classcall-files
# 设置签名密钥（注册/令牌签发）：
wrangler secret put SECRET_KEY
wrangler deploy
```

`wrangler.toml` 中的自定义域名路由默认注释，按需开启。

### 2. 教室机客户端

```bash
cd desktop
dotnet publish -c Release -r win-x64 --self-contained false
# 产物在 bin/Release/net10.0-windows/publish/
```

首次启动按向导配置服务端地址，扫码或输入配对令牌绑定教室机。

## 分支说明

- `main`：完整版客户端 + 服务端（本分支）
- `Lite`：[Classify Lite](../../tree/Lite) —— 极简远程喊话：免安装单文件，教室机常驻托盘显示 8 位设备口令，手机输入口令即可全屏发送

## 支持

觉得有用？[请作者喝杯奶茶](https://afdian.com/a/linkium) ♥

问题与建议：[Issues](../../issues)

## 许可证

[Apache Lisence 2.0](LICENSE)
