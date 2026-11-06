/**
 * 鉴权与账号：HMAC 无状态令牌、PBKDF2 口令哈希、身份门卫、Turnstile、注销清理。
 *
 * 令牌格式：base64url(JSON payload) . base64url(HMAC-SHA256)
 *   t=u 教师令牌 { uid, un, pv, exp } / t=d 设备令牌 { did, v, exp }
 */

import { storeCall, inboxCall } from "./store.js";
import {
  b64urlFromBytes, bytesFromB64url, enc, nowSec, safeEqual,
} from "./util.js";

/** 封禁用户访问时抛出，统一在 index.js 顶层转为 403 */
export class BanError extends Error {
  constructor(rec) {
    super((rec && rec.banReason ? `账号已被封禁：${rec.banReason}` : "账号已被封禁")
      + "（如有疑问请联系 support@linkium.top）");
    this.banned = true;
  }
}

/** 导入 HMAC 密钥（SECRET_KEY 为 wrangler secret） */
export async function hmac(secret) {
  return crypto.subtle.importKey("raw", enc.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
}

/** 签发令牌 */
export async function signToken(env, payload) {
  const body = b64urlFromBytes(enc.encode(JSON.stringify(payload)));
  const key = await hmac(env.SECRET_KEY);
  const sig = await crypto.subtle.sign("HMAC", key, enc.encode(body));
  return `${body}.${b64urlFromBytes(sig)}`;
}

/** 校验令牌：签名用 safeEqual 常数时间比较，过期/格式错一律返回 null */
export async function verifyToken(env, token) {
  if (!env.SECRET_KEY || !token) return null;
  const dot = token.indexOf(".");
  if (dot <= 0) return null;
  const body = token.slice(0, dot), sig = token.slice(dot + 1);
  const key = await hmac(env.SECRET_KEY);
  const expect = await crypto.subtle.sign("HMAC", key, enc.encode(body));
  let got;
  try { got = bytesFromB64url(sig); } catch { return null; }
  if (!safeEqual(got, new Uint8Array(expect))) return null;
  try {
    const payload = JSON.parse(new TextDecoder().decode(bytesFromB64url(body)));
    if (!payload.exp || payload.exp < nowSec()) return null;
    return payload;
  } catch { return null; }
}

/** 从 Authorization: Bearer 取令牌 */
export function bearer(req) {
  const h = req.headers.get("authorization") || "";
  return h.startsWith("Bearer ") ? h.slice(7).trim() : null;
}

/** 口令哈希（PBKDF2-SHA256，10 万次迭代，Workers 上限） */
export async function hashPassword(password, salt) {
  const key = await crypto.subtle.importKey("raw", enc.encode(password), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits(
    // Workers WebCrypto 的 PBKDF2 迭代上限为 100000
    { name: "PBKDF2", hash: "SHA-256", salt: bytesFromB64url(salt), iterations: 100000 }, key, 256);
  return b64urlFromBytes(bits);
}

/**
 * 教师身份门卫：令牌有效 + t=u + KV 用户存在 + pv 匹配（重置/封禁即失效）。
 * 封禁用户抛 BanError，由顶层转 403。
 */
export async function requireUser(env, req) {
  const p = await verifyToken(env, bearer(req));
  if (!p || p.t !== "u") return null;
  // 密码重置会递增 pv：旧令牌即刻失效（pv 不匹配 = 已被重置/更换凭据）
  const raw = await env.KV.get(`user:${p.un || ""}`);
  if (!raw) return null;
  let rec;
  try { rec = JSON.parse(raw); } catch { return null; }
  if (!rec || typeof rec !== "object") return null;
  if ((rec.pv || 0) !== (p.pv || 0)) return null;
  // 封禁用户：所有需要教师身份的接口一律拒绝（封禁同时递增 pv 使既有令牌失效）
  if (rec.banned) throw new BanError(rec);
  return p;
}

/** 设备身份门卫：令牌有效 + t=d + 设备存在且版本号匹配（吊销即失效） */
export async function requireDevice(env, req) {
  const p = await verifyToken(env, bearer(req));
  if (!p || p.t !== "d") return null;
  // 令牌只证明"这是哪台设备"；教师与设备的多对多关联在 device_links 中
  const dev = await storeCall(env, "/device/get", { did: p.did });
  const row = dev.body && dev.body.device;
  if (!row || row.v !== p.v) return null; // 已吊销
  return p;
}

/** 开发者/运营权限：仅部署者本人账号（KV owners 数组） */
export async function isOwnerUid(env, uid) {
  if (!uid) return false;
  // 迁移：老部署的 admins（首个注册者自动成为管理员）一次性转为 owners；注册不再自动授予。
  if (!(await env.KV.get("owners"))) {
    const legacy = await env.KV.get("admins");
    if (legacy) await env.KV.put("owners", legacy);
  }
  let owners;
  try { owners = JSON.parse(await env.KV.get("owners") || "[]"); } catch { return false; }
  return Array.isArray(owners) && owners.includes(uid);
}

/** Turnstile 人机验证（未配置密钥 = 未启用） */
export async function verifyTurnstile(env, token, ip) {
  if (!env.TURNSTILE_SECRET_KEY) return true; // 未配置 = 未启用
  if (!token) return false;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5000);
  try {
    const resp = await fetch("https://challenges.cloudflare.com/turnstile/v0/siteverify", {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ secret: env.TURNSTILE_SECRET_KEY, response: token, remoteip: ip || "" }),
      signal: controller.signal,
    });
    const out = await resp.json();
    return !!out.success;
  } catch { return false; }
  finally { clearTimeout(timer); }
}

/** 注销账号：解除设备关联（末位关联连带删设备并清收件箱/课表）、清 R2 文件与登记、
 *  清账号级数据（看板/规则/操作集/通知/剪贴板）、管理员数组摘除，最后删除用户记录（令牌即刻失效）。 */
export async function deleteUserAccount(env, username) {
  const raw = await env.KV.get(`user:${username}`);
  if (!raw) return { ok: false, status: 404, code: "not_found", message: "用户不存在" };
  let rec;
  try { rec = JSON.parse(raw); } catch { return { ok: false, status: 500, code: "corrupt", message: "账号数据损坏" }; }
  if (!rec || typeof rec !== "object") return { ok: false, status: 500, code: "corrupt", message: "账号数据损坏" };
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
