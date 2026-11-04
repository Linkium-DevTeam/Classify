/**
 * 测试替身：最小 KV / R2 / ASSETS / DO 实现（纯内存，不触网）。
 * 仅实现路由用到的 API 形状，行为足以驱动 app.handle()。
 */

/** 最小 KV：Map 实现 get / put / delete / list（支持 prefix / cursor / limit） */
export class MockKV {
  constructor(seed = {}) {
    this.map = new Map();
    for (const [k, v] of Object.entries(seed)) {
      this.map.set(k, typeof v === "string" ? v : JSON.stringify(v));
    }
  }

  async get(key) { return this.map.has(key) ? this.map.get(key) : null; }

  async put(key, value) { this.map.set(key, String(value)); }

  async delete(key) { this.map.delete(key); }

  async list({ prefix = "", cursor, limit = 1000 } = {}) {
    const names = [...this.map.keys()].filter(k => k.startsWith(prefix)).sort();
    const start = cursor ? Number(cursor) : 0;
    const page = names.slice(start, start + limit);
    const next = start + limit < names.length ? String(start + limit) : undefined;
    const out = { keys: page.map(name => ({ name })), list_complete: next === undefined };
    if (next) out.cursor = next;
    return out;
  }
}

/** 最小 R2：put / get / delete / list（list 走 prefix + uploaded 时间） */
export class MockR2 {
  constructor() {
    this.objects = new Map();
  }

  /** 直接植入对象：body 可为字符串，uploaded 为 Date/毫秒 */
  seed(key, body = "x", uploaded = new Date(0), contentType = "application/octet-stream") {
    const text = typeof body === "string" ? body : "x";
    this.objects.set(key, {
      body: text,
      size: typeof body === "string" ? body.length : Number(body) || 0,
      uploaded: uploaded instanceof Date ? uploaded : new Date(uploaded),
      httpMetadata: { contentType },
    });
  }

  async put(key, value, opts = {}) {
    this.seed(key, typeof value === "string" ? value : "blob", new Date(), (opts.httpMetadata || {}).contentType);
    return { key };
  }

  async get(key) { return this.objects.get(key) || null; }

  async delete(key) { this.objects.delete(key); }

  async list({ prefix = "", cursor, limit = 1000 } = {}) {
    const names = [...this.objects.keys()].filter(k => k.startsWith(prefix)).sort();
    const start = cursor ? Number(cursor) : 0;
    const page = names.slice(start, start + limit);
    const next = start + limit < names.length ? String(start + limit) : undefined;
    const out = {
      objects: page.map(key => ({ key, uploaded: this.objects.get(key).uploaded })),
      truncated: next !== undefined,
    };
    if (next) out.cursor = next;
    return out;
  }
}

/** 静态资源绑定：返回 asset:<pathname>，便于断言命中哪条门户路由 */
export function mockAssets() {
  return {
    fetch: async (req) => new Response(`asset:${new URL(req.url).pathname}`, {
      status: 200,
      headers: { "content-type": "text/html" },
    }),
  };
}

/** 极简 GlobalStoreDO stub：覆盖冒烟测试用到的路径 */
export function mockStore() {
  const state = { devices: new Map(), links: new Set(), pairing: new Map() };
  const json = (data, status = 200) => new Response(JSON.stringify(data), {
    status, headers: { "content-type": "application/json" },
  });
  const stub = {
    state,
    fetch: async (input, init) => {
      const path = new URL(input).pathname;
      const body = init && init.body ? JSON.parse(init.body) : {};
      if (path === "/pair/create") {
        state.pairing.set("654321", body.uid);
        return json({ code: "654321", expires: Date.now() + 600000 });
      }
      if (path === "/pair/redeem") {
        const uid = state.pairing.get(body.code);
        if (!uid) return json({ error: "bad_code" }, 400);
        state.pairing.delete(body.code);
        return json({ uid });
      }
      if (path === "/devices/add") {
        const did = body.did || `dev${state.devices.size + 1}`;
        state.devices.set(did, { did, uid: body.uid, name: body.name, v: 1, createdAt: Date.now(), lastSeen: Date.now() });
        state.links.add(`${body.uid}|${did}`);
        return json({ device: state.devices.get(did) });
      }
      if (path === "/device/get") return json({ device: state.devices.get(body.did) || null });
      if (path === "/devices/touch") return json({ ok: true });
      if (path === "/devices/list") {
        const devices = [...state.devices.values()].filter(d => state.links.has(`${body.uid}|${d.did}`));
        return json({ devices });
      }
      if (path === "/devices/list-dids") {
        const dids = [...state.devices.keys()].filter(did => state.links.has(`${body.uid}|${did}`));
        return json({ dids });
      }
      if (path === "/devices/links-for") {
        const uids = [...state.links].filter(k => k.endsWith(`|${body.did}`)).map(k => k.split("|")[0]);
        return json({ uids });
      }
      if (path === "/devices/link-check") return json({ linked: state.links.has(`${body.uid}|${body.did}`) });
      if (path === "/devices/all") return json({ devices: [...state.devices.values()], links: [] });
      if (path === "/files/get") return json({ file: null });
      if (path === "/files/list") return json({ files: [] });
      if (path === "/notices/get") return json({ notices: [] });
      if (path === "/readboard/get") return json({ state: null });
      if (path === "/clipboard/get") return json({ clipboard: null });
      if (path === "/actions/get") return json({ actions: [] });
      if (path === "/timetable/get") return json({ timetable: null, updatedBy: "" });
      if (path === "/receipts/get") return json({ receipts: [] });
      return json({ ok: true });
    },
  };
  return { state, stub, get: () => stub, idFromName: (name) => name };
}

/** 极简 InboxDO stub */
export function mockInbox() {
  const stub = {
    fetch: async () => new Response(JSON.stringify({ ok: true, messages: [], now: Date.now() }), {
      headers: { "content-type": "application/json" },
    }),
  };
  return { stub, get: () => stub, idFromName: (name) => name };
}

/** 组装测试用 env；默认带 SECRET_KEY，测试「未配置」时显式传 undefined */
export function makeEnv(overrides = {}) {
  const store = mockStore();
  const inbox = mockInbox();
  return {
    KV: new MockKV(),
    FILES: new MockR2(),
    ASSETS: mockAssets(),
    STORE: store,
    INBOX: inbox,
    SECRET_KEY: "unit-test-secret-key-0123456789abcdef",
    APP_NAME: "Classify",
    ...overrides,
  };
}
