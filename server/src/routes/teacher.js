/**
 * 教师路由（requireUser 之后）。
 *
 * 原 worker.js 中这段被管理员路由切成前后两段，为保持路由先后顺序拆成两个导出：
 *  handleTeacher     —— 扫码绑定 / 账号 / 设备列表 / 课堂控制 / 通知 / 看板 / 课表 / 操作集 / 回执
 *  handleTeacherTail —— admin 段之后的设备删除 / 喊话 / 文件 / 指令
 *
 * 返回 Response 表示已处理，返回 null 表示交给后续路由。
 */

import { json, fail, readJson, fileResponse } from "../lib/http.js";
import { inboxCall, storeCall } from "../lib/store.js";
import { nowSec, parseCaps, randomHex } from "../lib/util.js";
import { isOwnerUid, signToken } from "../lib/auth.js";
import { handleAccountDelete } from "./auth.js";

export async function handleTeacher(req, env, url, user) {
  const path = url.pathname, method = req.method;

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
    const { reqId } = await readJson(req);
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
  const deleted = await handleAccountDelete(req, env, url, user);
  if (deleted) return deleted;

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
    const { type, payload, targets } = await readJson(req);
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
    const { text } = await readJson(req);
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
    return fileResponse(obj, f.name, { inline: true });
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
    const { mode, ratio } = await readJson(req);
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
    const { avatar } = await readJson(req);
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
    const { greeting } = await readJson(req);
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
    const { text, targets } = await readJson(req);
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
    const body = await readJson(req);
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
    const { actions } = await readJson(req);
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

  return null;
}

export async function handleTeacherTail(req, env, url, user) {
  const path = url.pathname, method = req.method;

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
    const { text, speak, targets } = await readJson(req);
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
    const { pinned } = await readJson(req);
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
    const { name, value, targets } = await readJson(req);
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

  return null;
}
