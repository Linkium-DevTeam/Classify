/**
 * 限流（KV 计数，尽力而为）：固定窗口内累加，KV 异常时放行。
 *
 * key = rl:<bucket>:<窗口序号>，窗口长度 = windowSec，过期自动清理。
 */

export async function rateLimit(env, bucket, limit, windowSec) {
  try {
    const win = Math.floor(Date.now() / 1000 / windowSec);
    const key = `rl:${bucket}:${win}`;
    const n = parseInt((await env.KV.get(key)) || "0", 10) + 1;
    await env.KV.put(key, String(n), { expirationTtl: windowSec });
    return n <= limit;
  } catch { return true; }
}
