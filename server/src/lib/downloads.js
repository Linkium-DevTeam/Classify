/**
 * 安装包下载解析（/download/*）。
 *
 * 不再硬编码 3.6.5 之类版本号：实时列举 R2 dist/ 目录，按 uploaded 时间取最新匹配项，
 * 文件名做安全化（只保留 [A-Za-z0-9._-]）。列举不到时可用 env.DOWNLOAD_MANIFEST
 * （JSON）兜底，值可以是 R2 key 或 { key, name }。
 */

/** 路由 → dist/ 下的文件名匹配规则 */
const ROUTES = {
  "/download/classify-setup": /^Classify-Setup-.+\.exe$/i,
  "/download/clite": /^Clite-.+portable\.exe$/i,
  "/download/classify-update": /^classify-update-.+\.zip$/i,
};

const DIST_PREFIX = "dist/";
const MAX_PAGES = 10; // 防御性上限：1000 × 10 个对象足够放安装包

/** 该路径是否为受支持的下载直链 */
export function isDownloadRoute(path) {
  return Object.prototype.hasOwnProperty.call(ROUTES, path);
}

/** 文件名安全化：取最后一段，只保留 [A-Za-z0-9._-]，防止头部注入与路径穿越 */
export function safeFileName(name) {
  const base = String(name || "").split("/").pop() || "";
  const cleaned = base.replace(/[^A-Za-z0-9._-]/g, "_").replace(/^\.+/, "");
  return cleaned || "download.bin";
}

/** 列出 dist/ 下的一层对象（key/name/uploaded 毫秒时间戳） */
async function listDist(env) {
  const out = [];
  let cursor;
  for (let page = 0; page < MAX_PAGES; page++) {
    const res = await env.FILES.list({ prefix: DIST_PREFIX, cursor, limit: 1000 });
    for (const o of res.objects || []) {
      const name = o.key.slice(DIST_PREFIX.length);
      if (!name || name.includes("/")) continue; // 只看 dist/ 顶层
      out.push({ key: o.key, name, uploaded: o.uploaded ? new Date(o.uploaded).getTime() : 0 });
    }
    if (!res.truncated) break;
    cursor = res.cursor;
  }
  return out;
}

/** 从 DOWNLOAD_MANIFEST 兜底配置里取 key（支持路由名或短名作为键） */
function fromManifest(env, path) {
  if (!env.DOWNLOAD_MANIFEST) return null;
  let manifest;
  try { manifest = JSON.parse(env.DOWNLOAD_MANIFEST); } catch { return null; }
  if (!manifest || typeof manifest !== "object") return null;
  const short = path.replace("/download/", "");
  const hit = manifest[path] ?? manifest[short] ?? null;
  if (!hit) return null;
  const key = typeof hit === "string" ? hit : String(hit.key || "");
  const name = typeof hit === "string" ? key : String(hit.name || key);
  if (!/^dist\/[A-Za-z0-9._-]+$/.test(key)) return null;
  return { key, filename: safeFileName(name) };
}

/**
 * 解析下载路由 → { key, filename }；无可用安装包返回 null。
 * 优先 R2 实时列举（按上传时间取最新），列举失败/为空时回落到 DOWNLOAD_MANIFEST。
 */
export async function resolveDownload(env, path) {
  const re = ROUTES[path];
  if (!re) return null;
  try {
    const entries = await listDist(env);
    let best = null;
    for (const e of entries) {
      if (!re.test(e.name)) continue;
      if (!best || e.uploaded > best.uploaded) best = e;
    }
    if (best) return { key: best.key, filename: safeFileName(best.name) };
  } catch { /* R2 列举失败时走 manifest 兜底 */ }
  return fromManifest(env, path);
}
