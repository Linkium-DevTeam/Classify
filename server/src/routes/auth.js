/**
 * 公开账号路由：注册 / 登录 / 自助密码重置 / 反馈 / 自助注销。
 *
 * 前三者与反馈在 requireUser 之前执行；注销（handleAccountDelete）由教师路由段调用。
 */

import { json, fail, readJson } from "../lib/http.js";
import { rateLimit } from "../lib/rate-limit.js";
import {
  b64urlFromBytes, genResetCode, nowSec, randomHex, safeEqual, sha256hexAsync,
} from "../lib/util.js";
import {
  deleteUserAccount, hashPassword, requireDevice, requireUser, signToken, verifyTurnstile,
} from "../lib/auth.js";

/** 注册 / 登录 / 密码重置（顺序与原 worker.js 一致） */
export async function handleAuthPublic(req, env, url, ip) {
  const path = url.pathname, method = req.method;

  if (method === "POST" && path === "/api/register") {
    // 注册开关（管理员可临时关闭，防滥用）
    if ((await env.KV.get("registration")) === "closed")
      return fail(403, "registration_closed", "注册暂未开放（如有疑问请联系 support@linkium.top）");
    if (!await rateLimit(env, `reg:${ip}`, 10, 3600)) return fail(429, "rate_limited", "注册过于频繁，请稍后再试");
    const { username, password, subject, cfToken } = await readJson(req);
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
    const { username, password, cfToken } = await readJson(req);
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
    // 常数时间比较，避免逐字节提前返回泄露哈希前缀
    if (!safeEqual(hash, rec.hash)) return fail(401, "bad_credentials", "用户名或密码错误");
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
    const { username, code, newPassword } = await readJson(req);
    if (!/^[a-zA-Z0-9_-]{3,24}$/.test(username || "")) return fail(400, "bad_username", "用户名无效");
    if (typeof newPassword !== "string" || newPassword.length < 8 || newPassword.length > 64)
      return fail(400, "bad_password", "密码长度需为 8-64 位");
    const raw = await env.KV.get(`user:${username || ""}`);
    if (!raw) return fail(400, "bad_code", "重置码无效");
    const rec = JSON.parse(raw);
    if (rec.resetCodeExpires && Date.now() > rec.resetCodeExpires) return fail(400, "bad_code", "重置码已过期，请重新生成");
    const codeNorm = String(code || "").trim().toUpperCase();
    if (!rec.resetCodeHash || !codeNorm || !safeEqual(await sha256hexAsync(codeNorm), rec.resetCodeHash))
      return fail(400, "bad_code", "重置码无效");
    rec.hash = await hashPassword(newPassword, rec.salt);
    rec.pv = (rec.pv || 0) + 1;
    rec.resetCodeHash = "";
    await env.KV.put(`user:${username}`, JSON.stringify(rec));
    const token = await signToken(env, { t: "u", uid: rec.uid, un: rec.username, exp: nowSec() + 30 * 86400, pv: rec.pv });
    return json({ ok: true, token, uid: rec.uid, username: rec.username });
  }

  return null;
}

/** 反馈通道：教师（WebUI）或设备均可提交；内容 ≤500 字，设备可附 ≤8KB 脱敏日志。 */
export async function handleFeedback(req, env, url, ip) {
  if (!(req.method === "POST" && url.pathname === "/api/feedback")) return null;
  const u = await requireUser(env, req);
  let device = null;
  if (!u) device = await requireDevice(env, req);
  if (!u && !device) return fail(401, "unauthorized", "请先登录");
  if (!await rateLimit(env, `fb:${ip}`, 10, 3600)) return fail(429, "rate_limited", "反馈过于频繁，请稍后再试");
  const { message, category, logs } = await readJson(req);
  const text = String(message || "").trim();
  if (!text || text.length > 500) return fail(400, "bad_request", "请填写反馈内容（≤500 字）");
  // 存储 KV fb:{yyyymmdd}（每日一卷，≤50 条，30 天定时清理——免费额度纪律：不逐条建键）。
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

/** 自助注销账号（需密码确认防会话劫持误删；联动清理设备/文件/全部账号数据，不可恢复） */
export async function handleAccountDelete(req, env, url, user) {
  if (!(req.method === "POST" && url.pathname === "/api/account/delete")) return null;
  const { password } = await readJson(req);
  const rawU = await env.KV.get(`user:${user.un}`);
  const recU = rawU ? JSON.parse(rawU) : null;
  if (!recU) return fail(404, "not_found", "账号不存在");
  const hash = await hashPassword(String(password || ""), recU.salt);
  if (!safeEqual(hash, recU.hash)) return fail(401, "bad_credentials", "密码错误，账号未删除");
  const r = await deleteUserAccount(env, user.un);
  if (!r.ok) return fail(r.status, r.code, r.message);
  return json({ ok: true, removedDevices: (r.removedDids || []).length });
}
