/**
 * Classify Lite — 极简远程喊话 relay
 *
 * 无账号系统。设备首次运行时从硬件指纹生成 8 位数字口令，自动注册。
 * 教师在 WebUI 输入口令 + 文字 → 设备轮询取到后全屏展示 + TTS。
 *
 * KV Schema:
 *   dev:{code} → JSON { hwid, name, lastSeen, ver }
 *   msg:{code} → JSON [{ text, ts }]   (设备取走即删)
 */

import { VERSION, BUILD } from "./version.js";

const JSON_HEADERS = {
  "content-type": "application/json; charset=utf-8",
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

const json = (data, status = 200) =>
  new Response(JSON.stringify(data), { status, headers: JSON_HEADERS });

const jsonH = (data) => new Response(JSON.stringify(data), { headers: JSON_HEADERS });

/** 尽力而为的 KV 限流（写额度尽时放行，避免把正常设备挡在门外） */
async function rateLimit(env, bucket, limit, windowSec) {
  try {
    const win = Math.floor(Date.now() / 1000 / windowSec);
    const key = `rl:${bucket}:${win}`;
    const n = parseInt(await env.KV.get(key) || "0", 10) + 1;
    await env.KV.put(key, String(n), { expirationTtl: windowSec });
    return n <= limit;
  } catch { return true; }
}

function safeParse(raw, fallback) {
  try { return JSON.parse(raw); } catch { return fallback; }
}

function assetsResponse(resp) {
  const headers = new Headers(resp.headers);
  headers.set("x-content-type-options", "nosniff");
  headers.set("referrer-policy", "no-referrer");
  headers.set("content-security-policy",
    "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
    "connect-src 'self'; object-src 'none'; base-uri 'none'");
  return new Response(resp.body, { status: resp.status, headers });
}

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const path = url.pathname;
    const ip = req.headers.get("cf-connecting-ip") || "unknown";

    if (!path.startsWith("/api/")) {
      return assetsResponse(await env.ASSETS.fetch(req));
    }

    if (req.method === "GET" && path === "/api/health") {
      return json({ ok: true, version: VERSION, build: BUILD, serverTime: Date.now() });
    }

    const code = (url.searchParams.get("code") || req.headers.get("x-code") || "").replace(/\D/g, "").slice(0, 8);

    /* ---- 设备注册（首次运行自动调用） ---- */
    if (req.method === "POST" && path === "/api/register") {
      if (!await rateLimit(env, "reg:" + ip, 30, 3600))
        return json({ error: "too many requests" }, 429);
      const body = await req.json().catch(() => ({}));
      const c = (body.code || "").replace(/\D/g, "").slice(0, 8);
      if (c.length !== 8) return json({ error: "code must be 8 digits" }, 400);
      const key = "dev:" + c;
      const existing = await env.KV.get(key);
      const info = existing ? safeParse(existing, {}) : {};
      info.hwid = (body.hwid || info.hwid || "").slice(0, 64);
      info.name = (body.name || info.name || "Clite Device").slice(0, 32);
      info.lastSeen = Date.now();
      info.ver = (body.ver || info.ver || "").slice(0, 16);
      try {
        await env.KV.put(key, JSON.stringify(info), { expirationTtl: 365 * 86400 });
      } catch { } // 写额度尽也返回 ok：设备凭固定口令照常 poll
      return jsonH({ ok: true, code: c, name: info.name });
    }

    /* ---- 设备轮询（取走即删） ---- */
    if (req.method === "GET" && path === "/api/poll") {
      if (code.length !== 8) return json({ error: "bad code" }, 400);
      const msgKey = "msg:" + code;
      let raw = null;
      try { raw = await env.KV.get(msgKey); } catch { }
      const msgs = raw ? safeParse(raw, []) : [];
      if (!Array.isArray(msgs)) return jsonH({ messages: [] });
      if (msgs.length) { try { await env.KV.delete(msgKey); } catch { } }
      // touch lastSeen：10 分钟内不重复写 —— 免费档 KV 写仅 1000 次/天，
      // 每 3s 一写当天就打爆（2026-10-03 实测打爆过，poll 500）。全部 try/catch，额度尽也照常返回
      try {
        const devRaw = await env.KV.get("dev:" + code);
        if (devRaw) {
          const dev = safeParse(devRaw, {});
          if (Date.now() - (dev.lastSeen || 0) > 600_000) {
            dev.lastSeen = Date.now();
            await env.KV.put("dev:" + code, JSON.stringify(dev), { expirationTtl: 365 * 86400 });
          }
        }
      } catch { }
      return jsonH({ messages: msgs });
    }

    /* ---- 教师发送喊话（无需登录，知道口令即可） ---- */
    if (req.method === "POST" && path === "/api/send") {
      if (!await rateLimit(env, "send:" + ip, 60, 3600))
        return json({ error: "发送过于频繁，请稍后再试" }, 429);
      const body = await req.json().catch(() => ({}));
      const c = (body.code || "").replace(/\D/g, "").slice(0, 8);
      if (c.length !== 8) return json({ error: "需要 8 位设备口令" }, 400);
      const text = (body.text || "").trim();
      if (!text || text.length > 2000) return json({ error: "内容不能为空（≤2000 字）" }, 400);
      const devRaw = await env.KV.get("dev:" + c);
      if (!devRaw) return json({ error: "设备不存在（口令错误或设备未上线）" }, 404);
      const msgKey = "msg:" + c;
      const msgs = safeParse(await env.KV.get(msgKey) || "[]", []);
      if (!Array.isArray(msgs)) return json({ error: "消息队列异常，请稍后重试" }, 500);
      msgs.push({ text, ts: Date.now() });
      try {
        await env.KV.put(msgKey, JSON.stringify(msgs.slice(-20)), { expirationTtl: 86400 });
      } catch {
        return json({ error: "今日云端写入额度已用尽（每日重置），请明天再试" }, 503);
      }
      return jsonH({ ok: true, sent: 1 });
    }

    /* ---- 设备信息 ---- */
    if (req.method === "GET" && path === "/api/info") {
      if (code.length !== 8) return json({ error: "bad code" }, 400);
      if (!await rateLimit(env, "info:" + ip, 120, 3600))
        return json({ error: "too many requests" }, 429);
      const devRaw = await env.KV.get("dev:" + code);
      if (!devRaw) return json({ error: "not found" }, 404);
      return jsonH(safeParse(devRaw, {}));
    }

    return json({ error: "not found" }, 404);
  },
};
