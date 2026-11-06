/**
 * 纯函数工具集：base64url、随机串、时间戳、能力位解析、常数时间比较、哈希辅助。
 *
 * 本模块不依赖 env / KV / R2，可直接在 node:test 中单测（见 test/util.test.mjs）。
 */

export const enc = new TextEncoder();

/** 字节数组 → base64url（无 padding） */
export function b64urlFromBytes(buf) {
  let s = "";
  const b = new Uint8Array(buf);
  for (let i = 0; i < b.length; i++) s += String.fromCharCode(b[i]);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** base64url（无 padding）→ 字节数组；非法字符会抛错（与原实现一致） */
export function bytesFromB64url(str) {
  str = str.replace(/-/g, "+").replace(/_/g, "/");
  while (str.length % 4) str += "=";
  const bin = atob(str);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

/** 字符串 → base64url（无 padding） */
export function b64url(str) {
  return b64urlFromBytes(enc.encode(str));
}

/** 随机十六进制风格串（base64url 去掉非字母数字字符并截断，沿用旧实现） */
export const randomHex = (n) => b64urlFromBytes(crypto.getRandomValues(new Uint8Array(n))).slice(0, n * 2);

/**
 * 随机数字串（默认 6 位，配对码用）。
 * 用 crypto.getRandomValues 生成，250 = 25 × 10 去除取模偏差。
 */
export function randomDigits(n = 6) {
  const len = Math.max(0, Math.floor(Number(n) || 0));
  const out = [];
  const buf = new Uint8Array(1);
  while (out.length < len) {
    crypto.getRandomValues(buf);
    if (buf[0] < 250) out.push(String(buf[0] % 10));
  }
  return out.join("");
}

/** 容错 JSON 解析：KV/R2 中损坏的数据不应让整个请求 500 */
export function safeJson(raw, fallback) {
  try { return JSON.parse(raw || ""); } catch { return fallback; }
}

/** 当前 Unix 秒 */
export const nowSec = () => Math.floor(Date.now() / 1000);

/** 设备能力位解析：缺省（含 v2 老设备无字段）视为全开 */
export function parseCaps(raw) {
  let c = {};
  try { c = raw ? JSON.parse(raw) || {} : {}; } catch { c = {}; }
  return { camera: c.camera !== false, power: c.power !== false, clipboard: c.clipboard !== false };
}

/**
 * 常数时间比较（字符串或 Uint8Array）。
 * 长度不同直接 false（长度不是机密）；长度相同时逐字节异或累积，不提前返回。
 */
export function safeEqual(a, b) {
  if (typeof a === "string" && typeof b === "string") {
    if (a.length !== b.length) return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
    return diff === 0;
  }
  if (a instanceof Uint8Array && b instanceof Uint8Array) {
    if (a.length !== b.length) return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
    return diff === 0;
  }
  return false;
}

/** SHA-256 摘要（base64url 截断 32 位，沿用旧实现的取值方式） */
export async function sha256hexAsync(s) {
  const b = await crypto.subtle.digest("SHA-256", enc.encode(s));
  return b64urlFromBytes(b).replace(/[+/=]/g, "").slice(0, 32);
}

/** 12 位大写自助重置码（去易混字符，约 55 bit 熵，一次性使用） */
export function genResetCode() {
  const alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
  const bytes = crypto.getRandomValues(new Uint8Array(12));
  let s = "";
  for (const b of bytes) s += alphabet[b % alphabet.length];
  return s;
}
