/**
 * 管理员路由（requireUser 之后，且全部要求 isOwnerUid）。
 *
 * 顺序与原 worker.js 一致：重置码 / 开发者面板 / 公告 / 删号 / 封禁 / 注册开关 /
 * 反馈 / 崩溃 / 看板缓存清理。注：/api/admin/update 与 /api/admin/overview 属密钥级
 * 运维接口，仍在 routes/public.js（先于登录校验执行）。
 */

import { json, fail, readJson } from "../lib/http.js";
import { storeCall } from "../lib/store.js";
import { genResetCode, parseCaps, randomHex, sha256hexAsync } from "../lib/util.js";
import { deleteUserAccount, isOwnerUid } from "../lib/auth.js";

export async function handleAdmin(req, env, url, user) {
  const path = url.pathname, method = req.method;

  // 管理员为任意用户生成一次性重置码（30 分钟有效）
  if (method === "POST" && path === "/api/admin/reset-code") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const { username } = await readJson(req);
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
    const { text, level, hours } = await readJson(req);
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
    const { username } = await readJson(req);
    const name = String(username || "").slice(0, 24);
    if (!name) return fail(400, "bad_request", "缺少用户名");
    if (name === user.un) return fail(400, "bad_request", "不能通过管理员接口删除自己，请使用账号自助注销");
    const r = await deleteUserAccount(env, name);
    if (!r.ok) return fail(r.status, r.code, r.message);
    return json({ ok: true, username: name, removedDevices: (r.removedDids || []).length });
  }
  if (method === "POST" && path === "/api/admin/ban") {
    if (!(await isOwnerUid(env, user.uid))) return fail(403, "forbidden", "仅开发者可操作");
    const { username, reason } = await readJson(req);
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
    const { username } = await readJson(req);
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
    const { open } = await readJson(req);
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

  return null;
}
