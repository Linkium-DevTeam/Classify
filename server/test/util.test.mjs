/**
 * 纯函数单测：safeEqual / parseCaps / randomDigits / b64url / 重置码。
 * 不依赖网络、不依赖 KV。
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  b64url, b64urlFromBytes, bytesFromB64url, genResetCode, nowSec,
  parseCaps, randomDigits, randomHex, safeEqual, sha256hexAsync,
} from "../src/lib/util.js";

test("safeEqual：相同字符串为 true", () => {
  assert.equal(safeEqual("abc123", "abc123"), true);
  assert.equal(safeEqual("", ""), true);
});

test("safeEqual：长度相同但内容不同为 false（逐位比较）", () => {
  assert.equal(safeEqual("abc123", "abc124"), false);
  assert.equal(safeEqual("abc123", "Abc123"), false);
  assert.equal(safeEqual("000000", "000001"), false);
  // 只差最后一位：仍必须完全比较，不能因提前返回而误判
  assert.equal(safeEqual("a".repeat(64) + "x", "a".repeat(64) + "y"), false);
});

test("safeEqual：长度不同为 false", () => {
  assert.equal(safeEqual("abc", "abcd"), false);
  assert.equal(safeEqual("", "a"), false);
});

test("safeEqual：Uint8Array 走字节比较", () => {
  assert.equal(safeEqual(new Uint8Array([1, 2, 3]), new Uint8Array([1, 2, 3])), true);
  assert.equal(safeEqual(new Uint8Array([1, 2, 3]), new Uint8Array([1, 2, 4])), false);
  assert.equal(safeEqual(new Uint8Array([1, 2]), new Uint8Array([1, 2, 3])), false);
});

test("safeEqual：类型不匹配为 false", () => {
  assert.equal(safeEqual("abc", new Uint8Array([97, 98, 99])), false);
  assert.equal(safeEqual(null, null), false);
  assert.equal(safeEqual(undefined, "a"), false);
});

test("parseCaps：缺省 / 空值视为全开", () => {
  assert.deepEqual(parseCaps(""), { camera: true, power: true, clipboard: true });
  assert.deepEqual(parseCaps(undefined), { camera: true, power: true, clipboard: true });
  assert.deepEqual(parseCaps(null), { camera: true, power: true, clipboard: true });
  assert.deepEqual(parseCaps("null"), { camera: true, power: true, clipboard: true });
});

test("parseCaps：非法 JSON 回归为全开", () => {
  assert.deepEqual(parseCaps("{not json"), { camera: true, power: true, clipboard: true });
});

test("parseCaps：false 关闭对应能力，其余保持开启", () => {
  assert.deepEqual(parseCaps('{"camera":false}'), { camera: false, power: true, clipboard: true });
  assert.deepEqual(parseCaps('{"power":false,"clipboard":false}'), { camera: true, power: false, clipboard: false });
  assert.deepEqual(parseCaps('{"camera":false,"power":false,"clipboard":false}'),
    { camera: false, power: false, clipboard: false });
});

test("parseCaps：非布尔值按开启处理（与旧逻辑一致）", () => {
  assert.deepEqual(parseCaps('{"camera":0,"power":"no"}'), { camera: true, power: true, clipboard: true });
});

test("randomDigits：长度与字符集", () => {
  for (let i = 0; i < 100; i++) {
    const code = randomDigits(6);
    assert.equal(code.length, 6);
    assert.match(code, /^\d{6}$/);
  }
  for (const n of [1, 2, 4, 8, 12]) {
    const s = randomDigits(n);
    assert.equal(s.length, n);
    assert.match(s, new RegExp(`^\\d{${n}}$`));
  }
});

test("randomDigits：0 位返回空串，缺省 6 位", () => {
  assert.equal(randomDigits(0), "");
  assert.equal(randomDigits().length, 6);
});

test("randomDigits：足够随机（多次不重复）", () => {
  const seen = new Set();
  for (let i = 0; i < 300; i++) seen.add(randomDigits(6));
  // 30 万种组合里取 300 个，重复应极少（留足余量避免偶发失败）
  assert.ok(seen.size > 290, `唯一码数量过少：${seen.size}`);
});

test("randomHex：只含 base64url 字符", () => {
  const s = randomHex(8);
  assert.ok(s.length > 0 && s.length <= 16);
  assert.match(s, /^[A-Za-z0-9_-]+$/);
});

test("b64url：编码后不含 + / =，且可往返", () => {
  const raw = "ClassCall Lite 教师端 ?&/+= 测试";
  const encoded = b64url(raw);
  assert.doesNotMatch(encoded, /[+/=]/);
  assert.equal(new TextDecoder().decode(bytesFromB64url(encoded)), raw);
  assert.equal(b64url("abc"), "YWJj");
  assert.equal(new TextDecoder().decode(bytesFromB64url(b64urlFromBytes(new TextEncoder().encode("abc")))), "abc");
});

test("nowSec：为当前秒级时间戳", () => {
  const t = nowSec();
  assert.ok(Math.abs(t - Math.floor(Date.now() / 1000)) <= 2);
  assert.ok(Number.isInteger(t));
});

test("sha256hexAsync：稳定且长度为 32", async () => {
  const a = await sha256hexAsync("same-input");
  const b = await sha256hexAsync("same-input");
  assert.equal(a, b);
  assert.equal(a.length, 32);
  assert.match(a, /^[A-Za-z0-9_-]{32}$/);
  assert.notEqual(a, await sha256hexAsync("other-input"));
});

test("genResetCode：12 位、去易混字符", () => {
  const seen = new Set();
  for (let i = 0; i < 50; i++) {
    const code = genResetCode();
    assert.equal(code.length, 12);
    assert.match(code, /^[ABCDEFGHJKMNPQRSTVWXYZ23456789]{12}$/);
    assert.doesNotMatch(code, /[IO0L1]/);
    seen.add(code);
  }
  assert.ok(seen.size > 45);
});
