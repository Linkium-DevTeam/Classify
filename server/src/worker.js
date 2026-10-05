/**
 * ClassCall Lite — 教师喊话/传文件 云端中继 (Cloudflare Worker)
 *
 * 多教师多设备共用同一套 Worker：
 *  - 教师账号：注册/登录，密码 PBKDF2 存储，会话为 HMAC 无状态令牌
 *  - 设备：WebUI 生成 6 位配对码 → 桌面端兑换 → 设备令牌（可吊销，版本号校验）
 *  - 消息：每设备一个 InboxDO（SQLite 收件箱，支持长轮询）
 *  - 文件：R2 存储，全局 GlobalStoreDO 登记元数据，定时任务按保留期清理
 *
 * API 概览（全部 JSON，鉴权用 Authorization: Bearer <token>）：
 *  公开    POST /api/register | /api/login | /api/device/register   GET /api/version
 *  教师    POST /api/devices/pair  GET /api/devices  DELETE /api/devices/:did
 *          POST /api/announce      POST /api/files   GET /api/files  DELETE /api/files/:fid
 *          GET  /api/me
 *  设备    GET  /api/device/info   POST /api/device/heartbeat
 *          GET  /api/device/messages?after=N&wait=S
 *          POST /api/device/ack    GET /api/device/files/:fid
 */

const VERSION = "1.0.0";
const enc = new TextEncoder();

/* ------------------------- 基础工具 ------------------------- */

const json = (data, status = 200, headers = {}) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", ...headers },
  });

const fail = (status, code, message) => json({ error: code, message }, status);

/** 封禁用户访问时抛出，统一在 fetch 顶层转为 403 */
class BanError extends Error {
  constructor(rec) {
    super((rec && rec.banReason ? `账号已被封禁：${rec.banReason}` : "账号已被封禁")
      + "（如有疑问请联系 support@linkium.top）");
    this.banned = true;
  }
}

function b64urlFromBytes(buf) {
  let s = "";
  const b = new Uint8Array(buf);
  for (let i = 0; i < b.length; i++) s += String.fromCharCode(b[i]);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}
function bytesFromB64url(str) {
  str = str.replace(/-/g, "+").replace(/_/g, "/");
  while (str.length % 4) str += "=";
  const bin = atob(str);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
const randomHex = (n) => b64urlFromBytes(crypto.getRandomValues(new Uint8Array(n))).slice(0, n * 2);
const nowSec = () => Math.floor(Date.now() / 1000);

async function hmac(secret) {
  return crypto.subtle.importKey("raw", enc.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
}

/* ------------------------- 令牌（无状态 HMAC） ------------------------- */

async function signToken(env, payload) {
  const body = b64urlFromBytes(enc.encode(JSON.stringify(payload)));
  const key = await hmac(env.SECRET_KEY);
  const sig = await crypto.subtle.sign("HMAC", key, enc.encode(body));
  return `${body}.${b64urlFromBytes(sig)}`;
}

async function verifyToken(env, token) {
  if (!env.SECRET_KEY || !token) return null;
  const dot = token.indexOf(".");
  if (dot <= 0) return null;
  const body = token.slice(0, dot), sig = token.slice(dot + 1);
  const key = await hmac(env.SECRET_KEY);
  const expect = await crypto.subtle.sign("HMAC", key, enc.encode(body));
  const got = bytesFromB64url(sig);
  if (got.length !== expect.byteLength) return null;
  if (!crypto.subtle.timingSafeEqual) {
    // 逐字节常数时间比较兜底
    const a = new Uint8Array(expect);
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a[i] ^ got[i];
    if (diff !== 0) return null;
  } else if (!crypto.subtle.timingSafeEqual(got, new Uint8Array(expect))) return null;
  try {
    const payload = JSON.parse(new TextDecoder().decode(bytesFromB64url(body)));
    if (!payload.exp || payload.exp < nowSec()) return null;
    return payload;
  } catch { return null; }
}

function bearer(req) {
  const h = req.headers.get("authorization") || "";
  return h.startsWith("Bearer ") ? h.slice(7).trim() : null;
}

/** 设备能力位解析：缺省（含 v2 老设备无字段）视为全开 */
function parseCaps(raw) {
  let c = {};
  try { c = raw ? JSON.parse(raw) || {} : {}; } catch { c = {}; }
  return { camera: c.camera !== false, power: c.power !== false, clipboard: c.clipboard !== false };
}

/* ------------------------- 管理员 / Turnstile / 遥测辅助 ------------------------- */

async function sha256hexAsync(s) {
  const b = await crypto.subtle.digest("SHA-256", enc.encode(s));
  return b64urlFromBytes(b).replace(/[+/=]/g, "").slice(0, 32);
}

function genResetCode() {
  // 12 位大写去易混字符（约 55 bit 熵），一次性使用
  const alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
  const bytes = crypto.getRandomValues(new Uint8Array(12));
  let s = "";
  for (const b of bytes) s += alphabet[b % alphabet.length];
  return s;
}

async function isOwnerUid(env, uid) {
  if (!uid) return false;
  // 开发者/运营权限：仅部署者本人的账号（KV owners 数组）。
  // 迁移：老部署的 admins（首个注册者自动成为管理员）一次性转为 owners；注册不再自动授予。
  if (!(await env.KV.get("owners"))) {
    const legacy = await env.KV.get("admins");
    if (legacy) await env.KV.put("owners", legacy);
  }
  const owners = JSON.parse(await env.KV.get("owners") || "[]");
  return owners.includes(uid);
}

async function verifyTurnstile(env, token, ip) {
  if (!env.TURNSTILE_SECRET_KEY) return true; // 未配置 = 未启用
  if (!token) return false;
  try {
    const resp = await fetch("https://challenges.cloudflare.com/turnstile/v0/siteverify", {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ secret: env.TURNSTILE_SECRET_KEY, response: token, remoteip: ip || "" }),
    });
    const out = await resp.json();
    return !!out.success;
  } catch { return false; }
}

/* ------------------------- 密码（PBKDF2） ------------------------- */

async function hashPassword(password, salt) {
  const key = await crypto.subtle.importKey("raw", enc.encode(password), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits(
    // Workers WebCrypto 的 PBKDF2 迭代上限为 100000
    { name: "PBKDF2", hash: "SHA-256", salt: bytesFromB64url(salt), iterations: 100000 }, key, 256);
  return b64urlFromBytes(bits);
}

/* ------------------------- 限流（KV 计数，尽力而为） ------------------------- */

async function rateLimit(env, bucket, limit, windowSec) {
  try {
    const win = Math.floor(Date.now() / 1000 / windowSec);
    const key = `rl:${bucket}:${win}`;
    const n = parseInt((await env.KV.get(key)) || "0", 10) + 1;
    await env.KV.put(key, String(n), { expirationTtl: windowSec });
    return n <= limit;
  } catch { return true; }
}

/* ------------------------- DO 访问 ------------------------- */

async function storeCall(env, path, data) {
  const stub = env.STORE.get(env.STORE.idFromName("global"));
  const resp = await stub.fetch("https://do" + path, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(data || {}),
  });
  const body = await resp.json().catch(() => ({}));
  return { ok: resp.ok, status: resp.status, body };
}

async function inboxCall(env, key, path, data) {
  const stub = env.INBOX.get(env.INBOX.idFromName(key));
  const resp = await stub.fetch("https://do" + path, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(data || {}),
  });
  return resp.json();
}

/* ------------------------- 鉴权门卫 ------------------------- */

async function requireUser(env, req) {
  const p = await verifyToken(env, bearer(req));
  if (!p || p.t !== "u") return null;
  // 密码重置会递增 pv：旧令牌即刻失效（pv 不匹配 = 已被重置/更换凭据）
  const raw = await env.KV.get(`user:${p.un || ""}`);
  if (!raw) return null;
  const rec = JSON.parse(raw);
  if ((rec.pv || 0) !== (p.pv || 0)) return null;
  // 封禁用户：所有需要教师身份的接口一律拒绝（封禁同时递增 pv 使既有令牌失效）
  if (rec.banned) throw new BanError(rec);
  return p;
}

async function requireDevice(env, req) {
  const p = await verifyToken(env, bearer(req));
  if (!p || p.t !== "d") return null;
  // 令牌只证明"这是哪台设备"；教师与设备的多对多关联在 device_links 中
  const dev = await storeCall(env, "/device/get", { did: p.did });
  const row = dev.body && dev.body.device;
  if (!row || row.v !== p.v) return null; // 已吊销
  return p;
}

/** 注销账号：解除设备关联（末位关联连带删设备并清收件箱/课表）、清 R2 文件与登记、
 *  清账号级数据（看板/规则/操作集/通知/剪贴板）、管理员数组摘除，最后删除用户记录（令牌即刻失效）。 */
async function deleteUserAccount(env, username) {
  const raw = await env.KV.get(`user:${username}`);
  if (!raw) return { ok: false, status: 404, code: "not_found", message: "用户不存在" };
  const rec = JSON.parse(raw);
  const uid = rec.uid;
  const removedDids = [];
  try {
    const list = await storeCall(env, "/devices/list-dids", { uid });
    for (const did of list.body.dids || []) {
      await storeCall(env, "/devices/link-remove", { uid, did });
      const dev = await storeCall(env, "/device/get", { did });
      if (!dev.body || !dev.body.device) {
        await inboxCall(env, did, "/clear", {}).catch(() => {});
        await storeCall(env, "/timetable/clear", { did }).catch(() => {});
        removedDids.push(did);
      }
    }
    const files = await storeCall(env, "/files/list", { uid });
    for (const f of files.body.files || []) {
      try { await env.FILES.delete(`files/${uid}/${f.fid}`); } catch {}
    }
  } catch {}
  await storeCall(env, "/files/clear", { uid }).catch(() => {});
  await storeCall(env, "/clipboard/clear", { uid }).catch(() => {});
  await storeCall(env, "/readboard/clear-user", { uid }).catch(() => {});
  await storeCall(env, "/actions/clear", { uid }).catch(() => {});
  await storeCall(env, "/notices/clear", { uid }).catch(() => {});
  try { await env.KV.delete(`rbconfig:${uid}`); } catch {}
  try {
    const admins = JSON.parse(await env.KV.get("admins") || "[]");
    if (Array.isArray(admins) && admins.includes(uid)) {
      admins.splice(admins.indexOf(uid), 1);
      await env.KV.put("admins", JSON.stringify(admins));
    }
  } catch {}
  await env.KV.delete(`user:${username}`);
  return { ok: true, uid, removedDids };
}

/* ------------------------- 主入口 ------------------------- */

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

async function serveAsset(env, req, assetPath) {
  const resp = await env.ASSETS.fetch(new Request(new URL(req.url).origin + assetPath, req));
  const headers = new Headers(resp.headers);
  headers.set("x-content-type-options", "nosniff");
  headers.set("referrer-policy", "no-referrer");
  headers.set("cache-control", "no-cache");
  headers.set("content-security-policy",
    "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
    "connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'");
  return new Response(resp.body, { status: resp.status, headers });
}

async function handle(req, env) {
  const url = new URL(req.url);
  const p0 = url.pathname;

  /* ---- 门户路由：/ 官网+下载 · /app 教师端 WebUI · /lite 极简控制页 · /download 安装包 ---- */
  /* serveAsset 一律用"美化路径"（绝不 .html），避免 assets 层 pretty-URL 307 规范化 */
  if (p0 === "/") return serveAsset(env, req, "/welcome");
  if (p0 === "/app" || p0 === "/app/") return serveAsset(env, req, "/");
  if (p0 === "/lite" || p0 === "/lite/") return serveAsset(env, req, "/lite/");
  if (p0.startsWith("/lite/api/")) {
    // 反向代理到 clite worker（固定 https + 固定 host，无用户可控成分），页面侧保持同源免 CORS
    const target = "https://clite.linkium.top/api/" + p0.slice("/lite/api/".length) + url.search;
    return fetch(new Request(target, req));
  }
  if (p0.startsWith("/download/")) {
        const map = { }; // 自发行安装包直链：按需在此登记 R2 对象
    const hit = map[p0];
    if (hit) {
      const obj = await env.FILES.get(hit[0]);
      if (!obj) return fail(404, "not_found", "安装包暂未就绪，请稍后再试");
      return new Response(obj.body, { headers: {
        "content-type": "application/octet-stream",
        "content-disposition": `attachment; filename="${hit[1]}"`,
        "content-length": String(obj.size),
        "cache-control": "public, max-age=3600",
      } });
    }
  }

  if (!url.pathname.startsWith("/api/")) {
    return serveAsset(env, req, url.pathname);
  }

  if (req.method === "OPTIONS") return new Response(null, { status: 204 });
  if (!env.SECRET_KEY) return fail(503, "no_secret", "服务端未配置 SECRET_KEY，请先执行: wrangler secret put SECRET_KEY");

  return route(req, env, url);
}

async function route(req, env, url) {
  const path = url.pathname, method = req.method;
  const ip = req.headers.get("cf-connecting-ip") || "unknown";

  /* ---------- 公开接口 ---------- */

  if (method === "GET" && path === "/api/version") {
    return json({ name: env.APP_NAME || "ClassCall Lite", version: VERSION, minVersion: env.MIN_APP_VERSION || "", serverTime: Date.now() });
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
    const { kind, version, events, detail } = await req.json().catch(() => ({}));
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
    const key = req.headers.get("x-admin-key");
    if (!env.SECRET_KEY || !key || (key !== env.SECRET_KEY && key !== env.ADMIN_KEY))
      return fail(403, "forbidden", "需要管理员密钥");
    const body = await req.json().catch(() => ({}));
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
    const key = req.headers.get("x-admin-key");
    if (!env.SECRET_KEY || !key || (key !== env.SECRET_KEY && key !== env.ADMIN_KEY))
      return fail(403, "forbidden", "需要管理员密钥");
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

  if (method === "POST" && path === "/api/register") {
    // 注册开关（管理员可临时关闭，防滥用）
    if ((await env.KV.get("registration")) === "closed")
      return fail(403, "registration_closed", "注册暂未开放（如有疑问请联系 support@linkium.top）");
    if (!await rateLimit(env, `reg:${ip}`, 10, 3600)) return fail(429, "rate_limited", "注册过于频繁，请稍后再试");
    const { username, password, subject, cfToken } = await req.json().catch(() => ({}));
    if (!/^[a-zA-Z0-9_-]{3,24}$/.test(username || "")) return fail(400, "bad_username", "用户名需为 3-24 位字母、数字、_ 或 -");
    if (typeof password !== "string" || password.length < 8 || password.length > 64)
      return fail(400, "bad_password", "密码长度需为 8-64 位");
    if (await env.KV.get(`user:${username}`)) return fail(409, "exists", "用户名已被占用");
    // Turnstile（配置了密钥才启用）
    if (env.TURNSTILE_SECRET_KEY) {
      const ok = await verifyTurnstile(env, cfToken, ip);
      if (!ok) return fail(400, "turnstile", "人机验证未通过，请重试");
    }
    const uid = randomHex(8);
    const salt = b64urlFromBytes(crypto.getRandomValues(new Uint8Array(16)));
    const subj = ["语文","数学","英语","物理","化学","生物","政治","历史","地理","其他"].includes(subject) ? subject : "其他";
    // 一次性自助重置码：仅注册响应中展示一次，用于忘记密码后自助重置
    const resetCode = genResetCode();
    const record = { uid, username, salt, hash: await hashPassword(password, salt), created: Date.now(), subject: subj,
      resetCodeHash: await sha256hexAsync(resetCode), pv: 0 };
    await env.KV.put(`user:${username}`, JSON.stringify(record));
    // 首个注册用户不再自动成为管理员（运营权限仅属于部署者在 owners 名单中登记的账号）
    const token = await signToken(env, { t: "u", uid, un: username, exp: nowSec() + 30 * 86400, pv: 0 });
    return json({ token, uid, username, subject: subj, resetCode });
  }

  if (method === "POST" && path === "/api/login") {
    const { username, password, cfToken } = await req.json().catch(() => ({}));
    if (!await rateLimit(env, `login:${ip}:${username || ""}`, 20, 900))
      return fail(429, "rate_limited", "尝试过于频繁，请稍后再试");
    if (env.TURNSTILE_SECRET_KEY) {
      const ok = await verifyTurnstile(env, cfToken, ip);
      if (!ok) return fail(400, "turnstile", "人机验证未通过，请重试");
    }
    const raw = await env.KV.get(`user:${username || ""}`);
    if (!raw) return fail(401, "bad_credentials", "用户名或密码错误");
    const rec = JSON.parse(raw);
    const hash = await hashPassword(password || "", rec.salt);
    if (hash !== rec.hash) return fail(401, "bad_credentials", "用户名或密码错误");
    if (rec.banned)
      return fail(403, "banned",
        (rec.banReason ? `账号已被封禁：${rec.banReason}` : "账号已被封禁")
        + "（如有疑问请联系 support@linkium.top）");
    const token = await signToken(env, { t: "u", uid: rec.uid, un: rec.username, exp: nowSec() + 30 * 86400, pv: rec.pv || 0 });
    return json({ token, uid: rec.uid, username: rec.username });
  }

  // 自助密码重置：用户名 + 一次性重置码 + 新密码（重置后旧令牌即刻失效）
  if (method === "POST" && path === "/api/password/reset") {
    if (!await rateLimit(env, `pwreset:${ip}`, 6, 3600)) return fail(429, "rate_limited", "尝试过于频繁，请稍后再试");
    const { username, code, newPassword } = await req.json().catch(() => ({}));
    if (!/^[a-zA-Z0-9_-]{3,24}$/.test(username || "")) return fail(400, "bad_username", "用户名无效");
    if (typeof newPassword !== "string" || newPassword.length < 8 || newPassword.length > 64)
      return fail(400, "bad_password", "密码长度需为 8-64 位");
    const raw = await env.KV.get(`user:${username || ""}`);
    if (!raw) return fail(400, "bad_code", "重置码无效");
    const rec = JSON.parse(raw);
    if (rec.resetCodeExpires && Date.now() > rec.resetCodeExpires) return fail(400, "bad_code", "重置码已过期，请重新生成");
    const codeNorm = String(code || "").trim().toUpperCase();
    if (!rec.resetCodeHash || !codeNorm || (await sha256hexAsync(codeNorm)) !== rec.resetCodeHash)
      return fail(400, "bad_code", "重置码无效");
    rec.hash = await hashPassword(newPassword, rec.salt);
    rec.pv = (rec.pv || 0) + 1;
    rec.resetCodeHash = "";
    await env.KV.put(`user:${username}`, JSON.stringify(rec));
    const token = await signToken(env, { t: "u", uid: rec.uid, un: rec.username, exp: nowSec() + 30 * 86400, pv: rec.pv });
    return json({ ok: true, token, uid: rec.uid, username: rec.username });
  }

  // 桌面端配对：无需登录，凭 6 位配对码
  if (method === "POST" && path === "/api/device/register") {
    const { code, name, did } = await req.json().catch(() => ({}));
    if (!/^\d{6}$/.test(code || "")) return fail(400, "bad_code", "配对码为 6 位数字");
    const redeemed = await storeCall(env, "/pair/redeem", { code });
    if (!redeemed.ok) return fail(400, "bad_code", "配对码无效或已过期，请在手机端重新生成");
    const uid = redeemed.body.uid;
    // 封禁用户不得绑定新设备（KV 用户表按用户名为键，绑定是低频操作，扫描开销可接受）
    {
      let bannedOwner = false;
      let cur;
      do {
        const page = await env.KV.list({ prefix: "user:", cursor: cur });
        for (const k of page.keys) {
          const rawU = await env.KV.get(k.name);
          if (!rawU) continue;
          const u = JSON.parse(rawU);
          if (u.uid === uid) { bannedOwner = !!u.banned; break; }
        }
        cur = page.list_complete ? undefined : page.cursor;
      } while (!bannedOwner && cur);
      if (bannedOwner) return fail(403, "banned", "账号已被封禁，无法绑定设备（如有疑问请联系 support@linkium.top）");
    }
    // 客户端可携带硬件指纹 did（16 位，重装不变），实现设备身份稳定
    const hwid = /^[A-Za-z0-9_-]{8,32}$/.test(did || "") ? did : "";
    const added = await storeCall(env, "/devices/add", { uid, name: String(name || "未命名设备").slice(0, 32), did: hwid });
    const { device } = added.body;
    const token = await signToken(env, { t: "d", did: device.did, v: device.v, exp: nowSec() + 3650 * 86400 });
    await inboxCall(env, device.did, "/clear", {});
    return json({ token, deviceId: device.did, deviceName: device.name, serverTime: Date.now() });
  }

  // 扫码绑定：教室设备发起一次性绑定请求（公开，10 分钟有效）
  if (method === "POST" && path === "/api/device/pair-request") {
    if (!await rateLimit(env, `pairreq:${ip}`, 30, 3600)) return fail(429, "rate_limited", "请求过于频繁");
    const { name, did } = await req.json().catch(() => ({}));
    const r = await storeCall(env, "/pair-req/create",
      { name: String(name || "教室设备").slice(0, 32), did: String(did || "").slice(0, 32) });
    return json({ reqId: r.body.reqId, expires: r.body.expires });
  }
  if (method === "GET" && path === "/api/device/pair-wait") {
    const reqId = url.searchParams.get("req") || "";
    const wait = Math.min(parseFloat(url.searchParams.get("wait") || "0") || 0, 25);
    const r = await storeCall(env, "/pair-req/wait", { reqId, wait });
    if (r.status === 410) return fail(410, "expired", "绑定请求已过期，请重新生成二维码");
    return json(r.body);
  }

  // 反馈通道：教师（WebUI）或设备均可提交；内容 ≤500 字，设备可附 ≤8KB 脱敏日志。
  // 存储 KV fb:{yyyymmdd}（每日一卷，≤50 条，30 天定时清理——免费额度纪律：不逐条建键）。
  if (method === "POST" && path === "/api/feedback") {
    const u = await requireUser(env, req);
    let device = null;
    if (!u) device = await requireDevice(env, req);
    if (!u && !device) return fail(401, "unauthorized", "请先登录");
    if (!await rateLimit(env, `fb:${ip}`, 10, 3600)) return fail(429, "rate_limited", "反馈过于频繁，请稍后再试");
    const { message, category, logs } = await req.json().catch(() => ({}));
    const text = String(message || "").trim();
    if (!text || text.length > 500) return fail(400, "bad_request", "请填写反馈内容（≤500 字）");
    const day = new Date().toISOString().slice(0, 10).replace(/-/g, "");
    const key = `fb:${day}`;
    const list = JSON.parse(await env.KV.get(key) || "[]");
    if (list.length < 50) {
      list.push({
        at: Date.now(),
        by: u ? `uid:${u.uid}` : `did:${device.did}`,
        category: String(category || "").slice(0, 20),
        message: text,
        logs: typeof logs === "string" ? logs.slice(0, 8000) : "",
      });
      await env.KV.put(key, JSON.stringify(list), { expirationTtl: 31 * 86400 });
    }
    return json({ ok: true });
  }

  /* ---------- 教师接口 ---------- */

  const user = await requireUser(env, req);
  if (path.startsWith("/api/device/")) {
    // WS 通道自带 ?t= 鉴权（WS 客户端兼容），须在 requireDevice 之前处理
    if (method === "GET" && path === "/api/device/ws") {
      const p = await verifyToken(env, url.searchParams.get("t") || bearer(req));
      if (!p || p.t !== "d") return fail(401, "unauthorized", "设备令牌无效，请重新配对");
      const dev = await storeCall(env, "/device/get", { did: p.did });
      const row = dev.body && dev.body.device;
      if (!row || row.v !== p.v) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
      await storeCall(env, "/devices/touch", { did: p.did, version: String(url.searchParams.get("v") || "").slice(0, 16) });
      const stub = env.INBOX.get(env.INBOX.idFromName(p.did));
      return stub.fetch(new Request(
        `https://do/ws?after=${encodeURIComponent(url.searchParams.get("after") || "0")}`, req));
    }
    // 候课操作集拉取：支持 ?t= 令牌，须在 requireDevice 之前处理
    if (method === "GET" && path === "/api/device/action-set") {
      const p = await verifyToken(env, url.searchParams.get("t") || bearer(req));
      if (!p || p.t !== "d") return fail(401, "unauthorized", "设备令牌无效，请重新配对");
      const uid = url.searchParams.get("uid") || "";
      if (!/^[A-Za-z0-9_-]{4,24}$/.test(uid)) return fail(400, "bad_request", "uid 无效");
      const r = await storeCall(env, "/actions/get", { uid });
      return json({ actions: r.body.actions || [], uid });
    }
    // 学科教师解析：支持 ?t= 令牌，须在 requireDevice 之前处理
    if (method === "GET" && path === "/api/device/subject-teacher") {
      const p = await verifyToken(env, url.searchParams.get("t") || bearer(req));
      if (!p || p.t !== "d") return fail(401, "unauthorized", "设备令牌无效，请重新配对");
      const subject = url.searchParams.get("subject") || "";
      let cursor;
      do {
        const page = await env.KV.list({ prefix: "user:", cursor });
        for (const k of page.keys) {
          const raw = await env.KV.get(k.name);
          if (!raw) continue;
          const rec = JSON.parse(raw);
          if (rec.subject === subject)
            return json({ uid: rec.uid, username: rec.username, subject, avatar: rec.avatar || "", greeting: rec.greeting || "" });
        }
        cursor = page.list_complete ? undefined : page.cursor;
      } while (cursor);
      return fail(404, "not_found", "没有该学科的教师");
    }
    // 查询指定教师的公开信息（看板问候行用；设备令牌鉴权，仅返回昵称相关字段）
  if (method === "GET" && path === "/api/device/teacher-info") {
    const p = await verifyToken(env, url.searchParams.get("t") || bearer(req));
    if (!p || p.t !== "d") return fail(401, "unauthorized", "设备令牌无效，请重新配对");
    const name = String(url.searchParams.get("name") || "").slice(0, 24);
    if (!/^[a-zA-Z0-9_-]{3,24}$/.test(name)) return fail(400, "bad_request", "用户名无效");
    const rawU = await env.KV.get(`user:${name}`);
    if (!rawU) return fail(404, "not_found", "教师不存在");
    const u = JSON.parse(rawU);
    return json({ username: u.username, avatar: u.avatar || "", greeting: u.greeting || "" });
  }

  const device = await requireDevice(env, req);
    if (!device) return fail(401, "unauthorized", "设备令牌无效，请重新配对");

    if (method === "GET" && path === "/api/device/info") {
      const me = await storeCall(env, "/device/get", { did: device.did });
      return json({ device: me.body.device, serverTime: Date.now() });
    }
    if (method === "POST" && path === "/api/device/heartbeat") {
      const hb = await req.json().catch(() => ({}));
      await storeCall(env, "/devices/touch", {
        did: device.did,
        version: String(hb.version || "").slice(0, 16),
        // 能力位（摄像头/电源/剪贴板开关），WebUI 据此渲染锁态；缺省视为全开（兼容 v2）
        caps: hb.caps && typeof hb.caps === "object" ? JSON.stringify(hb.caps).slice(0, 200) : "",
      });
      return json({ ok: true, serverTime: Date.now() });
    }
    if (method === "GET" && path === "/api/device/messages") {
      const after = parseInt(url.searchParams.get("after") || "0", 10) || 0;
      const wait = Math.min(parseFloat(url.searchParams.get("wait") || "0") || 0, 20);
      const out = await inboxCall(env, device.did, "/poll", { after, wait });
      return json(out);
    }
    if (method === "POST" && path === "/api/device/upload") {
      const maxMB = 20;
      let form;
      try { form = await req.formData(); }
      catch { return fail(400, "bad_request", "上传解析失败"); }
      const file = form.get("file");
      if (!file || typeof file === "string") return fail(400, "no_file", "缺少文件");
      if (file.size > maxMB * 1024 * 1024) return fail(413, "too_large", `超过 ${maxMB}MB 上限`);
      const me = await storeCall(env, "/device/get", { did: device.did });
      const dev = me.body && me.body.device;
      if (!dev) return fail(401, "unauthorized", "设备不存在");
      // 真实文件名由 name 字段携带（workerd 会把非 ASCII filename 换成 ?）
      const customName = form.get("name");
      const upName = (typeof customName === "string" && customName.trim() ? customName : file.name || "device-upload.bin").slice(0, 80);
      // 多教师共享：回传发布到该设备的全部绑定教师（每人在自己的文件列表可见），R2 每人一份
      const links = await storeCall(env, "/devices/links-for", { did: device.did });
      const uids = (links.body.uids && links.body.uids.length) ? links.body.uids : [dev.uid];
      if (!uids.includes(dev.uid)) uids.push(dev.uid);
      const bytes = new Uint8Array(await file.arrayBuffer());
      const contentType = file.type || "application/octet-stream";
      let firstFid = "";
      let copies = 0;
      for (const uid of uids) {
        const fid = crypto.randomUUID();
        try {
          await env.FILES.put(`files/${uid}/${fid}`, new Blob([bytes], { type: contentType }), {
            httpMetadata: { contentType },
          });
          await storeCall(env, "/files/add", {
            uid, fid, name: upName, size: file.size, targets: "device",
          });
          if (!firstFid) firstFid = fid;
          copies++;
        } catch {}
      }
      if (!copies) return fail(500, "internal", "回传存储失败");
      return json({ fid: firstFid, name: upName, size: file.size, copies });
    }
    if (method === "POST" && path === "/api/device/clipboard") {
      const { text } = await req.json().catch(() => ({}));
      if (typeof text !== "string" || !text.trim()) return fail(400, "bad_request", "剪贴板内容为空");
      if (text.length > 51200) return fail(413, "too_large", "剪贴板内容过大（>50K 字符）");
      const me = await storeCall(env, "/device/get", { did: device.did });
      const dev = me.body && me.body.device;
      if (!dev) return fail(401, "unauthorized", "设备不存在");
      await storeCall(env, "/clipboard/set", { uid: dev.uid, text, did: device.did, name: dev.name });
      return json({ ok: true });
    }
    if (method === "GET" && path === "/api/device/timetable") {
      const r = await storeCall(env, "/timetable/get", { did: device.did });
      return json({ timetable: r.body.timetable, updatedBy: r.body.updatedBy || "" });
    }
    if (method === "GET" && path === "/api/device/notices") {
      const me = await storeCall(env, "/device/get", { did: device.did });
      const dev = me.body && me.body.device;
      if (!dev) return fail(401, "unauthorized", "设备不存在");
      // 多教师共享：聚合全部绑定教师的常驻通知（否则只显示设备创建者的，其他教师的通知"不显示"）
      const links = await storeCall(env, "/devices/links-for", { did: device.did });
      const uids = (links.body.uids && links.body.uids.length) ? links.body.uids : [dev.uid];
      const merged = [];
      for (const uid of uids) {
        try {
          const r = await storeCall(env, "/notices/get", { uid });
          for (const n of r.body.notices || []) merged.push(n);
        } catch {}
      }
      merged.sort((a, b) => (b.created || 0) - (a.created || 0));
      return json({ notices: merged.slice(0, 3) });
    }
    if (method === "POST" && path === "/api/device/readboard-state") {
      const { snapshot } = await req.json().catch(() => ({}));
      // 多教师共享：快照发布到该设备的全部绑定教师（每人的 WebUI 都能看到同一块板）
      const links = await storeCall(env, "/devices/links-for", { did: device.did });
      const uids = links.body.uids || [];
      if (!uids.length) return fail(401, "unauthorized", "设备不存在");
      await Promise.all(uids.map(uid =>
        storeCall(env, "/readboard/set", { uid, snapshot: String(snapshot || "{}").slice(0, 64000) })));
      return json({ ok: true, publishedTo: uids.length });
    }
    if (method === "GET" && path === "/api/device/readboard-config") {
      // 多教师共享：读取全部绑定教师的看板规则，取最近保存的一份
      const links = await storeCall(env, "/devices/links-for", { did: device.did });
      const uids = links.body.uids || [];
      if (!uids.length) return fail(401, "unauthorized", "设备不存在");
      let best = null;
      for (const uid of uids) {
        const raw = await env.KV.get(`rbconfig:${uid}`);
        if (!raw) continue;
        const cfg = JSON.parse(raw);
        if (!best || (cfg.updatedAt || 0) > (best.updatedAt || 0)) best = cfg;
      }
      return json({ config: best || { mode: "slash", ratio: 3 } });
    }
    // 卸载上报：解除所有教师关联并删除设备（用户要求卸载即解绑）
    if (method === "POST" && path === "/api/device/unregister") {
      const me = await storeCall(env, "/device/get", { did: device.did });
      const dev = me.body && me.body.device;
      if (!dev) return json({ ok: true });
      await storeCall(env, "/devices/remove-all", { did: device.did });
      await inboxCall(env, device.did, "/clear", {});
      return json({ ok: true });
    }
    // 为绑定教师生成本机自助重置码（持有设备 = 身份证明；30 分钟有效，一次性）
    if (method === "GET" && path === "/api/device/reset-codes") {
      const me = await storeCall(env, "/device/get", { did: device.did });
      const dev = me.body && me.body.device;
      if (!dev) return fail(401, "unauthorized", "设备不存在");
      // uid → username（KV user 记录以用户名为键，按 uid 反查）
      let target = null;
      let cursor;
      do {
        const page = await env.KV.list({ prefix: "user:", cursor });
        for (const k of page.keys) {
          const rawU = await env.KV.get(k.name);
          if (!rawU) continue;
          const u = JSON.parse(rawU);
          if (u.uid === dev.uid) { target = u; break; }
        }
        cursor = page.list_complete ? undefined : page.cursor;
      } while (target === null && cursor);
      if (!target) return fail(404, "not_found", "绑定教师账号不存在");
      const code = genResetCode();
      target.resetCodeHash = await sha256hexAsync(code);
      target.resetCodeExpires = Date.now() + 30 * 60 * 1000;
      await env.KV.put(`user:${target.username}`, JSON.stringify(target));
      return json({ username: target.username, code, expiresInMin: 30 });
    }
    if (method === "POST" && path === "/api/device/receipt") {
      const { mid, status } = await req.json().catch(() => ({}));
      if (!mid || !["received", "displayed", "failed"].includes(status))
        return fail(400, "bad_request", "回执参数缺失");
      await storeCall(env, "/receipts/set", { mid, did: device.did, status });
      return json({ ok: true });
    }
    if (method === "POST" && path === "/api/device/ack") {
      const { after } = await req.json().catch(() => ({}));
      await inboxCall(env, device.did, "/ack", { after: parseInt(after || "0", 10) || 0 });
      return json({ ok: true });
    }
    if (method === "GET" && path.startsWith("/api/device/files/")) {
      const fid = path.split("/").pop();
      const meta = await storeCall(env, "/files/get", { fid });
      if (!meta.ok || !meta.body.file) return fail(404, "not_found", "文件不存在或已过期");
      const f = meta.body.file;
      // 设备必须与上传文件的教师存在关联（多教师共享设备）
      const link = await storeCall(env, "/devices/link-check", { uid: f.uid, did: device.did });
      if (!link.body.linked) return fail(403, "forbidden", "无权访问该文件");
      if (f.targets !== "all" && Array.isArray(f.targets) && !f.targets.includes(device.did))
        return fail(403, "forbidden", "该文件未发送到此设备");
      const obj = await env.FILES.get(`files/${f.uid}/${f.fid}`);
      if (!obj) return fail(410, "gone", "文件已被清理");
      const headers = {
        "content-type": obj.httpMetadata && obj.httpMetadata.contentType || "application/octet-stream",
        "content-length": String(obj.size),
        "x-content-type-options": "nosniff",
      };
      try { headers["content-disposition"] = `attachment; filename*=UTF-8''${encodeURIComponent(f.name)}`; } catch {}
      return new Response(obj.body, { headers });
    }
    return fail(404, "not_found", "接口不存在");
  }

  if (!user) return fail(401, "unauthorized", "请先登录");

  // 扫码绑定：教师端读取请求信息 + 确认绑定
  if (method === "GET" && path === "/api/bind/info") {
    const reqId = url.searchParams.get("req") || "";
    const r = await storeCall(env, "/pair-req/get", { reqId });
    const row = r.body.request;
    if (!row || Date.now() > row.expires || row.status !== "pending")
      return fail(410, "expired", "绑定请求不存在或已过期，请在电脑端重新生成二维码");
    return json({ name: row.name, created: row.created });
  }
  if (method === "POST" && path === "/api/bind") {
    const { reqId } = await req.json().catch(() => ({}));
    const info = await storeCall(env, "/pair-req/get", { reqId: reqId || "" });
    const row = info.body.request;
    if (!row || row.status !== "pending" || Date.now() > row.expires)
      return fail(410, "expired", "绑定请求不存在或已过期，请在电脑端重新生成二维码");

    if (row.did) {
      // 该设备声称已注册过：把当前教师关联上去（多教师共享同一台设备），设备令牌保持不变
      const devCheck = await storeCall(env, "/device/get", { did: row.did });
      if (devCheck.ok && devCheck.body.device) {
        const linked = await storeCall(env, "/devices/link-add", { uid: user.uid, did: row.did });
        if (!linked.ok) return fail(404, "no_device", "设备不存在或已被移除");
        const bound = await storeCall(env, "/pair-req/bind", { reqId, uid: user.uid, did: row.did, token: "" });
        if (!bound.ok) return fail(410, "expired", "绑定请求已被使用");
        return json({ ok: true, linked: true, device: { did: row.did, name: (devCheck.body.device || {}).name || row.name } });
      }
      // 设备记录已不存在（如曾被全部解绑）：自愈为全新绑定
    }

    const added = await storeCall(env, "/devices/add", { uid: user.uid, name: row.name });
    const { device } = added.body;
    const token = await signToken(env, { t: "d", did: device.did, v: device.v, exp: nowSec() + 3650 * 86400 });
    const bound = await storeCall(env, "/pair-req/bind", { reqId, uid: user.uid, did: device.did, token });
    if (!bound.ok) return fail(410, "expired", "绑定请求已被使用");
    return json({ ok: true, device: { did: device.did, name: device.name } });
  }

  if (method === "GET" && path === "/api/me") {
    const list = await storeCall(env, "/devices/list", { uid: user.uid });
    const devices = (list.body.devices || []).map(d => ({
      did: d.did, name: d.name, createdAt: d.createdAt, lastSeen: d.lastSeen,
      online: Date.now() - d.lastSeen < 90 * 1000,
      caps: parseCaps(d.caps),
    }));
    const raw = await env.KV.get(`user:${user.un}`);
    const urec = raw ? JSON.parse(raw) : {};
    const subject = urec.subject || "其他";
    const role = await isOwnerUid(env, user.uid) ? "admin" : "user";
    return json({
      uid: user.uid, username: user.un, subject, devices,
      avatar: urec.avatar || "", greeting: urec.greeting || "",
      role,
    });
  }
  // 任教学科注册时填写、此后不可更改（保留路由仅作校验回执，不再写入）
  if (method === "POST" && path === "/api/me/subject") {
    return fail(403, "immutable", "任教学科注册后不可更改");
  }

  // 注销账号（自助）：需密码确认防会话劫持误删。联动清理设备/文件/全部账号数据，不可恢复。
  if (method === "POST" && path === "/api/account/delete") {
    const { password } = await req.json().catch(() => ({}));
    const rawU = await env.KV.get(`user:${user.un}`);
    const recU = rawU ? JSON.parse(rawU) : null;
    if (!recU) return fail(404, "not_found", "账号不存在");
    const hash = await hashPassword(String(password || ""), recU.salt);
    if (hash !== recU.hash) return fail(401, "bad_credentials", "密码错误，账号未删除");
    const r = await deleteUserAccount(env, user.un);
    if (!r.ok) return fail(r.status, r.code, r.message);
    return json({ ok: true, removedDevices: (r.removedDids || []).length });
  }

  if (method === "POST" && path === "/api/devices/pair") {
    const r = await storeCall(env, "/pair/create", { uid: user.uid });
    return json({ code: r.body.code, expires: r.body.expires });
  }

  if (method === "GET" && path === "/api/devices") {
    const list = await storeCall(env, "/devices/list", { uid: user.uid });
    const devices = (list.body.devices || []).map(d => ({
      did: d.did, name: d.name, createdAt: d.createdAt, lastSeen: d.lastSeen,
      online: Date.now() - d.lastSeen < 90 * 1000,
      caps: parseCaps(d.caps),
    }));
    return json({ devices });
  }

  // 课堂表面控制通道：clock / camera 等设备动作（带 mid 回执）
  if (method === "POST" && path === "/api/surface/send") {
    const { type, payload, targets } = await req.json().catch(() => ({}));
    const valid = ["clock", "camera", "screenshot", "readboard", "preclass", "picker", "image"];
    if (!valid.includes(type)) return fail(400, "bad_type", "不支持的类型");
    // 指定学科候课的权限门：管理员可指定任意学科，普通教师只能候自己任教学科
    if (type === "preclass" && payload && typeof payload.subject === "string" && payload.subject) {
      const urecRaw = await env.KV.get(`user:${user.un}`);
      const mySubject = urecRaw ? (JSON.parse(urecRaw).subject || "其他") : "其他";
      if (!(await isOwnerUid(env, user.uid)) && payload.subject !== mySubject)
        return fail(403, "forbidden_subject", "只有管理员可以指定其他学科");
    }
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    let dids = list.body.dids || [];
    if (Array.isArray(targets) && targets.length) dids = dids.filter(d => targets.includes(d));
    if (!dids.length) return fail(400, "no_device", "没有可用的设备");
    const mid = randomHex(8);
    const msg = { type, mid, ...(payload || {}), from: user.un, created: Date.now() };
    await Promise.all(dids.map(did => inboxCall(env, did, "/push", { msg })));
    return json({ mid, sent: dids.length });
  }
  if (method === "POST" && path === "/api/notice") {
    const { text } = await req.json().catch(() => ({}));
    if (typeof text !== "string" || !text.trim()) return fail(400, "empty", "通知内容不能为空");
    await storeCall(env, "/notices/set", { uid: user.uid, name: user.un, text: text.slice(0, 500) });
    // 同时推送到设备刷新灵动岛
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    const msg = { type: "notice", from: user.un, created: Date.now() };
    await Promise.all((list.body.dids || []).map(did => inboxCall(env, did, "/push", { msg })));
    return json({ ok: true });
  }
  if (method === "DELETE" && path === "/api/notice") {
    await storeCall(env, "/notices/clear", { uid: user.uid });
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    const msg = { type: "notice", from: user.un, created: Date.now() };
    await Promise.all((list.body.dids || []).map(did => inboxCall(env, did, "/push", { msg })));
    return json({ ok: true });
  }
  const fileContent = path.match(/^\/api\/files\/([0-9a-fA-F-]+)\/content$/);
  if (method === "GET" && fileContent) {
    const fid = fileContent[1];
    const meta = await storeCall(env, "/files/get", { fid });
    if (!meta.ok || !meta.body.file || meta.body.file.uid !== user.uid)
      return fail(404, "not_found", "文件不存在");
    const f = meta.body.file;
    const obj = await env.FILES.get(`files/${user.uid}/${fid}`);
    if (!obj) return fail(410, "gone", "文件已被清理");
    const headers = {
      "content-type": obj.httpMetadata && obj.httpMetadata.contentType || "application/octet-stream",
      "content-length": String(obj.size),
      "x-content-type-options": "nosniff",
    };
    try { headers["content-disposition"] = `inline; filename*=UTF-8''${encodeURIComponent(f.name)}`; } catch {}
    return new Response(obj.body, { headers });
  }
  if (method === "GET" && path === "/api/readboard/state") {
    const r = await storeCall(env, "/readboard/get", { uid: user.uid });
    return json({ state: r.body.state });
  }
  // 早读看板账号级配置：加分模式（slash=斜杠满比例派花 / flower=直接加花）与换算比例
  if (method === "GET" && path === "/api/readboard/config") {
    const raw = await env.KV.get(`rbconfig:${user.uid}`);
    return json({ config: raw ? JSON.parse(raw) : { mode: "slash", ratio: 3 } });
  }
  if (method === "POST" && path === "/api/readboard/config") {
    const { mode, ratio } = await req.json().catch(() => ({}));
    if (!["slash", "flower"].includes(mode)) return fail(400, "bad_mode", "加分模式无效");
    const r = Math.max(1, Math.min(10, parseInt(ratio, 10) || 3));
    await env.KV.put(`rbconfig:${user.uid}`, JSON.stringify({ mode, ratio: r, updatedAt: Date.now() }));
    // 通知在线设备即时刷新配置
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    const msg = { type: "readboard-config", from: user.un, created: Date.now() };
    await Promise.all((list.body.dids || []).map(did => inboxCall(env, did, "/push", { msg })));
    return json({ ok: true, config: { mode, ratio: r } });
  }
  // 教师头像（账号级 1 张，dataURL ≤48KB，客户端缩放后上传）与问候语
  if (method === "POST" && path === "/api/me/avatar") {
    const { avatar } = await req.json().catch(() => ({}));
    if (typeof avatar !== "string" || avatar.length > 64000)
      return fail(400, "bad_avatar", "头像无效或过大（请使用小图）");
    if (avatar && !/^data:image\/(png|jpeg|webp);base64,/.test(avatar))
      return fail(400, "bad_avatar", "仅支持 png/jpeg/webp");
    const raw = await env.KV.get(`user:${user.un}`);
    if (!raw) return fail(404, "not_found", "账号不存在");
    const rec = JSON.parse(raw);
    rec.avatar = avatar || "";
    await env.KV.put(`user:${user.un}`, JSON.stringify(rec));
    return json({ ok: true });
  }
  if (method === "POST" && path === "/api/me/greeting") {
    const { greeting } = await req.json().catch(() => ({}));
    const raw = await env.KV.get(`user:${user.un}`);
    if (!raw) return fail(404, "not_found", "账号不存在");
    const rec = JSON.parse(raw);
    rec.greeting = String(greeting || "").slice(0, 60);
    await env.KV.put(`user:${user.un}`, JSON.stringify(rec));
    return json({ ok: true, greeting: rec.greeting });
  }
  if (method === "GET" && path === "/api/clipboard/latest") {
    const r = await storeCall(env, "/clipboard/get", { uid: user.uid });
    return json({ clipboard: r.body.clipboard });
  }
  if (method === "POST" && path === "/api/clipboard/send") {
    const { text, targets } = await req.json().catch(() => ({}));
    if (typeof text !== "string" || !text.trim()) return fail(400, "empty", "内容不能为空");
    if (text.length > 51200) return fail(413, "too_large", "内容过大（>50K 字符）");
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    let dids = list.body.dids || [];
    if (Array.isArray(targets) && targets.length) dids = dids.filter(d => targets.includes(d));
    if (!dids.length) return fail(400, "no_device", "没有可用的设备，请先绑定");
    const mid = randomHex(8);
    const msg = { type: "clipboard", mid, text, from: user.un, created: Date.now() };
    await Promise.all(dids.map(did => inboxCall(env, did, "/push", { msg })));
    return json({ mid, sent: dids.length });
  }
  // 课程表：教师保存（应用到本人全部设备）/读取
  if (method === "POST" && path === "/api/timetable") {
    const body = await req.json().catch(() => ({}));
    const tt = {
      preclassMinutes: Math.max(1, Math.min(30, parseInt(body.preclassMinutes || 5, 10) || 5)),
      // 每节课支持起止时间（end 缺省时设备按下一节开始/45 分钟推导）
      periods: Array.isArray(body.periods)
        ? body.periods.slice(0, 12).map(p => ({
            p: parseInt(p && p.p, 10) || 0,
            start: String((p && p.start) || "08:00").slice(0, 5),
            end: String((p && p.end) || "").slice(0, 5),
          }))
        : [],
      lessons: Array.isArray(body.lessons) ? body.lessons.slice(0, 80) : [],
      overrides: body.overrides && typeof body.overrides === "object" ? body.overrides : {},
    };
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    const dids = list.body.dids || [];
    if (!dids.length) return fail(400, "no_device", "请先绑定设备");
    for (const did of dids)
      await storeCall(env, "/timetable/set", { did, json: JSON.stringify(tt), by: user.un });
    return json({ ok: true, devices: dids.length });
  }
  if (method === "GET" && path === "/api/timetable") {
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    const dids = list.body.dids || [];
    if (!dids.length) return json({ timetable: null, dids: [], updatedBy: "" });
    const r = await storeCall(env, "/timetable/get", { did: dids[0] });
    return json({ timetable: r.body.timetable, dids, updatedBy: r.body.updatedBy || "" });
  }

  // 候课操作集：教师保存/读取自己的
  if (method === "POST" && path === "/api/actions") {
    const { actions } = await req.json().catch(() => ({}));
    if (!Array.isArray(actions) || actions.length > 12) return fail(400, "bad_request", "操作集无效（≤12 项）");
    for (const a of actions)
      if (!a || !["clock", "tts", "open", "volume", "board", "delay"].includes(a.type))
        return fail(400, "bad_type", "不支持的动作类型");
    await storeCall(env, "/actions/set", { uid: user.uid, json: JSON.stringify(actions.slice(0, 12)) });
    return json({ ok: true, count: actions.length });
  }
  if (method === "GET" && path === "/api/actions") {
    const r = await storeCall(env, "/actions/get", { uid: user.uid });
    return json({ actions: r.body.actions || [] });
  }

  if (method === "GET" && path === "/api/announce/receipts") {
    const mid = url.searchParams.get("mid") || "";
    const r = await storeCall(env, "/receipts/get", { mid });
    return json({ receipts: r.body.receipts || [] });
  }

  // 管理员为任意用户生成一次性重置码（30 分钟有效）
  if (method === "POST" && path === "/api/admin/reset-code") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const { username } = await req.json().catch(() => ({}));
    const raw = await env.KV.get(`user:${String(username || "").slice(0, 24)}`);
    if (!raw) return fail(404, "not_found", "用户不存在");
    const target = JSON.parse(raw);
    const code = genResetCode();
    target.resetCodeHash = await sha256hexAsync(code);
    target.resetCodeExpires = Date.now() + 30 * 60 * 1000;
    await env.KV.put(`user:${target.username}`, JSON.stringify(target));
    return json({ username: target.username, code });
  }

  // 开发者面板（仅管理员角色用户）：运营总览 + 匿名遥测聚合
  if (method === "GET" && path === "/api/admin/panel") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可访问");
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
            users.push({ username: rec.username, uid: rec.uid, subject: rec.subject || "其他",
              banned: !!rec.banned, banReason: rec.banReason || "", created: rec.created });
          }
        }
        cursor = page.list_complete ? undefined : page.cursor;
      } while (cursor);
    } catch {}
    // 近 7 天遥测
    const telemetry = [];
    for (let i = 0; i < 7; i++) {
      const day = new Date(Date.now() - i * 86400000).toISOString().slice(0, 10);
      for (const kind of ["app", "web"]) {
        const raw = await env.KV.get(`tel:${day}:${kind}`);
        if (raw) telemetry.push({ day, kind, data: JSON.parse(raw) });
      }
    }
    return json({
      users,
      devices: (store.body.devices || []).map(d => ({
        did: d.did, ownerUid: d.uid, name: d.name, version: d.version || "",
        caps: parseCaps(d.caps),
        createdAt: d.createdAt, lastSeen: d.lastSeen,
      })),
      links: store.body.links || [],
      telemetry,
      registrationOpen: (await env.KV.get("registration")) !== "closed",
      serverTime: Date.now(),
    });
  }

  /* ---------- 管理员：公告 / 封禁 / 注册开关 / 反馈 / 看板缓存 ---------- */

  if (method === "POST" && path === "/api/admin/broadcast") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const { text, level, hours } = await req.json().catch(() => ({}));
    const t = String(text || "").trim();
    if (!t || t.length > 300) return fail(400, "bad_request", "公告内容需 1-300 字");
    const lv = ["info", "warn", "critical"].includes(level) ? level : "info";
    const h = Math.max(1, Math.min(720, parseInt(hours, 10) || 72));
    const b = { id: randomHex(8), text: t, level: lv, createdAt: Date.now(), expiresAt: Date.now() + h * 3600 * 1000 };
    await env.KV.put("broadcast", JSON.stringify(b));
    return json({ ok: true, broadcast: b });
  }
  if (method === "DELETE" && path === "/api/admin/broadcast") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    await env.KV.delete("broadcast");
    return json({ ok: true });
  }
  // 管理员删除用户（含封禁用户清理）：不能删除自己（自助走 /api/account/delete）
  if (method === "POST" && path === "/api/admin/delete-user") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const { username } = await req.json().catch(() => ({}));
    const name = String(username || "").slice(0, 24);
    if (!name) return fail(400, "bad_request", "缺少用户名");
    if (name === user.un) return fail(400, "bad_request", "不能通过管理员接口删除自己，请使用账号自助注销");
    const r = await deleteUserAccount(env, name);
    if (!r.ok) return fail(r.status, r.code, r.message);
    return json({ ok: true, username: name, removedDevices: (r.removedDids || []).length });
  }
  if (method === "POST" && path === "/api/admin/ban") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const { username, reason } = await req.json().catch(() => ({}));
    const raw = await env.KV.get(`user:${String(username || "").slice(0, 24)}`);
    if (!raw) return fail(404, "not_found", "用户不存在");
    const rec = JSON.parse(raw);
    rec.banned = true;
    rec.banReason = String(reason || "").slice(0, 120);
    rec.bannedAt = Date.now();
    rec.pv = (rec.pv || 0) + 1; // 递增 pv 使既有令牌即刻失效
    await env.KV.put(`user:${rec.username}`, JSON.stringify(rec));
    return json({ ok: true, username: rec.username });
  }
  if (method === "POST" && path === "/api/admin/unban") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const { username } = await req.json().catch(() => ({}));
    const raw = await env.KV.get(`user:${String(username || "").slice(0, 24)}`);
    if (!raw) return fail(404, "not_found", "用户不存在");
    const rec = JSON.parse(raw);
    rec.banned = false;
    rec.banReason = "";
    await env.KV.put(`user:${rec.username}`, JSON.stringify(rec));
    return json({ ok: true, username: rec.username });
  }
  if (method === "POST" && path === "/api/admin/registration") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const { open } = await req.json().catch(() => ({}));
    if (open) await env.KV.delete("registration");
    else await env.KV.put("registration", "closed");
    return json({ ok: true, open: !!open });
  }
  if (method === "GET" && path === "/api/admin/feedback") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const out = [];
    let cursor;
    try {
      do {
        const page = await env.KV.list({ prefix: "fb:", cursor });
        for (const k of page.keys) {
          const arr = JSON.parse(await env.KV.get(k.name) || "[]");
          for (const f of arr) out.push(f);
        }
        cursor = page.list_complete ? undefined : page.cursor;
      } while (cursor);
    } catch {}
    out.sort((a, b) => (b.at || 0) - (a.at || 0));
    return json({ feedback: out.slice(0, 100) });
  }

  // 崩溃上报列表（近 7 天，仅管理员）
  if (method === "GET" && path === "/api/admin/crashes") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const out = [];
    for (let i = 0; i < 7; i++) {
      const day = new Date(Date.now() - i * 86400000).toISOString().slice(0, 10).replace(/-/g, "");
      try {
        const arr = JSON.parse(await env.KV.get(`crash:${day}`) || "[]");
        for (const c of arr) out.push(c);
      } catch {}
    }
    out.sort((a, b) => (b.at || 0) - (a.at || 0));
    return json({ crashes: out.slice(0, 60) });
  }
  if (method === "POST" && path === "/api/admin/readboard-purge") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    await storeCall(env, "/readboard/clear", {});
    return json({ ok: true });
  }

  const deviceRemove = path.match(/^\/api\/devices\/([A-Za-z0-9_-]+)$/);
  if (method === "DELETE" && deviceRemove) {
    await storeCall(env, "/devices/remove", { uid: user.uid, did: deviceRemove[1] });
    return json({ ok: true });
  }

  // 喊话历史（最近 20 条，供 WebUI 一键重发；90 天过期）
  if (method === "GET" && path === "/api/announce/history") {
    let hist = [];
    try { hist = JSON.parse(await env.KV.get(`hist:${user.uid}`) || "[]"); } catch {}
    return json({ history: hist });
  }

  if (method === "POST" && path === "/api/announce") {
    const { text, speak, targets } = await req.json().catch(() => ({}));
    const t = String(text || "").trim();
    if (!t) return fail(400, "empty", "内容不能为空");
    if (t.length > 2000) return fail(400, "too_long", "内容过长（最多 2000 字）");
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    let dids = list.body.dids || [];
    if (Array.isArray(targets) && targets.length) dids = dids.filter(d => targets.includes(d));
    if (!dids.length) return fail(400, "no_device", "没有可用的设备，请先在「设备」页配对");
    const mid = randomHex(8);
    const msg = { type: "text", mid, text: t, speak: speak !== false, from: user.un, created: Date.now() };
    const results = await Promise.all(dids.map(did =>
      inboxCall(env, did, "/push", { msg })));
    // 记录历史（仅内容前 200 字，90 天过期）
    try {
      const hk = `hist:${user.uid}`;
      const hist = JSON.parse(await env.KV.get(hk) || "[]");
      hist.unshift({ text: t.slice(0, 200), speak: speak !== false, at: Date.now() });
      await env.KV.put(hk, JSON.stringify(hist.slice(0, 20)), { expirationTtl: 90 * 86400 });
    } catch {}
    return json({ mid, sent: results.length, devices: dids });
  }

  if (method === "POST" && path === "/api/files") {
    const maxMB = parseInt(env.MAX_FILE_MB || "80", 10);
    let form;
    try { form = await req.formData(); }
    catch { return fail(400, "bad_request", "上传内容解析失败"); }
    const file = form.get("file");
    if (!file || typeof file === "string") return fail(400, "no_file", "缺少文件");
    if (file.size > maxMB * 1024 * 1024) return fail(413, "too_large", `文件超过 ${maxMB}MB 上限`);
    let targets = form.get("targets");
    try { targets = JSON.parse(targets || '"all"'); } catch { targets = "all"; }
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    let dids = list.body.dids || [];
    if (Array.isArray(targets) && targets.length) dids = dids.filter(d => targets.includes(d));
    if (!dids.length) return fail(400, "no_device", "没有可用的设备，请先在「设备」页配对");
    const fid = crypto.randomUUID();
    // workerd 的 formData 会把非 ASCII filename 换成 ?，真实文件名由独立的 name 字段携带
    const customName = form.get("name");
    const name = (typeof customName === "string" && customName.trim() ? customName : file.name || "file.bin").slice(0, 80);
    // nopush=1：仅入库不推送设备消息（图片快投走 surface/image 单独投屏，避免文件消息再落一次桌面）
    const nopush = form.get("nopush") === "1";
    await env.FILES.put(`files/${user.uid}/${fid}`, file.stream(), {
      httpMetadata: { contentType: file.type || "application/octet-stream" },
    });
    await storeCall(env, "/files/add", { uid: user.uid, fid, name, size: file.size, targets });
    if (!nopush) {
      const msg = { type: "file", fid, name, size: file.size, from: user.un, created: Date.now() };
      await Promise.all(dids.map(did => inboxCall(env, did, "/push", { msg })));
    }
    return json({ fid, name, size: file.size, sent: nopush ? 0 : dids.length });
  }

  if (method === "GET" && path === "/api/files") {
    const r = await storeCall(env, "/files/list", { uid: user.uid });
    return json({ files: r.body.files || [] });
  }

  const fileDelete = path.match(/^\/api\/files\/([0-9a-fA-F-]+)$/);
  if (method === "DELETE" && fileDelete) {
    const fid = fileDelete[1];
    const meta = await storeCall(env, "/files/get", { fid });
    if (meta.ok && meta.body.file && meta.body.file.uid === user.uid) {
      await env.FILES.delete(`files/${user.uid}/${fid}`);
      await storeCall(env, "/files/remove", { uid: user.uid, fid });
    }
    return json({ ok: true });
  }

  // 置顶/取消置顶（置顶文件不参与 7 天自动清理）
  const filePin = path.match(/^\/api\/files\/([0-9a-fA-F-]+)\/pin$/);
  if (method === "POST" && filePin) {
    const { pinned } = await req.json().catch(() => ({}));
    await storeCall(env, "/files/pin", { uid: user.uid, fid: filePin[1], pinned: !!pinned });
    return json({ ok: true, pinned: !!pinned });
  }

  // 重新推送已上传文件到设备（课件复用；文件需仍在保留期内）
  const fileResend = path.match(/^\/api\/files\/([0-9a-fA-F-]+)\/resend$/);
  if (method === "POST" && fileResend) {
    const fid = fileResend[1];
    const meta = await storeCall(env, "/files/get", { fid });
    if (!meta.ok || !meta.body.file || meta.body.file.uid !== user.uid)
      return fail(404, "not_found", "文件不存在或已过期");
    const f = meta.body.file;
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    const dids = list.body.dids || [];
    if (!dids.length) return fail(400, "no_device", "没有可用的设备，请先绑定");
    const msg = { type: "file", fid, name: f.name, size: f.size, from: user.un, created: Date.now() };
    await Promise.all(dids.map(did => inboxCall(env, did, "/push", { msg })));
    return json({ ok: true, sent: dids.length, name: f.name });
  }

  if (method === "POST" && path === "/api/command") {
    const { name, value, targets } = await req.json().catch(() => ({}));
    const valid = ["set_volume", "mute", "lock", "screen_off", "restart", "shutdown"];
    if (!valid.includes(name)) return fail(400, "bad_command", "不支持的指令");
    const list = await storeCall(env, "/devices/list-dids", { uid: user.uid });
    let dids = list.body.dids || [];
    if (Array.isArray(targets) && targets.length) dids = dids.filter(d => targets.includes(d));
    if (!dids.length) return fail(400, "no_device", "没有可用的设备，请先在「设备」页配对");
    const msg = { type: "cmd", name, value: value ?? null, from: user.un, created: Date.now() };
    await Promise.all(dids.map(did => inboxCall(env, did, "/push", { msg })));
    return json({ sent: dids.length, devices: dids });
  }

  return fail(404, "not_found", "接口不存在");
}

/* ==================== Durable Objects ==================== */

/** 全局存储：配对码 / 设备表 / 文件登记（SQLite，强一致） */
export class GlobalStoreDO {
  constructor(state) {
    this.state = state;
    this.sql = state.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS devices (
      did TEXT PRIMARY KEY, uid TEXT NOT NULL, name TEXT NOT NULL,
      v INTEGER NOT NULL DEFAULT 1, createdAt INTEGER, lastSeen INTEGER)`);
    // 多教师共享同一台设备：uid ↔ did 多对多
    this.sql.exec(`CREATE TABLE IF NOT EXISTS device_links (
      uid TEXT NOT NULL, did TEXT NOT NULL, created INTEGER,
      PRIMARY KEY (uid, did))`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS pairing (
      code TEXT PRIMARY KEY, uid TEXT NOT NULL, expires INTEGER)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS files (
      fid TEXT PRIMARY KEY, uid TEXT NOT NULL, name TEXT, size INTEGER,
      targets TEXT, created INTEGER)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS pair_reqs (
      reqId TEXT PRIMARY KEY, name TEXT, status TEXT, uid TEXT, did TEXT,
      token TEXT, delivered INTEGER DEFAULT 0, created INTEGER, expires INTEGER)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS receipts (
      mid TEXT NOT NULL, did TEXT NOT NULL, status TEXT NOT NULL,
      created INTEGER, PRIMARY KEY (mid, did))`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS clipboards (
      uid TEXT PRIMARY KEY, text TEXT, fromDid TEXT, fromName TEXT, created INTEGER)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS notices (
      uid TEXT NOT NULL, name TEXT NOT NULL, text TEXT NOT NULL, created INTEGER)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS readboard_state (
      uid TEXT PRIMARY KEY, snapshot TEXT, created INTEGER)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS timetables (
      did TEXT PRIMARY KEY, json TEXT, updatedBy TEXT, created INTEGER)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS action_sets (
      uid TEXT PRIMARY KEY, json TEXT, created INTEGER)`);
    // 自愈迁移：把历史"设备归属某教师"的数据补进多对多关联表（幂等）
    this.sql.exec(`INSERT OR IGNORE INTO device_links (uid, did, created)
      SELECT uid, did, createdAt FROM devices WHERE uid <> ''`);
    // 旧版本 pair_reqs 表缺少 delivered 列
    try { this.sql.exec("ALTER TABLE pair_reqs ADD COLUMN delivered INTEGER DEFAULT 0"); }
    catch { /* 列已存在 */ }
    // v2：设备心跳上报版本号
    try { this.sql.exec("ALTER TABLE devices ADD COLUMN version TEXT DEFAULT ''"); }
    catch { /* 列已存在 */ }
    // v3.1：设备能力位（JSON：camera/power/clipboard 开关）
    try { this.sql.exec("ALTER TABLE devices ADD COLUMN caps TEXT DEFAULT ''"); }
    catch { /* 列已存在 */ }
    // v3.3：文件置顶（置顶不参与 7 天清理）
    try { this.sql.exec("ALTER TABLE files ADD COLUMN pinned INTEGER DEFAULT 0"); }
    catch { /* 列已存在 */ }
  }

  rows(q) { return q.toArray(); }

  async fetch(req) {
    const url = new URL(req.url);
    const { pathname } = url;
    const body = req.method === "POST" ? await req.json().catch(() => ({})) : {};
    const now = Date.now();
    const R = (data) => json(data);

    // ---- 配对码 ----
    if (pathname === "/pair/create") {
      this.sql.exec("DELETE FROM pairing WHERE expires < ?", now);
      const code = String(Math.floor(100000 + Math.random() * 900000));
      const expires = now + 10 * 60 * 1000;
      this.sql.exec("DELETE FROM pairing WHERE uid = ?", body.uid);
      this.sql.exec("INSERT INTO pairing (code, uid, expires) VALUES (?, ?, ?)", code, body.uid, expires);
      return R({ code, expires });
    }
    if (pathname === "/pair/redeem") {
      const q = this.sql.exec("SELECT uid FROM pairing WHERE code = ? AND expires > ?", body.code, now);
      const row = this.rows(q)[0];
      if (!row) return json({ error: "bad_code" }, 400);
      this.sql.exec("DELETE FROM pairing WHERE code = ?", body.code);
      return R({ uid: row.uid });
    }

    // ---- 扫码配对请求 ----
    if (pathname === "/pair-req/create") {
      this.sql.exec("DELETE FROM pair_reqs WHERE expires < ?", now);
      const reqId = randomHex(16);
      const expires = now + 10 * 60 * 1000;
      // 已注册设备再次发起（多教师扫码共享）时携带 did
      const did = /^[A-Za-z0-9_-]+$/.test(body.did || "") ? body.did : "";
      this.sql.exec("INSERT INTO pair_reqs (reqId, name, status, uid, did, token, delivered, created, expires) VALUES (?, ?, 'pending', '', ?, '', 0, ?, ?)",
        reqId, String(body.name || "教室设备").slice(0, 32), did, now, expires);
      return R({ reqId, expires });
    }
    if (pathname === "/pair-req/get") {
      const row = this.rows(this.sql.exec("SELECT * FROM pair_reqs WHERE reqId = ?", body.reqId))[0] || null;
      return R({ request: row });
    }
    if (pathname === "/pair-req/wait") {
      let wait = Math.min(body.wait || 0, 25);
      for (;;) {
        const row = this.rows(this.sql.exec("SELECT * FROM pair_reqs WHERE reqId = ?", body.reqId))[0];
        if (!row || Date.now() > row.expires) return json({ error: "expired" }, 410);
        // 绑定完成且结果尚未被设备取走（token 可能为空 = 关联到已有设备，多教师共享）
        if (row.status === "bound" && !row.delivered) {
          this.sql.exec("UPDATE pair_reqs SET delivered = 1, token = '' WHERE reqId = ?", body.reqId);
          return R({ token: row.token || "", did: row.did, name: row.name });
        }
        if (wait <= 0) return R({ pending: true });
        await new Promise(r => setTimeout(r, 500));
        wait -= 0.5;
      }
    }
    if (pathname === "/pair-req/bind") {
      const row = this.rows(this.sql.exec("SELECT * FROM pair_reqs WHERE reqId = ?", body.reqId))[0];
      if (!row || row.status !== "pending" || Date.now() > row.expires) return json({ error: "expired" }, 410);
      this.sql.exec("UPDATE pair_reqs SET status = 'bound', uid = ?, did = ?, token = ?, delivered = 0 WHERE reqId = ? AND status = 'pending'",
        body.uid, body.did, body.token, body.reqId);
      return R({ ok: true, name: row.name });
    }

    // ---- 设备 ----
    if (pathname === "/devices/add") {
      const did = /^[A-Za-z0-9_-]+$/.test(body.did || "") ? body.did : randomHex(6);
      this.sql.exec("INSERT OR IGNORE INTO devices (did, uid, name, v, createdAt, lastSeen) VALUES (?, ?, ?, 1, ?, ?)",
        did, body.uid, body.name || "未命名设备", now, now);
      this.sql.exec("UPDATE devices SET name = ? WHERE did = ? AND ? <> ''", body.name || "未命名设备", did, body.name || "");
      this.sql.exec("INSERT OR IGNORE INTO device_links (uid, did, created) VALUES (?, ?, ?)", body.uid, did, now);
      const dev = this.rows(this.sql.exec("SELECT * FROM devices WHERE did = ?", did))[0];
      return R({ device: dev });
    }
    if (pathname === "/devices/list") {
      const devices = this.rows(this.sql.exec(
        `SELECT d.* FROM devices d JOIN device_links l ON l.did = d.did
         WHERE l.uid = ? ORDER BY l.created`, body.uid));
      return R({ devices });
    }
    if (pathname === "/devices/list-dids") {
      const dids = this.rows(this.sql.exec("SELECT did FROM device_links WHERE uid = ?", body.uid))
        .map(r => r.did);
      return R({ dids });
    }
    if (pathname === "/devices/link-add") {
      const dev = this.rows(this.sql.exec("SELECT did FROM devices WHERE did = ?", body.did))[0];
      if (!dev) return json({ error: "no_device" }, 404);
      this.sql.exec("INSERT OR IGNORE INTO device_links (uid, did, created) VALUES (?, ?, ?)", body.uid, body.did, now);
      return R({ ok: true });
    }
    if (pathname === "/devices/link-remove") {
      this.sql.exec("DELETE FROM device_links WHERE uid = ? AND did = ?", body.uid, body.did);
      const left = this.rows(this.sql.exec("SELECT COUNT(*) AS n FROM device_links WHERE did = ?", body.did))[0];
      if (!left || Number(left.n) === 0) this.sql.exec("DELETE FROM devices WHERE did = ?", body.did);
      return R({ ok: true });
    }
    if (pathname === "/devices/link-check") {
      const row = this.rows(this.sql.exec("SELECT 1 AS ok FROM device_links WHERE uid = ? AND did = ?",
        body.uid, body.did))[0];
      return R({ linked: !!row });
    }
    // 设备的全部绑定教师（多教师共享：快照/配置按绑定关系分发）
    if (pathname === "/devices/links-for") {
      const uids = this.rows(this.sql.exec("SELECT uid FROM device_links WHERE did = ?", body.did))
        .map(r => r.uid);
      return R({ uids });
    }
    if (pathname === "/devices/remove") {
      this.sql.exec("DELETE FROM device_links WHERE uid = ? AND did = ?", body.uid, body.did);
      const left = this.rows(this.sql.exec("SELECT COUNT(*) AS n FROM device_links WHERE did = ?", body.did))[0];
      if (!left || Number(left.n) === 0) this.sql.exec("DELETE FROM devices WHERE did = ?", body.did);
      return R({ ok: true });
    }
    // 卸载解绑：删除该设备的全部教师关联与设备记录
    if (pathname === "/devices/remove-all") {
      this.sql.exec("DELETE FROM device_links WHERE did = ?", body.did);
      this.sql.exec("DELETE FROM devices WHERE did = ?", body.did);
      return R({ ok: true });
    }
    if (pathname === "/devices/touch") {
      const ver = String(body.version || "");
      const caps = String(body.caps || "");
      this.sql.exec(
        "UPDATE devices SET lastSeen = ?, version = CASE WHEN ? <> '' THEN ? ELSE version END, caps = CASE WHEN ? <> '' THEN ? ELSE caps END WHERE did = ?",
        now, ver, ver, caps, caps, body.did);
      return R({ ok: true });
    }
    if (pathname === "/device/get") {
      const dev = this.rows(this.sql.exec("SELECT * FROM devices WHERE did = ?", body.did))[0] || null;
      return R({ device: dev });
    }
    if (pathname === "/receipts/set") {
      this.sql.exec("DELETE FROM receipts WHERE created < ?", now - 24 * 3600 * 1000);
      this.sql.exec(`INSERT INTO receipts (mid, did, status, created) VALUES (?, ?, ?, ?)
        ON CONFLICT (mid, did) DO UPDATE SET status = excluded.status,
          created = CASE WHEN excluded.status = 'displayed' THEN excluded.created ELSE receipts.created END`,
        String(body.mid || "").slice(0, 16), String(body.did || "").slice(0, 16),
        String(body.status || "received").slice(0, 12), now);
      return R({ ok: true });
    }
    if (pathname === "/receipts/get") {
      const rows = this.rows(this.sql.exec(
        "SELECT did, status, created FROM receipts WHERE mid = ?", String(body.mid || "")))
        .map(r => ({ did: r.did, status: r.status, created: Number(r.created) }));
      return R({ receipts: rows });
    }
    if (pathname === "/clipboard/set") {
      this.sql.exec(`INSERT INTO clipboards (uid, text, fromDid, fromName, created) VALUES (?, ?, ?, ?, ?)
        ON CONFLICT (uid) DO UPDATE SET text = excluded.text,
          fromDid = excluded.fromDid, fromName = excluded.fromName, created = excluded.created`,
        String(body.uid || "").slice(0, 24), String(body.text || "").slice(0, 51200),
        String(body.did || "").slice(0, 16), String(body.name || "").slice(0, 32), now);
      return R({ ok: true });
    }
    if (pathname === "/clipboard/get") {
      const row = this.rows(this.sql.exec(
        "SELECT text, fromDid, fromName, created FROM clipboards WHERE uid = ?", String(body.uid || "").slice(0, 24)))[0] || null;
      return R({ clipboard: row });
    }
    if (pathname === "/notices/set") {
      this.sql.exec("DELETE FROM notices WHERE uid = ? AND text = ? AND name = ?",
        String(body.uid || "").slice(0, 24), String(body.text || "").slice(0, 500), String(body.name || "").slice(0, 32));
      this.sql.exec("INSERT INTO notices (uid, name, text, created) VALUES (?, ?, ?, ?)",
        String(body.uid || "").slice(0, 24), String(body.name || "").slice(0, 32),
        String(body.text || "").slice(0, 500), now);
      this.sql.exec(`DELETE FROM notices WHERE uid = ? AND created NOT IN (
        SELECT created FROM notices WHERE uid = ? ORDER BY created DESC LIMIT 3)`,
        String(body.uid || "").slice(0, 24), String(body.uid || "").slice(0, 24));
      return R({ ok: true });
    }
    if (pathname === "/notices/get") {
      const rows = this.rows(this.sql.exec(
        "SELECT name, text, created FROM notices WHERE uid = ? ORDER BY created DESC LIMIT 3",
        String(body.uid || "").slice(0, 24)))
        .map(r => ({ name: r.name, text: r.text, created: Number(r.created) }));
      return R({ notices: rows });
    }
    if (pathname === "/notices/clear") {
      this.sql.exec("DELETE FROM notices WHERE uid = ?", String(body.uid || "").slice(0, 24));
      return R({ ok: true });
    }
    if (pathname === "/readboard/set") {
      this.sql.exec(`INSERT INTO readboard_state (uid, snapshot, created) VALUES (?, ?, ?)
        ON CONFLICT (uid) DO UPDATE SET snapshot = excluded.snapshot, created = excluded.created`,
        String(body.uid || "").slice(0, 24), String(body.snapshot || "{}").slice(0, 64000), now);
      return R({ ok: true });
    }
    if (pathname === "/readboard/get") {
      const row = this.rows(this.sql.exec(
        "SELECT snapshot, created FROM readboard_state WHERE uid = ?", String(body.uid || "").slice(0, 24)))[0] || null;
      return R({ state: row ? JSON.parse(row.snapshot) : null });
    }
    // 看板快照定时清理（含学生姓名脱敏策略：快照只保留 48h，且设备端只上传座号不传姓名）
    if (pathname === "/readboard/prune") {
      const cutoff = now - (body.hours || 48) * 3600 * 1000;
      this.sql.exec("DELETE FROM readboard_state WHERE created < ?", cutoff);
      return R({ ok: true });
    }
    if (pathname === "/readboard/clear") {
      this.sql.exec("DELETE FROM readboard_state");
      return R({ ok: true });
    }
    if (pathname === "/timetable/set") {
      this.sql.exec(`INSERT INTO timetables (did, json, updatedBy, created) VALUES (?, ?, ?, ?)
        ON CONFLICT (did) DO UPDATE SET json = excluded.json, updatedBy = excluded.updatedBy, created = excluded.created`,
        String(body.did || "").slice(0, 16), String(body.json || "{}").slice(0, 64000),
        String(body.by || "").slice(0, 32), now);
      return R({ ok: true });
    }
    if (pathname === "/timetable/get") {
      const row = this.rows(this.sql.exec(
        "SELECT json, updatedBy, created FROM timetables WHERE did = ?", String(body.did || "").slice(0, 16)))[0] || null;
      return R({ timetable: row ? JSON.parse(row.json) : null, updatedBy: row ? row.updatedBy : "" });
    }
    if (pathname === "/actions/set") {
      this.sql.exec(`INSERT INTO action_sets (uid, json, created) VALUES (?, ?, ?)
        ON CONFLICT (uid) DO UPDATE SET json = excluded.json, created = excluded.created`,
        String(body.uid || "").slice(0, 24), String(body.json || "[]").slice(0, 16000), now);
      return R({ ok: true });
    }
    if (pathname === "/actions/get") {
      const row = this.rows(this.sql.exec(
        "SELECT json FROM action_sets WHERE uid = ?", String(body.uid || "").slice(0, 24)))[0] || null;
      return R({ actions: row ? JSON.parse(row.json) : [] });
    }
    if (pathname === "/devices/all") {
      const devices = this.rows(this.sql.exec("SELECT * FROM devices ORDER BY createdAt"));
      const links = this.rows(this.sql.exec("SELECT uid, did, created FROM device_links ORDER BY created"));
      return R({ devices, links });
    }

    // ---- 文件登记 ----
    if (pathname === "/files/add") {
      this.sql.exec("INSERT OR REPLACE INTO files (fid, uid, name, size, targets, created) VALUES (?, ?, ?, ?, ?, ?)",
        body.fid, body.uid, body.name, body.size, JSON.stringify(body.targets ?? "all"), now);
      // 只保留最近 200 条记录
      this.sql.exec(`DELETE FROM files WHERE fid IN (
        SELECT fid FROM files WHERE uid = ? ORDER BY created DESC LIMIT -1 OFFSET 200)`, body.uid);
      return R({ ok: true });
    }
    if (pathname === "/files/list") {
      const files = this.rows(this.sql.exec(
        "SELECT fid, name, size, targets, pinned, created FROM files WHERE uid = ? ORDER BY pinned DESC, created DESC LIMIT 200", body.uid))
        .map(f => ({ ...f, targets: JSON.parse(f.targets || '"all"'), pinned: !!Number(f.pinned) }));
      return R({ files });
    }
    if (pathname === "/files/get") {
      const f = this.rows(this.sql.exec("SELECT fid, uid, name, size, targets, pinned, created FROM files WHERE fid = ?",
        body.fid))[0] || null;
      if (f) { f.targets = JSON.parse(f.targets || '"all"'); f.pinned = !!Number(f.pinned); }
      return R({ file: f });
    }
    if (pathname === "/files/pin") {
      this.sql.exec("UPDATE files SET pinned = ? WHERE uid = ? AND fid = ?",
        body.pinned ? 1 : 0, String(body.uid || "").slice(0, 24), String(body.fid || ""));
      return R({ ok: true });
    }
    if (pathname === "/files/remove") {
      this.sql.exec("DELETE FROM files WHERE uid = ? AND fid = ?", body.uid, body.fid);
      return R({ ok: true });
    }
    if (pathname === "/files/prune") {
      const cutoff = now - (body.days || 7) * 86400 * 1000;
      const pruned = this.rows(this.sql.exec(
        "SELECT fid, uid FROM files WHERE created < ? AND pinned = 0", cutoff));
      this.sql.exec("DELETE FROM files WHERE created < ? AND pinned = 0", cutoff);
      return R({ pruned });
    }

    // ---- 账号注销清理（v3.2） ----
    if (pathname === "/files/clear") {
      this.sql.exec("DELETE FROM files WHERE uid = ?", String(body.uid || "").slice(0, 24));
      return R({ ok: true });
    }
    if (pathname === "/clipboard/clear") {
      this.sql.exec("DELETE FROM clipboards WHERE uid = ?", String(body.uid || "").slice(0, 24));
      return R({ ok: true });
    }
    if (pathname === "/readboard/clear-user") {
      this.sql.exec("DELETE FROM readboard_state WHERE uid = ?", String(body.uid || "").slice(0, 24));
      return R({ ok: true });
    }
    if (pathname === "/actions/clear") {
      this.sql.exec("DELETE FROM action_sets WHERE uid = ?", String(body.uid || "").slice(0, 24));
      return R({ ok: true });
    }
    if (pathname === "/timetable/clear") {
      this.sql.exec("DELETE FROM timetables WHERE did = ?", String(body.did || "").slice(0, 16));
      return R({ ok: true });
    }

    return json({ error: "unknown_do_route" }, 404);
  }
}
/** 每设备收件箱：id = `${uid}:${did}`，支持长轮询 */
export class InboxDO {
  constructor(state) {
    this.state = state;
    this.sql = state.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS messages (
      seq INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT, payload TEXT, created INTEGER)`);
    // 防御性设置：休眠时运行时自动应答协议层 ping（部分运行时版本不支持则跳过，
    // 此时依赖客户端应用级 KA 每 30s 唤醒 DO 防休眠丢推送）
    try { this.state.setWebSocketAutoResponsePair(new WebSocketRequestResponsePair("ping", "pong")); }
    catch { /* 运行时不支持 */ }
  }

  rows(q) { return q.toArray(); }

  prune() {
    const cutoff = Date.now() - 24 * 3600 * 1000;
    this.sql.exec("DELETE FROM messages WHERE created < ?", cutoff);
    this.sql.exec(`DELETE FROM messages WHERE seq <= (
      SELECT COALESCE(MAX(seq), 0) FROM messages) - 200`);
  }

  sendBacklog(ws, after) {
    const rows = this.rows(this.sql.exec(
      "SELECT seq, type, payload, created FROM messages WHERE seq > ? ORDER BY seq LIMIT 50", after))
      .map(r => ({ seq: Number(r.seq), type: r.type, payload: JSON.parse(r.payload), created: Number(r.created) }));
    if (rows.length) ws.send(JSON.stringify({ type: "messages", messages: rows }));
  }

  async fetch(req) {
    // WebSocket 实时通道（hibernation API：连接休眠时几乎零开销）
    if (req.headers.get("upgrade") === "websocket") {
      const pair = new WebSocketPair();
      this.state.acceptWebSocket(pair[1]);
      const url = new URL(req.url);
      this.sendBacklog(pair[1], parseInt(url.searchParams.get("after") || "0") || 0);
      pair[1].send(JSON.stringify({ type: "ready", now: Date.now() }));
      return new Response(null, { status: 101, webSocket: pair[0] });
    }

    const { pathname } = new URL(req.url);
    const body = req.method === "POST" ? await req.json().catch(() => ({})) : {};
    const R = (data) => json(data);

    if (pathname === "/push") {
      const msg = body.msg || {};
      const created = msg.created || Date.now();
      this.sql.exec("INSERT INTO messages (type, payload, created) VALUES (?, ?, ?)",
        msg.type || "text", JSON.stringify(msg), created);
      this.prune();
      const seq = this.lastSeq();
      const row = { type: "messages", messages: [{ seq, type: msg.type || "text", payload: msg, created }] };
      const data = JSON.stringify(row);
      for (const ws of this.state.getWebSockets()) {
        try { ws.send(data); } catch {}
      }
      return R({ seq });
    }
    if (pathname === "/poll") {
      const after = body.after || 0;
      let wait = Math.min(body.wait || 0, 20);
      for (;;) {
        const rows = this.rows(this.sql.exec(
          "SELECT seq, type, payload, created FROM messages WHERE seq > ? ORDER BY seq LIMIT 50", after))
          .map(r => ({ seq: Number(r.seq), type: r.type, payload: JSON.parse(r.payload), created: Number(r.created) }));
        if (rows.length || wait <= 0) return R({ messages: rows, now: Date.now() });
        await new Promise(r => setTimeout(r, 500));
        wait -= 0.5;
      }
    }
    if (pathname === "/ack") {
      this.sql.exec("DELETE FROM messages WHERE seq <= ?", body.after || 0);
      return R({ ok: true });
    }
    if (pathname === "/clear") {
      this.sql.exec("DELETE FROM messages");
      return R({ ok: true });
    }
    return json({ error: "unknown_do_route" }, 404);
  }

  lastSeq() {
    const r = this.rows(this.sql.exec("SELECT COALESCE(MAX(seq), 0) AS s FROM messages"))[0];
    return Number(r && r.s || 0);
  }

  async webSocketMessage(ws, message) {
    try {
      const data = JSON.parse(message);
      if (data.type === "ping") {
        ws.send(JSON.stringify({ type: "pong", now: Date.now() }));
      } else if (data.type === "ack" && data.after > 0) {
        this.sql.exec("DELETE FROM messages WHERE seq <= ?", data.after);
      }
      // "ka"（保活）: 客户端定期发送以唤醒 DO，防止休眠后服务端推送不到；无需回包
    } catch {}
  }

  async webSocketClose() { /* hibernation 自动清理 */ }
}
