/* Classify WebUI — 核心：状态、API、视图切换与通用工具
 * 经典脚本（非 ES Module）：与 compose/classroom/settings/boot 共享全局作用域。
 * 为了跨脚本可用，$ / $$ / state / DEMO 使用 var 声明（成为 window 属性），
 * 同时统一挂到 window.CC，便于调试与后续迁移。
 * 注意：本文件不直接绑定业务事件，事件统一在 boot.js 中注册。
 */
"use strict";

const CC = (window.CC = window.CC || {});

var $ = (s) => document.querySelector(s);
var $$ = (s) => Array.from(document.querySelectorAll(s));
var state = {
  token: localStorage.getItem("cc_token") || null,
  uid: null, username: null,
  devices: [], files: [],
  targets: new Set(),           // 选中的设备 did
  pairTimer: null,
  deferredInstall: null,
  pendingBind: null,            // 扫码绑定请求（/bind?req=xxx）
  subject: "其他",
  role: "user",                 // admin | user
  avatar: "", turnstileSiteKey: "",
};

/* ---------------- API 封装 ---------------- */

var DEMO = new URLSearchParams(location.search).has("demo");

async function api(path, opts = {}) {
  if (DEMO) return mockApi(path, opts);
  const headers = opts.headers || {};
  if (state.token) headers["authorization"] = "Bearer " + state.token;
  if (opts.json !== undefined) {
    headers["content-type"] = "application/json";
    opts = { ...opts, body: JSON.stringify(opts.json), json: undefined };
  }
  let resp;
  try {
    resp = await fetch("/api" + path, { ...opts, headers });
  } catch {
    throw new Error("网络不可用，请检查连接");
  }
  if (resp.status === 401 && !path.startsWith("/login") && !path.startsWith("/register")) {
    logout(false);
    throw new Error("登录已过期，请重新登录");
  }
  const data = await resp.json().catch(() => ({}));
  if (!resp.ok) throw new Error(data.message || ("请求失败 (" + resp.status + ")"));
  return data;
}

/* ---------------- Toast / Modal ---------------- */

let toastTimer = null;
function toast(msg, isErr = false) {
  const t = $("#toast");
  t.textContent = msg;
  t.classList.toggle("err", isErr);
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), 2600);
}

function modal({ title, body, okText = "确定", danger = false, onOk }) {
  $("#modal-title").textContent = title;
  const b = $("#modal-body");
  if (typeof body === "string") b.innerHTML = '<p style="margin:0">' + body + '</p>';
  else { b.innerHTML = ""; b.appendChild(body); }
  const ok = $("#modal-ok");
  ok.textContent = okText;
  // app.css 只定义了 btn-filled / btn-outline-danger；旧代码的 btn-primary/btn-danger 无样式
  ok.className = "btn " + (danger ? "btn-outline-danger" : "btn-filled");
  $("#modal").hidden = false;
  ok.onclick = () => { $("#modal").hidden = true; onOk && onOk(); };
  $("#modal-cancel").onclick = () => ($("#modal").hidden = true);
}

/* ---------------- 视图切换 ---------------- */

function showAuth() {
  $("#app-view").hidden = true;
  $("#auth-view").hidden = false;
}

function showApp() {
  $("#auth-view").hidden = true;
  $("#app-view").hidden = false;
  $("#hdr-name").textContent = state.username || "";
  $("#hdr-avatar").textContent = (state.username || "师")[0].toUpperCase();
  $("#st-username").textContent = state.username || "—";
  // 跨文件调用：/me 的展示与数据刷新依赖 settings/compose/classroom 中的函数，
  // 这些脚本在 boot.js 之前加载，调用发生在运行时，因此安全。
  api("/me").then((me) => {
    state.subject = me.subject || "其他";
    state.role = me.role || "user";
    state.avatar = me.avatar || "";
    applyAvatar();
    // 固定问候（无需自定义）
    const banner = $("#welcome-greeting");
    if (banner) banner.textContent = "👋 你好，" + state.username + "老师";
    // 学科注册后不可更改：下拉框禁用，仅展示
    const subSel = $("#st-subject");
    if (subSel) { subSel.value = state.subject; subSel.disabled = true; }
    // 管理员专属 UI 显隐
    document.querySelectorAll(".admin-only").forEach((el) => (el.hidden = state.role !== "admin"));
    document.querySelectorAll(".user-only").forEach((el) => (el.hidden = state.role === "admin"));
  }).catch(() => {});
  $("#st-uid").textContent = state.uid || "—";
  $("#st-server").textContent = location.origin;
  $("#st-server").title = location.origin;
  switchTab("announce");
  refreshDevices().catch(() => {});
  refreshFiles().catch(() => {});
  loadRbConfig().catch(() => {});
  refreshBroadcast().catch(() => {});
  renderQuick();
  renderHistory().catch(() => {});
  if (state.pendingBind) showBindFlow();
  // 设备在线状态自动刷新（20 秒）
  if (!state._devRefreshTimer) {
    state._devRefreshTimer = setInterval(() => {
      if (!document.hidden && state.token) refreshDevices().catch(() => {});
    }, 20000);
  }
  // 服务公告轮询（5 分钟）
  if (!state._bcTimer) {
    state._bcTimer = setInterval(() => {
      if (!document.hidden) refreshBroadcast().catch(() => {});
    }, 5 * 60000);
  }
  // 匿名遥测（可开关，默认开；仅页面打开次数，无任何内容与身份）
  const telBox = $("#st-telemetry");
  if (telBox) {
    telBox.checked = localStorage.getItem("cc_telemetry") !== "0";
    telBox.onchange = () => localStorage.setItem("cc_telemetry", telBox.checked ? "1" : "0");
  }
  if (localStorage.getItem("cc_telemetry") !== "0" && !DEMO) {
    api("/telemetry", { method: "POST", json: { kind: "web", version: "web" } }).catch(() => {});
  }
}

function applyAvatar() {
  const el = $("#hdr-avatar");
  if (state.avatar) {
    el.textContent = "";
    el.style.backgroundImage = "url(" + state.avatar + ")";
    el.style.backgroundSize = "cover";
    el.style.backgroundPosition = "center";
    el.classList.add("has-img");
    const big = $("#st-avatar-preview");
    if (big) big.src = state.avatar;
  } else {
    el.textContent = (state.username || "师")[0].toUpperCase();
    el.style.backgroundImage = "";
    el.classList.remove("has-img");
    const big = $("#st-avatar-preview");
    if (big) big.src = "/icons/devteam.png";
  }
}

function switchTab(name) {
  $$(".nav-item").forEach(b => b.classList.toggle("active", b.dataset.tab === name));
  $$(".tab").forEach(t => t.classList.toggle("active", t.id === "tab-" + name));
  if (name === "devices") refreshDevices().catch(() => {});
  if (name === "files") refreshFiles().catch(() => {});
  if (name === "announce") { renderQuick(); if (!DEMO) renderHistory().catch(() => {}); }
  if (name === "settings" && !DEMO) loadPreclass().catch(() => {});
}

/* ---------------- 服务公告（管理员发布，全员可见） ---------------- */

async function refreshBroadcast() {
  const el = $("#svc-banner");
  if (!el) return;
  try {
    const r = await fetch("/api/broadcast").then((x) => x.json());
    const b = r && r.broadcast;
    if (!b) { el.hidden = true; return; }
    el.textContent = b.text;
    el.className = "svc-banner " + (b.level || "info");
    el.hidden = false;
  } catch { el.hidden = true; }
}

/* ---------------- 登录状态 ---------------- */

function logout(clear = true) {
  if (clear) localStorage.removeItem("cc_token");
  state.token = null; state.uid = null; state.username = null;
  state.devices = []; state.files = []; state.targets.clear();
  if (state.pairTimer) clearInterval(state.pairTimer);
  showAuth();
}

/* ---------------- 工具函数 ---------------- */

function fmtSize(n) {
  if (n == null) return "";
  if (n < 1024) return n + " B";
  if (n < 1048576) return (n / 1024).toFixed(1) + " KB";
  if (n < 1073741824) return (n / 1048576).toFixed(1) + " MB";
  return (n / 1073741824).toFixed(2) + " GB";
}
function fmtTime(ts) {
  if (!ts) return "—";
  const d = new Date(ts);
  const today = new Date();
  const sameDay = d.toDateString() === today.toDateString();
  const hm = String(d.getHours()).padStart(2, "0") + ":" + String(d.getMinutes()).padStart(2, "0");
  return sameDay ? ("今天 " + hm) : ((d.getMonth() + 1) + "/" + d.getDate() + " " + hm);
}
function fileEmoji(name) {
  const ext = (name.split(".").pop() || "").toLowerCase();
  if (["ppt", "pptx", "key"].includes(ext)) return "📽️";
  if (["doc", "docx", "pdf", "txt", "md"].includes(ext)) return "📄";
  if (["xls", "xlsx", "csv"].includes(ext)) return "📊";
  if (["jpg", "jpeg", "png", "gif", "webp", "bmp"].includes(ext)) return "🖼️";
  if (["mp4", "mkv", "avi", "mov", "flv"].includes(ext)) return "🎬";
  if (["mp3", "wav", "flac", "m4a"].includes(ext)) return "🎵";
  if (["zip", "rar", "7z"].includes(ext)) return "🗜️";
  return "📦";
}
function targetsText(t) {
  if (t === "all" || !Array.isArray(t)) return "全部设备";
  if (!t.length) return "—";
  const names = t.map(did => (state.devices.find(d => d.did === did) || {}).name || did);
  return names.join("、");
}

/* ---------------- 版本号（/api/version，不在页面硬编码） ---------------- */

async function refreshAppVersion() {
  const nodes = $$("[data-app-version]");
  if (!nodes.length) return;
  try {
    const r = await fetch("/api/version", { cache: "no-store" }).then((x) => x.json());
    if (!r || !r.version) return;
    const label = "V" + String(r.version).replace(/^v/i, "");
    nodes.forEach((el) => { el.textContent = label; });
  } catch {
    /* 离线时保留占位符，不影响主流程 */
  }
}

/* ---------------- 演示模式（?demo=1，用于预览） ---------------- */

async function mockApi(path, opts) {
  await new Promise(r => setTimeout(r, 250));
  const data = opts.json || {};
  if (path === "/login") return { token: "demo", uid: "demo0001", username: data.username || "wanglaoshi" };
  if (path === "/register") return { token: "demo", uid: "demo0001", username: data.username || "wanglaoshi" };
  if (path === "/devices") return { devices: [
    { did: "a1b2c3", name: "高一（3）班 一体机", online: true, lastSeen: Date.now() - 20000, createdAt: Date.now() - 86400000 * 30 },
    { did: "d4e5f6", name: "高一（4）班 一体机", online: false, lastSeen: Date.now() - 86400000, createdAt: Date.now() - 86400000 * 10 },
  ] };
  if (path === "/devices/pair") return { code: String(Math.floor(100000 + Math.random() * 900000)), expires: Date.now() + 600000 };
  if (path.startsWith("/bind/info")) return { name: "高一（3）班 一体机" };
  if (path === "/bind") return { ok: true, device: { did: "a1b2c3", name: "高一（3）班 一体机" } };
  if (path === "/announce") return { sent: 1, devices: ["a1b2c3"] };
  if (path === "/command") return { sent: 1, devices: ["a1b2c3"] };
  if (path === "/files") return { files: [
    { fid: "f1", name: "第3章 细胞的结构.pptx", size: 24 * 1048576, created: Date.now() - 3600 * 1000, targets: "all" },
    { fid: "f2", name: "月考成绩表.xlsx", size: 340 * 1024, created: Date.now() - 86400 * 1000, targets: "all" },
  ] };
  if (path.startsWith("/files/")) return { ok: true };
  if (path.startsWith("/devices/")) return { ok: true };
  return { ok: true };
}

/* ---------------- 共享导出（window.CC） ---------------- */
Object.assign(CC, {
  $, $$, state, DEMO,
  api, toast, modal,
  showAuth, showApp, applyAvatar, switchTab, refreshBroadcast, logout,
  fmtSize, fmtTime, fileEmoji, targetsText,
  refreshAppVersion, mockApi,
});
