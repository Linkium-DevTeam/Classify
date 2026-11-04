/**
 * InboxDO — 每设备收件箱（id = did，支持长轮询与 WebSocket hibernation）。
 *
 * messages 表只保留最近 24h / 200 条；WS 走 hibernation API，休眠时几乎零开销。
 */

import { json } from "../lib/http.js";

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
