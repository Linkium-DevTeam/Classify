/**
 * GlobalStoreDO — 全局存储（单实例，idFromName("global")）。
 *
 * SQLite 存配对码 / 设备表 / 设备-教师多对多关联 / 文件登记 / 看板 / 课表 / 操作集 / 回执。
 * 表结构与 SQL 与原 worker.js 完全一致（含自愈迁移）。
 */

import { json } from "../lib/http.js";
import { randomHex, randomDigits } from "../lib/util.js";

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
      // 6 位数字配对码：crypto.getRandomValues 生成，不用 Math.random
      const code = randomDigits(6);
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
