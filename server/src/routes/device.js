/**
 * 设备路由：/api/device/*
 *
 * 分两段：
 *  1) handleDevicePublic —— 配对兑换 / 扫码绑定（requireUser 之前，公开）；
 *  2) handleDevice       —— 设备令牌鉴权后的整组接口，必须在教师路由之前匹配。
 *
 * 注意：/ws、/action-set、/subject-teacher、/teacher-info 支持 ?t= 令牌（WS 客户端兼容），
 * 因此这四个必须分布在 requireDevice 之前处理。
 */

import { json, fail, readJson, fileResponse } from "../lib/http.js";
import { rateLimit } from "../lib/rate-limit.js";
import { inboxCall, storeCall } from "../lib/store.js";
import { genResetCode, nowSec, sha256hexAsync } from "../lib/util.js";
import { bearer, requireDevice, signToken, verifyToken } from "../lib/auth.js";

/* ------------------------- 公开：配对与扫码绑定 ------------------------- */

export async function handleDevicePublic(req, env, url, ip) {
  const path = url.pathname, method = req.method;

  // 桌面端配对：无需登录，凭 6 位配对码
  if (method === "POST" && path === "/api/device/register") {
    // 单 IP 每小时 30 次，防止配对码被爆破（配对码兑换失败的提示保持原样）
    if (!await rateLimit(env, `regdev:${ip}`, 30, 3600))
      return fail(429, "rate_limited", "尝试过于频繁，请稍后再试");
    const { code, name, did } = await readJson(req);
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
    const { name, did } = await readJson(req);
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

  return null;
}

/* ------------------------- 设备令牌鉴权后的整组接口 ------------------------- */

/** 校验 ?t= 或 Authorization 里的设备令牌；无效返回 null */
async function deviceFromQuery(req, env, url) {
  const p = await verifyToken(env, url.searchParams.get("t") || bearer(req));
  return p && p.t === "d" ? p : null;
}

/** 设备令牌 + 未吊销校验（令牌来自 query/header 时替代 requireDevice） */
async function liveDevice(env, p) {
  const dev = await storeCall(env, "/device/get", { did: p.did });
  const row = dev.body && dev.body.device;
  return row && row.v === p.v ? row : null;
}

export async function handleDevice(req, env, url) {
  const path = url.pathname, method = req.method;

  // WS 通道自带 ?t= 鉴权（WS 客户端兼容），须在 requireDevice 之前处理
  if (method === "GET" && path === "/api/device/ws") {
    const p = await deviceFromQuery(req, env, url);
    if (!p) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
    const row = await liveDevice(env, p);
    if (!row) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
    await storeCall(env, "/devices/touch", { did: p.did, version: String(url.searchParams.get("v") || "").slice(0, 16) });
    const stub = env.INBOX.get(env.INBOX.idFromName(p.did));
    // 原样转发：保留 Request 与 Upgrade 头，交由 InboxDO 完成 101 握手
    return stub.fetch(new Request(
      `https://do/ws?after=${encodeURIComponent(url.searchParams.get("after") || "0")}`, req));
  }
  // 候课操作集拉取：支持 ?t= 令牌，须在 requireDevice 之前处理
  if (method === "GET" && path === "/api/device/action-set") {
    const p = await deviceFromQuery(req, env, url);
    if (!p) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
    if (!await liveDevice(env, p)) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
    const uid = url.searchParams.get("uid") || "";
    if (!/^[A-Za-z0-9_-]{4,24}$/.test(uid)) return fail(400, "bad_request", "uid 无效");
    const r = await storeCall(env, "/actions/get", { uid });
    return json({ actions: r.body.actions || [], uid });
  }
  // 学科教师解析：支持 ?t= 令牌，须在 requireDevice 之前处理
  if (method === "GET" && path === "/api/device/subject-teacher") {
    const p = await deviceFromQuery(req, env, url);
    if (!p) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
    if (!await liveDevice(env, p)) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
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
    const p = await deviceFromQuery(req, env, url);
    if (!p) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
    if (!await liveDevice(env, p)) return fail(401, "unauthorized", "设备令牌无效，请重新配对");
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
    const hb = await readJson(req);
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
    const { text } = await readJson(req);
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
    const { snapshot } = await readJson(req);
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
    const { mid, status } = await readJson(req);
    if (!mid || !["received", "displayed", "failed"].includes(status))
      return fail(400, "bad_request", "回执参数缺失");
    await storeCall(env, "/receipts/set", { mid, did: device.did, status });
    return json({ ok: true });
  }
  if (method === "POST" && path === "/api/device/ack") {
    const { after } = await readJson(req);
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
    return fileResponse(obj, f.name);
  }
  return fail(404, "not_found", "接口不存在");
}