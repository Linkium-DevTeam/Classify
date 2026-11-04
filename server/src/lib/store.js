/**
 * Durable Object 调用封装。
 *
 * STORE 单实例（idFromName("global")）：配对码 / 设备表 / 文件登记；
 * INBOX 每设备一实例（idFromName(did)）：消息收件箱。
 */

/** 调 GlobalStoreDO：返回 { ok, status, body }，解析失败给空对象 */
export async function storeCall(env, path, data) {
  const stub = env.STORE.get(env.STORE.idFromName("global"));
  const resp = await stub.fetch("https://do" + path, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(data || {}),
  });
  const body = await resp.json().catch(() => ({}));
  return { ok: resp.ok, status: resp.status, body };
}

/** 调 InboxDO：直接返回 JSON 体 */
export async function inboxCall(env, key, path, data) {
  const stub = env.INBOX.get(env.INBOX.idFromName(key));
  const resp = await stub.fetch("https://do" + path, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(data || {}),
  });
  return resp.json();
}
