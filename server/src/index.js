/**
 * ClassCall Lite — 教师喊话/传文件 云端中继 (Cloudflare Worker) 入口。
 *
 * 多教师多设备共用同一套 Worker：
 *  - 教师账号：注册/登录，密码 PBKDF2 存储，会话为 HMAC 无状态令牌
 *  - 设备：WebUI 生成 6 位配对码 → 桌面端兑换 → 设备令牌（可吊销，版本号校验）
 *  - 消息：每设备一个 InboxDO（SQLite 收件箱，支持长轮询 / WebSocket）
 *  - 文件：R2 存储，全局 GlobalStoreDO 登记元数据，定时任务按保留期清理
 *
 * 模块划分：app.js（编排）/ routes/*（路由）/ lib/*（基础能力）/ do/*（Durable Objects）。
 * 顶层错误处理：BanError → 403，其他 → 500。
 */

import { handle } from "./app.js";
import { fail } from "./lib/http.js";
import { storeCall } from "./lib/store.js";

export { GlobalStoreDO } from "./do/GlobalStoreDO.js";
export { InboxDO } from "./do/InboxDO.js";

export default {
  async fetch(req, env) {
    try { return await handle(req, env); }
    catch (e) {
      if (e && e.banned) return fail(403, "banned", String(e.message || "账号已被封禁"));
      return fail(500, "internal", String(e && e.message || e));
    }
  },

  // 每日清理：过期文件（R2 对象 + 登记表）、超期看板快照（48h，含隐私脱敏策略）、30 天前反馈
  async scheduled(event, env) {
    const days = parseInt(env.FILE_RETENTION_DAYS || "7", 10);
    const { body } = await storeCall(env, "/files/prune", { days });
    for (const f of (body && body.pruned) || []) {
      try { await env.FILES.delete(`files/${f.uid}/${f.fid}`); } catch {}
    }
    await storeCall(env, "/readboard/prune", { hours: 48 }).catch(() => {});
    try {
      const cutoff = Date.now() - 30 * 86400 * 1000;
      let cursor;
      do {
        const page = await env.KV.list({ prefix: "fb:", cursor });
        for (const k of page.keys) {
          const d = k.name.slice(3);
          const t = Date.parse(`${d.slice(0, 4)}-${d.slice(4, 6)}-${d.slice(6, 8)}`);
          if (!isNaN(t) && t < cutoff) await env.KV.delete(k.name);
        }
        cursor = page.list_complete ? undefined : page.cursor;
      } while (cursor);
    } catch {}
  },
};
