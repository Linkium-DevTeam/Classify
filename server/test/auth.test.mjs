/**
 * 令牌与口令单测：signToken / verifyToken（正常、篡改、过期、错误密钥、格式非法）、
 * bearer 解析、PBKDF2 哈希稳定性。全部离线执行。
 */

import test from "node:test";
import assert from "node:assert/strict";

import { bearer, hashPassword, signToken, verifyToken } from "../src/lib/auth.js";
import { b64url, b64urlFromBytes, bytesFromB64url, nowSec, safeEqual } from "../src/lib/util.js";

const env = { SECRET_KEY: "unit-test-secret-key-0123456789abcdef" };

test("signToken/verifyToken：正常签发与校验", async () => {
  const token = await signToken(env, { t: "u", uid: "u1", un: "teacher", exp: nowSec() + 60, pv: 2 });
  assert.match(token, /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/);
  const p = await verifyToken(env, token);
  assert.ok(p);
  assert.equal(p.t, "u");
  assert.equal(p.uid, "u1");
  assert.equal(p.un, "teacher");
  assert.equal(p.pv, 2);
});

test("signToken：同一 payload 每次签名一致（HMAC 无随机量）", async () => {
  const payload = { t: "d", did: "dev1", v: 1, exp: nowSec() + 60 };
  const a = await signToken(env, payload);
  const b = await signToken(env, payload);
  assert.equal(a, b);
});

test("verifyToken：篡改 payload 后失效", async () => {
  const token = await signToken(env, { t: "u", uid: "u1", exp: nowSec() + 60 });
  const [body, sig] = token.split(".");
  const forged = b64url(JSON.stringify({ t: "u", uid: "attacker", exp: nowSec() + 60 }));
  assert.equal(await verifyToken(env, `${forged}.${sig}`), null);
  // 原 token 仍有效，确认上面的失败源于签名而非解码
  assert.ok(await verifyToken(env, token));
  assert.notEqual(body, forged);
});

test("verifyToken：篡改签名（同长度字节）后失效", async () => {
  const token = await signToken(env, { t: "u", uid: "u1", exp: nowSec() + 60 });
  const [body, sig] = token.split(".");
  const bytes = bytesFromB64url(sig);
  bytes[0] ^= 1;
  const tampered = `${body}.${b64urlFromBytes(bytes)}`;
  assert.equal(tampered.length, token.length);
  assert.equal(await verifyToken(env, tampered), null);
});

test("verifyToken：签名长度不符返回 null", async () => {
  const token = await signToken(env, { t: "u", uid: "u1", exp: nowSec() + 60 });
  const body = token.split(".")[0];
  assert.equal(await verifyToken(env, `${body}.YWJj`), null);
});

test("verifyToken：过期令牌返回 null", async () => {
  const token = await signToken(env, { t: "u", uid: "u1", exp: nowSec() - 1 });
  assert.equal(await verifyToken(env, token), null);
});

test("verifyToken：缺少 exp 返回 null", async () => {
  const token = await signToken(env, { t: "u", uid: "u1" });
  assert.equal(await verifyToken(env, token), null);
});

test("verifyToken：错误密钥返回 null", async () => {
  const token = await signToken({ SECRET_KEY: "another-secret-key" }, { t: "u", uid: "u1", exp: nowSec() + 60 });
  assert.equal(await verifyToken(env, token), null);
});

test("verifyToken：无 SECRET_KEY / 空 token 返回 null", async () => {
  const token = await signToken(env, { t: "u", uid: "u1", exp: nowSec() + 60 });
  assert.equal(await verifyToken({}, token), null);
  assert.equal(await verifyToken(env, ""), null);
  assert.equal(await verifyToken(env, null), null);
});

test("verifyToken：格式非法返回 null", async () => {
  assert.equal(await verifyToken(env, "nodot"), null);
  assert.equal(await verifyToken(env, ".sig"), null);
});

test("bearer：解析 Authorization 头", () => {
  const mk = (value) => new Request("https://x/", { headers: value === null ? {} : { authorization: value } });
  assert.equal(bearer(mk("Bearer abc.def")), "abc.def");
  assert.equal(bearer(mk("Bearer   spaced  ")), "spaced");
  assert.equal(bearer(mk("Basic abc")), null);
  assert.equal(bearer(mk(null)), null);
});

test("hashPassword：同盐同密码稳定，异密码不同", async () => {
  const salt = b64urlFromBytes(new Uint8Array(16).fill(7));
  const a = await hashPassword("password123", salt);
  const b = await hashPassword("password123", salt);
  const c = await hashPassword("password124", salt);
  assert.equal(a, b);
  assert.notEqual(a, c);
  assert.ok(safeEqual(a, b));
  assert.equal(safeEqual(a, c), false);
  assert.match(a, /^[A-Za-z0-9_-]+$/);
});
