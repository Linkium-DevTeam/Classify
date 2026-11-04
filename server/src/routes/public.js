/**
 * 公开路由（全部在 requireUser 之前）：诊断 / 版本 / 前端配置 / 公告 / 遥测 /
 * 更新清单 / 密钥保护的运维接口。
 *
 * 返回 Response 表示已处理，返回 null 表示交给后续路由。
 */

import { json, fail, readJson } from "../lib/http.js";
import { rateLimit } from "../lib/rate-limit.js";
import { storeCall } from "../lib/store.js";
import { VERSION, LITE_VERSION, BUILD } from "../version.js";

/** 判断 X-Admin-Key 是否为部署者密钥（SECRET_KEY 或 ADMIN_KEY） */
function isAdminKey(env, req) {
  const key = req.headers.get("x-admin-key");
  return !!env.SECRET_KEY && !!key && (key === env.SECRET_KEY || key === env.ADMIN_KEY);
}

export async function handlePublic(req, env, url, ip) {
  const path = url.pathname, method = req.method;

  if (method === "POST" && path === "/api/diag/boot") {
    // 超早期启动记录回传（无鉴权但限流：单 IP 每小时 10 次，TTL 7 天自动清理）
    if (!await rateLimit(env, `diag:${ip}`, 10, 3600)) return json({ ok: true });
    const raw = await req.text();
    // 体积上限 16KB；控制字符（换行/制表/ESC 等）替换为空格，避免日志注入与终端转义
    if (raw && raw.length <= 16 * 1024) {
      const key = `diag:boot:${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
      await env.KV.put(key, raw.replace(/[\u0000-\u001F\u007F]/g, " ").slice(0, 16 * 1024), {
        expirationTtl: 7 * 86400,
      });
    }
    return json({ ok: true });
  }

  if (method === "GET" && path === "/api/version") {
    return json({
      name: env.APP_NAME || "ClassCall Lite",
      version: VERSION,
      liteVersion: LITE_VERSION,
      minVersion: env.MIN_APP_VERSION || "",
      serverTime: Date.now(),
      build: BUILD,
    });
  }

  // 前端配置：Turnstile 站点密钥（配置了才启用人机验证）
  if (method === "GET" && path === "/api/config") {
    return json({
      name: env.APP_NAME || "ClassCall Lite",
      turnstileSiteKey: env.TURNSTILE_SITE_KEY || "",
    });
  }

  // 服务公告（公开，无需登录；设备端与 WebUI 均拉取；过期自动视为无公告）
  if (method === "GET" && path === "/api/broadcast") {
    const raw = await env.KV.get("broadcast");
    if (!raw) return json({ broadcast: null });
    try {
      const b = JSON.parse(raw);
      if (!b || (b.expiresAt && Date.now() > b.expiresAt)) return json({ broadcast: null });
      return json({ broadcast: { id: b.id, text: b.text, level: b.level || "info", createdAt: b.createdAt || 0 } });
    } catch { return json({ broadcast: null }); }
  }

  // 匿名遥测（用户可开关）：仅版本与计数，无任何个人身份信息。
  // kind=crash 为崩溃上报：脱敏日志 detail ≤4000 字，每日每 IP ≤5 条，卷存 30 天。
  if (method === "POST" && path === "/api/telemetry") {
    if (!await rateLimit(env, `tel:${ip}`, 12, 3600)) return json({ ok: true });
    const { kind, version, events, detail } = await readJson(req);
    if (!["app", "web", "crash"].includes(kind)) return fail(400, "bad_kind", "kind 无效");
    const day = new Date().toISOString().slice(0, 10).replace(/-/g, "");
    if (kind === "crash") {
      if (!await rateLimit(env, `crash:${ip}`, 5, 3600)) return json({ ok: true });
      const key = `crash:${day}`;
      const list = JSON.parse(await env.KV.get(key) || "[]");
      if (list.length < 20) {
        list.push({
          at: Date.now(),
          version: String(version || "?").slice(0, 16),
          detail: String(detail || "").slice(0, 4000),
        });
        await env.KV.put(key, JSON.stringify(list), { expirationTtl: 30 * 86400 });
      }
      return json({ ok: true });
    }
    const key = `tel:${day}:${kind}`;
    const cur = JSON.parse(await env.KV.get(key) || "{}");
    cur.count = (cur.count || 0) + 1;
    cur.versions = cur.versions || {};
    const v = String(version || "?").slice(0, 16);
    cur.versions[v] = (cur.versions[v] || 0) + 1;
    if (Array.isArray(events)) {
      cur.events = cur.events || {};
      for (const e of events.slice(0, 20)) {
        if (e && typeof e.name === "string" && e.name.length <= 40)
          cur.events[e.name.slice(0, 40)] = (cur.events[e.name.slice(0, 40)] || 0) + (parseInt(e.count, 10) || 1);
      }
    }
    await env.KV.put(key, JSON.stringify(cur), { expirationTtl: 90 * 86400 });
    return json({ ok: true });
  }

  // 自动更新清单（公开读取；发布走 admin）
  if (method === "GET" && path === "/api/update/latest") {
    const raw = await env.KV.get("update:latest");
    if (!raw) return json({ version: "none" });
    return new Response(raw, { headers: { "content-type": "application/json", "cache-control": "no-store" } });
  }

  // 发布自动更新清单（仅部署者）
  if (method === "POST" && path === "/api/admin/update") {
    if (!isAdminKey(env, req)) return fail(403, "forbidden", "需要管理员密钥");
    const body = await readJson(req);
    if (!body.version || !body.url || !body.sha256) return fail(400, "bad_request", "缺少 version/url/sha256");
    const manifest = {
      version: String(body.version).slice(0, 16),
      url: String(body.url).slice(0, 300),
      sha256: String(body.sha256).slice(0, 128),
      notes: String(body.notes || "").slice(0, 500),
      publishedAt: Date.now(),
    };
    await env.KV.put("update:latest", JSON.stringify(manifest));
    return json({ ok: true, manifest });
  }

  // 运维总览（仅部署者：X-Admin-Key = SECRET_KEY 或 ADMIN_KEY）
  if (method === "GET" && path === "/api/admin/overview") {
    if (!isAdminKey(env, req)) return fail(403, "forbidden", "需要管理员密钥");
    const store = await storeCall(env, "/devices/all", {});
    const users = [];
    try {
      let cursor;
      do {
        const page = await env.KV.list({ prefix: "user:", cursor });
        for (const k of page.keys) {
          const raw = await env.KV.get(k.name);
          if (raw) {
            const rec = JSON.parse(raw);
            users.push({ username: rec.username, uid: rec.uid, created: rec.created });
          }
        }
        cursor = page.list_complete ? undefined : page.cursor;
      } while (cursor);
    } catch {}
    return json({
      users,
      devices: (store.body.devices || []).map(d => ({
        did: d.did, ownerUid: d.uid, name: d.name, version: d.version || "",
        createdAt: d.createdAt, lastSeen: d.lastSeen,
      })),
      links: store.body.links || [],
      serverTime: Date.now(),
    });
  }

  return null;
}
