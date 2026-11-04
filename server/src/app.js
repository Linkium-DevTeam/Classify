/**
 * 应用编排：门户 / 静态资源 / 下载直链 / OPTIONS / SECRET_KEY 检查 + 主路由分发。
 *
 * 路由顺序（与原 worker.js 完全一致）：
 *   1. 公开路由（诊断/版本/配置/公告/遥测/更新清单/运维总览）
 *   2. 账号公开路由（注册/登录/密码重置）
 *   3. 设备公开路由（配对兑换/扫码绑定）
 *   4. 反馈（教师或设备令牌）
 *   5. 解析教师令牌 → /api/device/* 整组（含 ?t= 特例，先于教师路由）
 *   6. 教师路由前半段 → 管理员路由 → 教师路由后半段
 *   7. 未登录 401 / 未知路由 404
 */

import { json, fail, serveAsset, downloadResponse } from "./lib/http.js";
import { requireUser } from "./lib/auth.js";
import { isDownloadRoute, resolveDownload } from "./lib/downloads.js";
import { VERSION, BUILD } from "./version.js";
import { handlePublic } from "./routes/public.js";
import { handleAuthPublic, handleFeedback } from "./routes/auth.js";
import { handleDevicePublic, handleDevice } from "./routes/device.js";
import { handleTeacher, handleTeacherTail } from "./routes/teacher.js";
import { handleAdmin } from "./routes/admin.js";

/** 下载直链：实时解析 R2 dist/ 里最新的安装包（不再硬编码版本号） */
async function handleDownload(env, path) {
  if (!isDownloadRoute(path)) return null;
  const hit = await resolveDownload(env, path);
  const obj = hit ? await env.FILES.get(hit.key) : null;
  if (!obj) return fail(404, "not_found", "安装包暂未就绪，请稍后再试");
  return downloadResponse(obj, hit.filename);
}

export async function handle(req, env) {
  const url = new URL(req.url);
  const p0 = url.pathname;

  /* ---- 门户路由：/ 官网+下载 · /app 教师端 WebUI · /lite 极简控制页 · /download 安装包 ---- */
  /* serveAsset 一律用"美化路径"（绝不 .html），避免 assets 层 pretty-URL 307 规范化 */
  if (p0 === "/") return serveAsset(env, req, "/welcome");
  if (p0 === "/app" || p0 === "/app/") return serveAsset(env, req, "/");
  if (p0 === "/lite" || p0 === "/lite/") return serveAsset(env, req, "/lite/");
  if (p0.startsWith("/lite/api/")) {
    // 反向代理到 clite worker（固定 https + 固定 host，无用户可控成分），页面侧保持同源免 CORS
    const target = "https://clite.linkium.top/api/" + p0.slice("/lite/api/".length) + url.search;
    return fetch(new Request(target, req));
  }
  if (p0.startsWith("/download/")) {
    const dl = await handleDownload(env, p0);
    if (dl) return dl;
  }

  if (!url.pathname.startsWith("/api/")) {
    return serveAsset(env, req, url.pathname);
  }

  if (req.method === "OPTIONS") return new Response(null, { status: 204 });

  // 健康检查：不要求 SECRET_KEY（密钥未配置/轮换期间仍可探活），禁止缓存
  if (req.method === "GET" && p0 === "/api/health") {
    return json(
      { ok: true, version: VERSION, build: BUILD, serverTime: Date.now() },
      200,
      { "cache-control": "no-store" },
    );
  }

  if (!env.SECRET_KEY) return fail(503, "no_secret", "服务端未配置 SECRET_KEY，请先执行: wrangler secret put SECRET_KEY");

  return route(req, env, url);
}

export async function route(req, env, url) {
  const path = url.pathname;
  const ip = req.headers.get("cf-connecting-ip") || "unknown";

  /* ---------- 公开接口（requireUser 之前） ---------- */
  let res = await handlePublic(req, env, url, ip);
  if (res) return res;

  res = await handleAuthPublic(req, env, url, ip);
  if (res) return res;

  res = await handleDevicePublic(req, env, url, ip);
  if (res) return res;

  res = await handleFeedback(req, env, url, ip);
  if (res) return res;

  /* ---------- 教师令牌：原实现在设备分组之前解析，此处保持同一语义 ---------- */
  const user = await requireUser(env, req);

  /* ---------- 设备接口整组（在教师路由之前） ---------- */
  if (path.startsWith("/api/device/")) return handleDevice(req, env, url);

  if (!user) return fail(401, "unauthorized", "请先登录");

  /* ---------- 教师接口（含夹在其中的管理员接口） ---------- */
  res = await handleTeacher(req, env, url, user);
  if (res) return res;

  res = await handleAdmin(req, env, url, user);
  if (res) return res;

  res = await handleTeacherTail(req, env, url, user);
  if (res) return res;

  return fail(404, "not_found", "接口不存在");
}
