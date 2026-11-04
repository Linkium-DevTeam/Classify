/**
 * 路由冒烟测试：用内存替身（KV / R2 / ASSETS / DO）驱动 app.handle()。
 *
 * 覆盖：版本与健康检查、SECRET_KEY 门、门户/静态/下载、公开接口、注册登录注销、
 * 设备配对与设备分组顺序、BanError -> 403、OPTIONS、未知路由 404。
 * 全程离线，不访问任何网络。
 */

import test from "node:test";
import assert from "node:assert/strict";

import worker from "../src/index.js";
import { handle } from "../src/app.js";
import { makeEnv, MockKV } from "./_mock.mjs";
import { signToken } from "../src/lib/auth.js";
import { nowSec } from "../src/lib/util.js";

const BASE = "https://classify.test";
const IP = "203.0.113.7";

function req(path, { method = "GET", body, token, headers = {} } = {}) {
  const h = { "cf-connecting-ip": IP, ...headers };
  if (token) h.authorization = `Bearer ${token}`;
  if (body !== undefined && !h["content-type"]) h["content-type"] = "application/json";
  return new Request(BASE + path, {
    method,
    headers: h,
    body: body === undefined ? undefined : (typeof body === "string" ? body : JSON.stringify(body)),
  });
}

const get = (path, env, opts = {}) => handle(req(path, opts), env);
const post = (path, body, env, opts = {}) => handle(req(path, { ...opts, method: "POST", body }), env);

/* ------------------------- 版本 / 健康检查 ------------------------- */

test("GET /api/health：无需 SECRET_KEY，返回版本与 build 且 no-store", async () => {
  const env = makeEnv({ SECRET_KEY: undefined });
  const res = await get("/api/health", env);
  assert.equal(res.status, 200);
  assert.match(res.headers.get("content-type"), /application\/json/);
  assert.equal(res.headers.get("cache-control"), "no-store");
  const body = await res.json();
  assert.deepEqual(Object.keys(body), ["ok", "version", "build", "serverTime"]);
  assert.equal(body.ok, true);
  assert.equal(body.version, "4.0.0");
  assert.equal(body.build, "2026-11-04");
  assert.equal(typeof body.serverTime, "number");
});

test("GET /api/version：返回 name/version/liteVersion/minVersion/serverTime/build", async () => {
  const env = makeEnv();
  const res = await get("/api/version", env);
  assert.equal(res.status, 200);
  const body = await res.json();
  assert.deepEqual(Object.keys(body), ["name", "version", "liteVersion", "minVersion", "serverTime", "build"]);
  assert.equal(body.name, "Classify");
  assert.equal(body.version, "4.0.0");
  assert.equal(body.liteVersion, "1.1.0");
  assert.equal(body.build, "2026-11-04");
  assert.equal(body.minVersion, "");
});

test("未配置 SECRET_KEY：/api/health 可用，其余 /api/* 返回 503 no_secret", async () => {
  const env = makeEnv({ SECRET_KEY: undefined });
  assert.equal((await get("/api/health", env)).status, 200);
  const res = await get("/api/version", env);
  assert.equal(res.status, 503);
  const body = await res.json();
  assert.equal(body.error, "no_secret");
  assert.match(body.message, /SECRET_KEY/);
});

/* ------------------------- 门户 / 静态 / 下载 ------------------------- */

test("门户路由：/ 与 /app /lite 命中对应静态资源", async () => {
  const env = makeEnv();
  assert.equal(await (await get("/", env)).text(), "asset:/welcome");
  assert.equal(await (await get("/app", env)).text(), "asset:/");
  assert.equal(await (await get("/lite", env)).text(), "asset:/lite/");
  const asset = await get("/icons/logo.png", env);
  assert.equal(asset.headers.get("x-content-type-options"), "nosniff");
  assert.equal(asset.headers.get("cache-control"), "no-cache");
  assert.match(asset.headers.get("content-security-policy"), /default-src 'self'/);
});

test("下载直链：按 uploaded 取 R2 里最新的安装包（不再硬编码 3.6.5）", async () => {
  const env = makeEnv();
  env.FILES.seed("dist/Classify-Setup-3.6.5.exe", "old", new Date("2026-01-01T00:00:00Z"));
  env.FILES.seed("dist/Classify-Setup-4.0.0.exe", "newest", new Date("2026-06-01T00:00:00Z"));
  env.FILES.seed("dist/Clite-1.0.0-portable.exe", "clite", new Date("2026-02-01T00:00:00Z"));
  env.FILES.seed("dist/Clite-2.0.0-portable.exe", "clite2", new Date("2026-03-01T00:00:00Z"));
  env.FILES.seed("dist/classify-update-3.6.5.zip", "zip", new Date("2026-04-01T00:00:00Z"));
  env.FILES.seed("dist/notes.txt", "nope", new Date("2026-07-01T00:00:00Z"));

  const setup = await get("/download/classify-setup", env);
  assert.equal(setup.status, 200);
  assert.equal(setup.headers.get("content-type"), "application/octet-stream");
  assert.equal(setup.headers.get("content-disposition"), 'attachment; filename="Classify-Setup-4.0.0.exe"');
  assert.equal(await setup.text(), "newest");

  const clite = await get("/download/clite", env);
  assert.equal(clite.headers.get("content-disposition"), 'attachment; filename="Clite-2.0.0-portable.exe"');

  const upd = await get("/download/classify-update", env);
  assert.equal(upd.headers.get("content-disposition"), 'attachment; filename="classify-update-3.6.5.zip"');
});

test("下载直链：文件名安全化，去掉引号/换行等危险字符", async () => {
  const env = makeEnv();
  env.FILES.seed('dist/Classify-Setup-4.0.0 "beta".exe', "x", new Date("2026-06-01T00:00:00Z"));
  const res = await get("/download/classify-setup", env);
  assert.equal(res.status, 200);
  const cd = res.headers.get("content-disposition");
  assert.match(cd, /^attachment; filename="[A-Za-z0-9._-]+"$/);
  assert.equal(cd.includes('"beta"'), false);
});

test("下载直链：找不到安装包返回 404 原提示；未知 /download 路径回落静态资源", async () => {
  const env = makeEnv();
  const missing = await get("/download/clite", env);
  assert.equal(missing.status, 404);
  const body = await missing.json();
  assert.equal(body.error, "not_found");
  assert.equal(body.message, "安装包暂未就绪，请稍后再试");

  const other = await get("/download/something-else", env);
  assert.equal(await other.text(), "asset:/download/something-else");
});

/* ------------------------- 公开接口与 404 / OPTIONS ------------------------- */

test("OPTIONS /api/* 返回 204", async () => {
  const env = makeEnv();
  const opt = await handle(req("/api/me", { method: "OPTIONS" }), env);
  assert.equal(opt.status, 204);
  assert.equal(await opt.text(), "");
});

test("未知 /api 路由：未登录 401，已登录 404 接口不存在", async () => {
  const env = makeEnv();
  const anon = await get("/api/not-a-route", env);
  assert.equal(anon.status, 401);

  await env.KV.put("user:known", JSON.stringify({ uid: "u-known", username: "known", salt: "c2FsdA", hash: "x", pv: 0 }));
  const token = await signToken(env, { t: "u", uid: "u-known", un: "known", exp: nowSec() + 60, pv: 0 });
  const res = await get("/api/not-a-route", env, { token });
  assert.equal(res.status, 404);
  assert.deepEqual(await res.json(), { error: "not_found", message: "接口不存在" });
});

test("GET /api/config、/api/broadcast 公开可读", async () => {
  const env = makeEnv({ TURNSTILE_SITE_KEY: "site-key" });
  assert.deepEqual(await (await get("/api/config", env)).json(),
    { name: "Classify", turnstileSiteKey: "site-key" });

  assert.deepEqual(await (await get("/api/broadcast", env)).json(), { broadcast: null });
  await env.KV.put("broadcast", JSON.stringify({ id: "b1", text: "停机维护", level: "warn", createdAt: 1, expiresAt: Date.now() + 60000 }));
  const b = await (await get("/api/broadcast", env)).json();
  assert.deepEqual(b.broadcast, { id: "b1", text: "停机维护", level: "warn", createdAt: 1 });
});

test("POST /api/telemetry：kind 非法 400，合法计数成功", async () => {
  const env = makeEnv();
  const bad = await post("/api/telemetry", { kind: "nope" }, env);
  assert.equal(bad.status, 400);
  assert.equal((await bad.json()).error, "bad_kind");

  const ok = await post("/api/telemetry", { kind: "app", version: "4.0.0", events: [{ name: "start", count: 2 }] }, env);
  assert.equal(ok.status, 200);
  assert.deepEqual(await ok.json(), { ok: true });
});

test("GET /api/update/latest：无清单返回 version none，有清单原样返回且 no-store", async () => {
  const env = makeEnv();
  assert.deepEqual(await (await get("/api/update/latest", env)).json(), { version: "none" });
  const manifest = { version: "4.0.1", url: "https://example.com/a.exe", sha256: "abc" };
  await env.KV.put("update:latest", JSON.stringify(manifest));
  const res = await get("/api/update/latest", env);
  assert.equal(res.headers.get("cache-control"), "no-store");
  assert.deepEqual(await res.json(), manifest);
});

test("POST /api/admin/update：无密钥 403，有密钥发布成功", async () => {
  const env = makeEnv();
  const denied = await post("/api/admin/update", { version: "4.0.1" }, env);
  assert.equal(denied.status, 403);
  assert.equal((await denied.json()).error, "forbidden");

  const ok = await post("/api/admin/update",
    { version: "4.0.1", url: "https://example.com/a.exe", sha256: "abc" },
    env, { headers: { "x-admin-key": env.SECRET_KEY } });
  assert.equal(ok.status, 200);
  const body = await ok.json();
  assert.equal(body.ok, true);
  assert.equal(body.manifest.version, "4.0.1");
});

test("POST /api/diag/boot：控制字符替换为空格，超 16KB 丢弃", async () => {
  const env = makeEnv();
  const res = await post("/api/diag/boot", "a\u0000b\nc\u001bd", env);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { ok: true });
  const keys = [...env.KV.map.keys()].filter(k => k.startsWith("diag:boot:"));
  assert.equal(keys.length, 1);
  assert.equal(env.KV.map.get(keys[0]), "a b c d");

  const env2 = makeEnv();
  await post("/api/diag/boot", "x".repeat(16 * 1024 + 1), env2, { headers: { "cf-connecting-ip": "203.0.113.8" } });
  assert.equal([...env2.KV.map.keys()].filter(k => k.startsWith("diag:boot:")).length, 0);
});

/* ------------------------- 账号：注册 / 登录 / 注销 ------------------------- */

test("注册 → 登录 → /api/me；密码错误 401；注销需密码确认", async () => {
  const env = makeEnv();
  const reg = await post("/api/register", { username: "teacher1", password: "password123", subject: "数学" }, env);
  assert.equal(reg.status, 200);
  const regBody = await reg.json();
  assert.equal(regBody.username, "teacher1");
  assert.equal(regBody.subject, "数学");
  assert.equal(regBody.resetCode.length, 12);
  assert.ok(regBody.token);

  const me = await get("/api/me", env, { token: regBody.token });
  assert.equal(me.status, 200);
  const meBody = await me.json();
  assert.equal(meBody.username, "teacher1");
  assert.equal(meBody.subject, "数学");
  assert.equal(meBody.role, "user");
  assert.deepEqual(meBody.devices, []);

  const dup = await post("/api/register", { username: "teacher1", password: "password123" }, env);
  assert.equal(dup.status, 409);
  assert.equal((await dup.json()).error, "exists");

  const bad = await post("/api/login", { username: "teacher1", password: "wrong-password" }, env);
  assert.equal(bad.status, 401);
  assert.deepEqual(await bad.json(), { error: "bad_credentials", message: "用户名或密码错误" });

  const ok = await post("/api/login", { username: "teacher1", password: "password123" }, env);
  assert.equal(ok.status, 200);
  const loginBody = await ok.json();
  assert.equal(loginBody.username, "teacher1");

  const del = await post("/api/account/delete", { password: "wrong-password" }, env, { token: loginBody.token });
  assert.equal(del.status, 401);
  assert.deepEqual(await del.json(), { error: "bad_credentials", message: "密码错误，账号未删除" });

  const delOk = await post("/api/account/delete", { password: "password123" }, env, { token: loginBody.token });
  assert.equal(delOk.status, 200);
  assert.deepEqual(await delOk.json(), { ok: true, removedDevices: 0 });
  assert.equal(await env.KV.get("user:teacher1"), null);
});

test("注册：用户名/密码校验与中文提示", async () => {
  const env = makeEnv();
  const badName = await post("/api/register", { username: "a b", password: "password123" }, env);
  assert.equal(badName.status, 400);
  assert.deepEqual(await badName.json(),
    { error: "bad_username", message: "用户名需为 3-24 位字母、数字、_ 或 -" });

  const badPw = await post("/api/register", { username: "teacher2", password: "short" }, env);
  assert.equal(badPw.status, 400);
  assert.deepEqual(await badPw.json(), { error: "bad_password", message: "密码长度需为 8-64 位" });
});

test("未登录访问教师接口返回 401 请先登录", async () => {
  const env = makeEnv();
  for (const path of ["/api/me", "/api/devices", "/api/announce/history", "/api/admin/panel"]) {
    const res = await get(path, env);
    assert.equal(res.status, 401, path);
    assert.equal((await res.json()).message, "请先登录", path);
  }
});

test("封禁用户令牌：顶层 BanError 转 403 banned", async () => {
  const env = makeEnv();
  await env.KV.put("user:banned1", JSON.stringify({
    uid: "u-banned", username: "banned1", salt: "c2FsdA", hash: "x", pv: 0, banned: true, banReason: "测试封禁",
  }));
  const token = await signToken(env, { t: "u", uid: "u-banned", un: "banned1", exp: nowSec() + 60, pv: 0 });
  const res = await worker.fetch(req("/api/me", { token }), env);
  assert.equal(res.status, 403);
  const body = await res.json();
  assert.equal(body.error, "banned");
  assert.equal(body.message, "账号已被封禁：测试封禁（如有疑问请联系 support@linkium.top）");
});

/* ------------------------- 设备：配对与路由顺序 ------------------------- */

test("设备配对：配对码格式、兑换失败、成功返回设备令牌", async () => {
  const env = makeEnv();
  const badFmt = await post("/api/device/register", { code: "12" }, env);
  assert.equal(badFmt.status, 400);
  assert.deepEqual(await badFmt.json(), { error: "bad_code", message: "配对码为 6 位数字" });

  const badCode = await post("/api/device/register", { code: "000000" }, env);
  assert.equal(badCode.status, 400);
  assert.deepEqual(await badCode.json(), { error: "bad_code", message: "配对码无效或已过期，请在手机端重新生成" });

  env.STORE.state.pairing.set("123456", "u-dev");
  const ok = await post("/api/device/register", { code: "123456", name: "教室A" }, env);
  assert.equal(ok.status, 200);
  const body = await ok.json();
  assert.equal(body.deviceName, "教室A");
  assert.ok(body.deviceId);
  assert.ok(body.token);
  assert.equal(typeof body.serverTime, "number");

  const info = await get("/api/device/info", env, { token: body.token });
  assert.equal(info.status, 200);
  assert.equal((await info.json()).device.did, body.deviceId);
});

test("/api/device/register：单 IP 每小时 30 次，第 31 次 429", async () => {
  const env = makeEnv();
  for (let i = 0; i < 30; i++) {
    const res = await post("/api/device/register", { code: "12" }, env);
    assert.equal(res.status, 400, `第 ${i + 1} 次不应被限流`);
  }
  const limited = await post("/api/device/register", { code: "12" }, env);
  assert.equal(limited.status, 429);
  assert.equal((await limited.json()).error, "rate_limited");
});

test("/api/device/* 整组先于教师路由：无令牌一律设备 401，而不是「请先登录」", async () => {
  const env = makeEnv();
  for (const path of ["/api/device/ws", "/api/device/action-set", "/api/device/subject-teacher",
    "/api/device/teacher-info", "/api/device/nope"]) {
    const res = await get(path, env);
    assert.equal(res.status, 401, path);
    assert.deepEqual(await res.json(), { error: "unauthorized", message: "设备令牌无效，请重新配对" }, path);
  }
});

test("/api/device/ws 与 action-set：支持 ?t= 查询参数令牌", async () => {
  const env = makeEnv();
  env.STORE.state.devices.set("dev-q", { did: "dev-q", uid: "u1", name: "教室Q", v: 1 });
  const token = await signToken(env, { t: "d", did: "dev-q", v: 1, exp: nowSec() + 60 });

  const act = await get(`/api/device/action-set?t=${token}&uid=u1234`, env);
  assert.equal(act.status, 200);
  assert.deepEqual(await act.json(), { actions: [], uid: "u1234" });

  const badUid = await get(`/api/device/action-set?t=${token}&uid=%20`, env);
  assert.equal(badUid.status, 400);

  // WS：转发给 InboxDO（替身），不校验设备存活之外的额外条件
  const ws = await get(`/api/device/ws?t=${token}&after=0`, env);
  assert.equal(ws.status, 200);
  assert.deepEqual((await ws.json()).ok, true);

  // 已吊销（版本号不符）的令牌：WS 在转发前拒绝
  env.STORE.state.devices.set("dev-q", { did: "dev-q", uid: "u1", name: "教室Q", v: 2 });
  const revoked = await get(`/api/device/ws?t=${token}&after=0`, env);
  assert.equal(revoked.status, 401);
  assert.equal((await revoked.json()).message, "设备令牌无效，请重新配对");
});

test("教师令牌无效时不会被当成设备令牌", async () => {
  const env = makeEnv();
  await env.KV.put("user:t1", JSON.stringify({ uid: "u1", username: "t1", salt: "c2FsdA", hash: "x", pv: 0 }));
  const userToken = await signToken(env, { t: "u", uid: "u1", un: "t1", exp: nowSec() + 60, pv: 0 });
  const res = await get("/api/device/info", env, { token: userToken });
  assert.equal(res.status, 401);
  assert.equal((await res.json()).message, "设备令牌无效，请重新配对");
});

/* ------------------------- 管理员 ------------------------- */

test("管理员接口：非管理员 403，管理员可读面板", async () => {
  const env = makeEnv();
  await env.KV.put("user:owner", JSON.stringify({ uid: "u-owner", username: "owner", salt: "c2FsdA", hash: "x", pv: 0 }));
  await env.KV.put("owners", JSON.stringify(["u-owner"]));
  const token = await signToken(env, { t: "u", uid: "u-owner", un: "owner", exp: nowSec() + 60, pv: 0 });

  const panel = await get("/api/admin/panel", env, { token });
  assert.equal(panel.status, 200);
  const body = await panel.json();
  assert.deepEqual(body.users.map(u => u.username), ["owner"]);
  assert.equal(body.registrationOpen, true);

  const denied = await post("/api/admin/ban", { username: "owner" }, env);
  assert.equal(denied.status, 401);

  const notOwnerEnv = makeEnv();
  await notOwnerEnv.KV.put("user:t2", JSON.stringify({ uid: "u2", username: "t2", salt: "c2FsdA", hash: "x", pv: 0 }));
  const t2 = await signToken(notOwnerEnv, { t: "u", uid: "u2", un: "t2", exp: nowSec() + 60, pv: 0 });
  const forbidden = await get("/api/admin/panel", notOwnerEnv, { token: t2 });
  assert.equal(forbidden.status, 403);
  assert.deepEqual(await forbidden.json(), { error: "forbidden", message: "仅开发者可访问" });
});

/* ------------------------- 顶层错误处理 ------------------------- */

test("顶层错误处理：未知异常转 500 internal（KV 故障）", async () => {
  const broken = makeEnv({ KV: new MockKV() });
  broken.KV.get = () => { throw new Error("kv exploded"); };
  const res = await worker.fetch(req("/api/broadcast"), broken);
  assert.equal(res.status, 500);
  const body = await res.json();
  assert.equal(body.error, "internal");
  assert.match(body.message, /kv exploded/);
});
