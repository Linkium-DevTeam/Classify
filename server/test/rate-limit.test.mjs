/**
 * 限流单测：固定窗口计数、bucket 隔离、KV 异常时放行。
 * 用内存 MockKV，不触网。
 */

import test from "node:test";
import assert from "node:assert/strict";

import { rateLimit } from "../src/lib/rate-limit.js";
import { MockKV } from "./_mock.mjs";

test("rateLimit：窗口内超过阈值返回 false", async () => {
  const env = { KV: new MockKV() };
  assert.equal(await rateLimit(env, "test:ip", 2, 3600), true);
  assert.equal(await rateLimit(env, "test:ip", 2, 3600), true);
  assert.equal(await rateLimit(env, "test:ip", 2, 3600), false);
  assert.equal(await rateLimit(env, "test:ip", 2, 3600), false);
});

test("rateLimit：不同 bucket 互不影响", async () => {
  const env = { KV: new MockKV() };
  assert.equal(await rateLimit(env, "a:1.1.1.1", 1, 3600), true);
  assert.equal(await rateLimit(env, "a:1.1.1.1", 1, 3600), false);
  assert.equal(await rateLimit(env, "b:1.1.1.1", 1, 3600), true);
  assert.equal(await rateLimit(env, "a:2.2.2.2", 1, 3600), true);
});

test("rateLimit：KV 异常时放行（尽力而为）", async () => {
  const env = { KV: { get() { throw new Error("kv down"); }, put() { throw new Error("kv down"); } } };
  assert.equal(await rateLimit(env, "x", 0, 60), true);
});
