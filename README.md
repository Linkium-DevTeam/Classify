# Classify

**Your Class, More Classify.** 面向教室一体机的免费开源课堂助手。

手机变成教室的遥控器：**远程喊话**（大屏全屏展示 + 语音朗读）、**文件快传**、**图片快投**、**早读看板**、**随机点名**、**课堂时钟**、**智能候课**。为学生隐私设计——学生姓名不出教室电脑，云端只存座位与计分。

> 本仓库为开源版（v4.0.1）。**Lite 极简版**（免安装、8 位口令、零配置）在 [`Lite` 分支](../../tree/Lite)。

## 组成

| 目录 | 说明 | 技术 |
|---|---|---|
| `server/` | 云端：实时通道（WebSocket + 长轮询降级）、设备绑定、消息收件箱（Durable Objects）、文件存储（R2）、教师 WebUI/PWA、官网与下载 | Cloudflare Workers + KV + Durable Objects + R2 |
| `desktop/` | 教室机客户端：托盘常驻、全屏喊话 + TTS、灵动岛、早读看板、智能候课、安全自更新 | WPF (.NET 10) |

## 自建部署

### 1. 云端（Cloudflare Workers）

```bash
cd server
npm install -g wrangler        # 或使用 npx
wrangler login

# 创建绑定资源（把 wrangler.toml 中的占位 ID 替换为实际值）
wrangler kv namespace create KV
wrangler r2 bucket create classcall-files

# 签发令牌 / 加密用的密钥（至少 32 位随机串）
wrangler secret put SECRET_KEY

# 可选：Turnstile 人机验证、独立管理员密钥
wrangler secret put TURNSTILE_SECRET_KEY
wrangler secret put ADMIN_KEY

wrangler deploy
```

自定义域名路由在 `wrangler.toml` 中默认注释，按需开启。

### 2. 教室机客户端

```bash
cd desktop
dotnet publish -c Release -r win-x64 --self-contained true
# 产物：bin/Release/net10.0-windows/win-x64/publish/
# 自包含发布体积较大，但教室电脑无需预装 .NET 10 Desktop Runtime，兼容性优先。
```

首次启动按向导配置服务端地址，扫码或输入配对令牌绑定教室机。

## 运行要求

- 教室机：Windows 10 1809+ / Windows 11，x64；安装 .NET 10 Desktop Runtime。
- 云端：Cloudflare 账号（Workers Paid 计划才能使用 Durable Objects；免费额度不足以支撑多设备常驻）。
- 手机端：现代浏览器；可“添加到主屏幕”作为 PWA 使用。

## 安全与隐私

- 密码 PBKDF2-SHA256 加盐存储；会话为 HMAC 无状态令牌，重置密码/封禁即刻吊销。
- 设备令牌与教师账号分离；解绑即失效。
- 文件默认 7 天清理，喊话最多保留 24 小时；学生姓名不出教室电脑。
- 遥测与启动诊断默认开启、可在设置关闭；上传前脱敏。

## 开发

```bash
node tools/version.mjs --check     # 版本号一致性
node server/scripts/check.mjs     # Worker 语法检查
node --test server/test/          # Worker 单元测试
dotnet build desktop/Classify.csproj -c Release -r win-x64
```

## 支持

觉得有用？[请作者喝杯奶茶](https://afdian.com/a/linkium) ♥

问题与建议：[Issues](../../issues)

## 许可证

[Apache License 2.0](LICENSE)
