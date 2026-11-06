/**
 * HTTP 辅助：JSON / 错误响应、请求体读取、静态资源与下载响应、安全响应头。
 *
 * 所有 JSON 响应统一 content-type: application/json; charset=utf-8。
 */

export const JSON_HEADERS = {
  "content-type": "application/json; charset=utf-8",
  "x-content-type-options": "nosniff",
  "referrer-policy": "no-referrer",
};

/** 安全响应头（静态资源与下载共用，均为无副作用的保守值） */
export const SECURITY_HEADERS = {
  "x-content-type-options": "nosniff",
  "referrer-policy": "no-referrer",
};

const ASSET_CSP =
  "default-src 'self'; script-src 'self' https://challenges.cloudflare.com; " +
  "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https://challenges.cloudflare.com; " +
  "connect-src 'self' https://challenges.cloudflare.com; " +
  "frame-src https://challenges.cloudflare.com; font-src 'self'; " +
  "object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

export const json = (data, status = 200, headers = {}) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { ...JSON_HEADERS, ...headers },
  });

export const fail = (status, code, message) => json({ error: code, message }, status);

/**
 * 读取 JSON 请求体；解析失败返回 {}（等价于旧实现的 req.json().catch(() => ({}))）。
 * 注意：合法但为 null 的 JSON 会原样返回 null，调用方解构时行为与旧实现一致。
 */
export async function readJson(req) {
  try {
    const value = await req.json();
    return value && typeof value === "object" ? value : {};
  } catch { return {}; }
}

/** 静态资源：走 ASSETS 绑定，统一补安全响应头（一律用"美化路径"，绝不 .html） */
export async function serveAsset(env, req, assetPath) {
  const resp = await env.ASSETS.fetch(new Request(new URL(req.url).origin + assetPath, req));
  const headers = new Headers(resp.headers);
  headers.set("x-content-type-options", SECURITY_HEADERS["x-content-type-options"]);
  headers.set("referrer-policy", SECURITY_HEADERS["referrer-policy"]);
  headers.set("cache-control", "no-cache");
  headers.set("content-security-policy", ASSET_CSP);
  return new Response(resp.body, { status: resp.status, headers });
}

/** 安装包下载响应（/download/*）：文件名已由调用方安全化 */
export function downloadResponse(obj, filename) {
  return new Response(obj.body, {
    headers: {
      "content-type": "application/octet-stream",
      "content-disposition": `attachment; filename="${filename}"`,
      "content-length": String(obj.size),
      "cache-control": "no-cache",
      "x-content-type-options": "nosniff",
      "referrer-policy": "no-referrer",
    },
  });
}

/**
 * R2 对象响应（设备/教师取文件用）。
 * inline=true 用于 WebUI 内联预览（课件/图片），否则按附件下载。
 */
export function fileResponse(obj, filename, { inline = false } = {}) {
  const headers = {
    "content-type": (obj.httpMetadata && obj.httpMetadata.contentType) || "application/octet-stream",
    "content-length": String(obj.size),
    "x-content-type-options": SECURITY_HEADERS["x-content-type-options"],
  };
  try {
    headers["content-disposition"] =
      `${inline ? "inline" : "attachment"}; filename*=UTF-8''${encodeURIComponent(filename)}`;
  } catch { /* 文件名不可编码时省略 content-disposition（沿用旧实现） */ }
  return new Response(obj.body, { headers });
}
