/* ClassCall Lite WebUI — 教师端逻辑 */
"use strict";

const $ = (s) => document.querySelector(s);
const $$ = (s) => Array.from(document.querySelectorAll(s));
const state = {
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

/* ---------------- 扫码绑定 ---------------- */

function stripBindQuery() {
  try { history.replaceState(null, "", "/"); } catch {}
}

async function showBindFlow() {
  const reqId = state.pendingBind;
  if (!reqId) return;
  const body = $("#bind-body");
  const ok = $("#bind-ok");
  ok.disabled = true;
  $("#bind-overlay").hidden = false;
  try {
    const info = await api("/bind/info?req=" + encodeURIComponent(reqId));
    body.innerHTML = `<p style="margin:0 0 4px">发现设备：<b></b></p>
      <p class="muted" style="margin:0">绑定后，这台设备的喊话与文件将发送到你的账号。</p>`;
    body.querySelector("b").textContent = info.name;
    ok.disabled = false;
    ok.onclick = async () => {
      ok.disabled = true;
      try {
        await api("/bind", { method: "POST", json: { reqId } });
        state.pendingBind = null;
        stripBindQuery();
        $("#bind-overlay").hidden = true;
        toast("✅ 绑定成功，设备已上线");
        refreshDevices().catch(() => {});
        // 激活引导：绑定成功即引导发第一条喊话
        try {
          switchTab("announce");
          const ta = $("#an-text");
          if (ta && !ta.value.trim()) {
            ta.value = "同学们好，这是来自 Classify 的第一条喊话 👋";
            ta.dispatchEvent(new Event("input"));
            ta.focus();
          }
          setTimeout(() => toast("🎉 发送第一条喊话试试！"), 800);
        } catch {}
      } catch (ex) {
        body.innerHTML = `<p style="margin:0;color:#b3261e"></p>`;
        body.querySelector("p").textContent = ex.message;
      } finally { ok.disabled = false; }
    };
  } catch (ex) {
    body.innerHTML = `<p style="margin:0;color:#b3261e"></p>`;
    body.querySelector("p").textContent = ex.message;
    $("#bind-cancel").textContent = "关闭";
  }
}

$("#bind-cancel").addEventListener("click", () => {
  state.pendingBind = null;
  stripBindQuery();
  $("#bind-overlay").hidden = true;
  $("#bind-cancel").textContent = "暂不绑定";
});

/* ---------------- API 封装 ---------------- */

const DEMO = new URLSearchParams(location.search).has("demo");

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
  if (!resp.ok) throw new Error(data.message || `请求失败 (${resp.status})`);
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
  if (typeof body === "string") b.innerHTML = `<p style="margin:0">${body}</p>`;
  else { b.innerHTML = ""; b.appendChild(body); }
  const ok = $("#modal-ok");
  ok.textContent = okText;
  ok.className = "btn " + (danger ? "btn-danger" : "btn-primary");
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
  api("/me").then((me) => {
    state.subject = me.subject || "其他";
    state.role = me.role || "user";
    state.avatar = me.avatar || "";
    applyAvatar();
    // 固定问候（无需自定义）
    const banner = $("#welcome-greeting");
    if (banner) banner.textContent = `👋 你好，${state.username}老师`;
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
    el.style.backgroundImage = `url(${state.avatar})`;
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

/* ---------------- 反馈 ---------------- */

$("#fb-send-btn").addEventListener("click", () => {
  const box = document.createElement("div");
  box.innerHTML = `
    <select id="fb-cat" class="cb-select" style="width:100%;margin-bottom:8px">
      <option value="">选择类别（可选）</option>
      <option>功能异常</option><option>体验建议</option><option>连接问题</option>
      <option>看板/EasiCare</option><option>其他</option>
    </select>
    <textarea id="fb-msg" rows="4" maxlength="500" class="area"
      placeholder="请描述遇到的问题或建议（≤500 字）。为保护隐私，请不要填写学生姓名等个人信息。" style="width:100%"></textarea>
    <p class="body-s on-var" style="margin:6px 0 0">复杂问题可邮件 <a href="mailto:support@linkium.top">support@linkium.top</a>，教室电脑端还可附脱敏日志。</p>`;
  modal({
    title: "💬 反馈问题", body: box, okText: "提交",
    onOk: async () => {
      const message = box.querySelector("#fb-msg").value.trim();
      const category = box.querySelector("#fb-cat").value;
      if (!message) return toast("请填写反馈内容", true);
      try {
        await api("/feedback", { method: "POST", json: { message, category } });
        toast("反馈已提交，感谢 🙏");
      } catch (ex) { toast(ex.message, true); }
    },
  });
});

/* ---------------- 开发者治理功能已迁移至 /admin（admin.js） ---------------- */

/* ---------------- 登录 / 注册 / 自助重置 ---------------- */

let authMode = "login";
$$(".auth-tab").forEach(btn => btn.addEventListener("click", () => {
  authMode = btn.dataset.mode;
  $$(".auth-tab").forEach(b => b.classList.toggle("active", b === btn));
  $("#pwd2-row").hidden = authMode === "login";
  const sr = $("#subject-row");
  if (sr) sr.hidden = authMode === "login";
  $("#auth-submit").textContent = authMode === "login" ? "登 录" : "注 册";
  $("#auth-error").hidden = true;
}));

// 忘记密码：展开自助重置（需重置码：注册时展示过，或在绑定教室电脑上生成）
$("#btn-forgot").addEventListener("click", () => {
  const p = $("#reset-panel");
  p.hidden = !p.hidden;
  if (!p.hidden) { $("#btn-forgot").textContent = "收起重置"; }
  else { $("#btn-forgot").textContent = "忘记密码？"; }
});

$("#reset-submit").addEventListener("click", async () => {
  const err = $("#reset-error");
  err.hidden = true;
  const username = $("#r-username").value.trim();
  const code = $("#r-code").value.trim().toUpperCase();
  const newPassword = $("#r-newpass").value;
  if (!username || !code) { err.textContent = "请填写用户名与重置码"; err.hidden = false; return; }
  if (newPassword.length < 8) { err.textContent = "新密码至少 8 位"; err.hidden = false; return; }
  $("#reset-submit").disabled = true;
  try {
    const data = await api("/password/reset", { method: "POST", json: { username, code, newPassword } });
    state.token = data.token;
    state.uid = data.uid;
    state.username = data.username;
    localStorage.setItem("cc_token", data.token);
    toast("密码已重置并登录 ✅");
    showApp();
  } catch (ex) {
    err.textContent = ex.message;
    err.hidden = false;
  } finally {
    $("#reset-submit").disabled = false;
  }
});

$("#auth-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const err = $("#auth-error");
  err.hidden = true;
  const username = $("#f-username").value.trim();
  const password = $("#f-password").value;
  const btn = $("#auth-submit");
  btn.disabled = true;
  try {
    let data;
    const cfToken = window.turnstile ? window.turnstile.getResponse() : "";
    if (state.turnstileSiteKey && !cfToken) throw new Error("请先完成人机验证");
    if (authMode === "register") {
      if (password !== $("#f-password2").value) throw new Error("两次输入的密码不一致");
      data = await api("/register", { method: "POST", json: { username, password, subject: $("#f-subject")?.value || "其他", cfToken } });
    } else {
      data = await api("/login", { method: "POST", json: { username, password, cfToken } });
    }
    state.token = data.token;
    state.uid = data.uid;
    state.username = data.username;
    localStorage.setItem("cc_token", data.token);
    showApp();
    if (data.resetCode) {
      // 重置码仅此一次展示
      const div = document.createElement("div");
      div.innerHTML = `<p style="margin:0 0 8px">请<b>立即抄写并保存</b>你的密码重置码（忘记密码时凭它自助重置，仅展示这一次）：</p>
        <b style="font-size:22px;letter-spacing:3px;display:block;text-align:center;background:var(--surface-c-high);border-radius:8px;padding:10px"></b>
        <p class="body-s on-var" style="margin:8px 0 0">也可在已绑定的教室电脑「设置 → 密码重置码」随时重新生成。</p>`;
      div.querySelector("b:last-of-type").textContent = data.resetCode;
      modal({ title: "🔑 你的密码重置码", body: div, okText: "我已保存", onOk: null });
    }
  } catch (ex) {
    err.textContent = ex.message;
    err.hidden = false;
    if (window.turnstile) window.turnstile.reset();
  } finally {
    btn.disabled = false;
  }
});

// Turnstile：配置了站点密钥才渲染人机验证
(async () => {
  try {
    const cfg = await fetch("/api/config").then((r) => r.json());
    state.turnstileSiteKey = cfg.turnstileSiteKey || "";
  } catch { return; }
  if (!state.turnstileSiteKey) return;
  const s = document.createElement("script");
  s.src = "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit";
  s.async = true;
  s.onload = () => {
    const host = $("#cf-turnstile-box");
    if (!host) return;
    host.hidden = false;
    window.turnstile = window.turnstile || {};
    turnstileRender(host);
  };
  document.head.appendChild(s);
  function turnstileRender(host) {
    if (!window.turnstile || typeof window.turnstile.render !== "function") {
      setTimeout(() => turnstileRender(host), 300);
      return;
    }
    window.turnstile.render(host, { sitekey: state.turnstileSiteKey });
  }
})();

function logout(clear = true) {
  if (clear) localStorage.removeItem("cc_token");
  state.token = null; state.uid = null; state.username = null;
  state.devices = []; state.files = []; state.targets.clear();
  if (state.pairTimer) clearInterval(state.pairTimer);
  showAuth();
}

/* ---------------- 常用语（本机）与喊话历史（云端，最近 20 条） ---------------- */

function getQuick() {
  try { return JSON.parse(localStorage.getItem("cc_quick") || "[]"); } catch { return []; }
}

function renderQuick() {
  const box = $("#an-quick");
  if (!box) return;
  box.innerHTML = "";
  const quick = getQuick();
  if (!quick.length) return;
  for (const q of quick) {
    const c = document.createElement("button");
    c.type = "button";
    c.className = "chip";
    c.innerHTML = `<span style="max-width:140px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"></span><b style="margin-left:6px">✕</b>`;
    c.querySelector("span").textContent = q;
    c.querySelector("span").onclick = () => { $("#an-text").value = q; $("#an-text").dispatchEvent(new Event("input")); };
    c.querySelector("b").onclick = (e) => {
      e.stopPropagation();
      localStorage.setItem("cc_quick", JSON.stringify(getQuick().filter((x) => x !== q)));
      renderQuick();
    };
    box.appendChild(c);
  }
}

$("#an-save-quick").addEventListener("click", () => {
  const t = $("#an-text").value.trim();
  if (!t) return toast("先输入内容再保存为常用语", true);
  if (t.length > 60) return toast("常用语建议 60 字以内", true);
  const list = getQuick().filter((x) => x !== t);
  list.unshift(t);
  localStorage.setItem("cc_quick", JSON.stringify(list.slice(0, 12)));
  renderQuick();
  toast("已存为常用语 ⭐");
});

async function renderHistory() {
  const box = $("#an-history");
  if (!box || DEMO) return;
  box.innerHTML = "";
  let hist = [];
  try { hist = (await api("/announce/history")).history || []; } catch { return; }
  if (!hist.length) return;
  const label = document.createElement("span");
  label.className = "body-s on-var";
  label.textContent = "最近：";
  box.appendChild(label);
  for (const h of hist.slice(0, 6)) {
    const c = document.createElement("button");
    c.type = "button";
    c.className = "chip";
    c.title = "点击填入（可修改后发送）";
    c.innerHTML = `<span style="max-width:130px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"></span>`;
    c.querySelector("span").textContent = h.text;
    c.onclick = () => { $("#an-text").value = h.text; $("#an-text").dispatchEvent(new Event("input")); };
    box.appendChild(c);
  }
}

/* ---------------- 随机点名 / 倒计时预设 ---------------- */

$("#picker-run").addEventListener("click", () => {
  modal({
    title: "🎲 随机点名", body: "将从早读看板名册中随机抽一名学生，大屏滚动后定格并朗读姓名。开始？",
    okText: "开始", onOk: () => surface("picker", {}, "点名进行中 🎲"),
  });
});

/* ---------------- 图片快投 ---------------- */

function castImage(file) {
  if (!/^image\//.test(file.type)) return toast("请选择图片文件", true);
  const hint = $("#cam-cast-hint");
  if (hint) hint.textContent = "上传并投屏中…";
  const targets = state.targets.size ? JSON.stringify(Array.from(state.targets)) : "all";
  const fd = new FormData();
  fd.append("file", file);
  fd.append("name", "投屏-" + file.name);
  fd.append("targets", targets);
  fd.append("nopush", "1");
  const xhr = new XMLHttpRequest();
  xhr.open("POST", "/api/files");
  xhr.setRequestHeader("authorization", "Bearer " + state.token);
  xhr.addEventListener("load", () => {
    let data = {};
    try { data = JSON.parse(xhr.responseText); } catch {}
    if (xhr.status >= 200 && xhr.status < 300) {
      api("/surface/send", { method: "POST", json: { type: "image", payload: { fid: data.fid, name: data.name } } })
        .then(() => { toast("已投到教室大屏 🖼️"); if (hint) hint.textContent = "已投屏 ✓"; refreshFiles().catch(() => {}); })
        .catch((ex) => { toast(ex.message, true); if (hint) hint.textContent = ""; });
    } else {
      toast(data.message || `上传失败 (${xhr.status})`, true);
      if (hint) hint.textContent = "";
    }
  });
  xhr.addEventListener("error", () => { toast("网络错误", true); if (hint) hint.textContent = ""; });
  xhr.send(fd);
}

$("#cam-cast-btn").addEventListener("click", () => $("#cam-cast-input").click());
$("#cam-cast-input").addEventListener("change", (e) => {
  if (e.target.files.length) castImage(e.target.files[0]);
  e.target.value = "";
});

/* ---------------- 倒计时预设 ---------------- */

(() => {
  const box = $("#cd-presets");
  if (!box) return;
  for (const m of [1, 3, 5, 10, 20, 30]) {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "chip";
    b.textContent = m + " 分钟";
    b.onclick = async () => {
      try {
        await api("/surface/send", { method: "POST", json: { type: "clock", payload: { action: "start", minutes: m } } });
        toast(`⏳ ${m} 分钟倒计时已开始`);
      } catch (ex) { toast(ex.message, true); }
    };
    box.appendChild(b);
  }
})();

/* ---------------- 注销账号（自助删除） ---------------- */

$("#btn-delete-account").addEventListener("click", () => {
  const box = document.createElement("div");
  box.innerHTML = `
    <p style="margin:0 0 8px">将<b>永久删除</b>你的账号：全部设备解绑、云端文件清空、看板/课表/操作集等数据一并清除，<b>不可恢复</b>。</p>
    <p class="body-s on-var" style="margin:0 0 8px">请输入用户名 <b id="del-un-label"></b> 确认，并输入密码：</p>
    <input id="del-un" class="cd-input" style="width:100%;margin-bottom:8px" placeholder="输入用户名确认">
    <input id="del-pw" type="password" class="cd-input" style="width:100%" placeholder="登录密码">`;
  box.querySelector("#del-un-label").textContent = state.username;
  modal({
    title: "⚠️ 删除账号", body: box, danger: true, okText: "永久删除",
    onOk: async () => {
      const un = box.querySelector("#del-un").value.trim();
      const pw = box.querySelector("#del-pw").value;
      if (un !== state.username) return toast("用户名不匹配，未删除", true);
      if (!pw) return toast("请输入密码", true);
      try {
        await api("/account/delete", { method: "POST", json: { password: pw } });
        toast("账号已删除，感谢使用 Classify");
        setTimeout(() => logout(), 1200);
      } catch (ex) { toast(ex.message, true); }
    },
  });
});

/* ---------------- 喊话（原有发送逻辑） ---------------- */

function renderTargets() {
  const box = $("#an-targets");
  box.innerHTML = "";
  const mk = (label, isSel, onClick, online = true) => {
    const c = document.createElement("button");
    c.type = "button";
    c.className = "chip" + (isSel ? " sel" : "");
    c.innerHTML = (online ? `<span class="dot ${online ? "on" : ""}"></span>` : "") + label;
    c.onclick = onClick;
    box.appendChild(c);
  };
  mk("🖥️ 全部设备", state.targets.size === 0, () => { state.targets.clear(); renderTargets(); });
  if (!state.devices.length) {
    const p = document.createElement("span");
    p.className = "muted";
    p.textContent = "还没有绑定设备，请到「设备」页配对";
    box.appendChild(p);
  }
  for (const d of state.devices) {
    mk(`${d.name}${d.online ? "" : "（离线）"}`, state.targets.has(d.did), () => {
      if (state.targets.has(d.did)) state.targets.delete(d.did); else state.targets.add(d.did);
      renderTargets();
    }, d.online);
  }
}

$("#an-text").addEventListener("input", () => {
  $("#an-count").textContent = `${$("#an-text").value.length} / 2000`;
});

$("#an-send").addEventListener("click", async () => {
  const text = $("#an-text").value.trim();
  if (!text) return toast("请输入喊话内容", true);
  const targets = state.targets.size ? Array.from(state.targets) : undefined;
  $("#an-send").disabled = true;
  $("#an-hint").textContent = "发送中…";
  try {
    const r = await api("/announce", { method: "POST", json: { text, speak: $("#an-speak").checked, targets } });
    $("#an-text").value = "";
    $("#an-count").textContent = "0 / 2000";
    const total = r.sent || 0;
    $("#an-hint").textContent = `✅ 已发送到 ${total} 台设备`;
    toast("喊话已发送 📢");
    if (r.mid && total > 0) pollReceipts(r.mid, total); // 送达/显示回执
    renderHistory().catch(() => {});
  } catch (ex) {
    $("#an-hint").textContent = "";
    toast(ex.message, true);
  } finally {
    $("#an-send").disabled = false;
  }
});

// 送达回执轮询：设备上报 received/displayed，实时更新在发送按钮下方
async function pollReceipts(mid, total) {
  for (let i = 0; i < 8; i++) {
    await new Promise((s) => setTimeout(s, 2500));
    try {
      const r = await api(`/announce/receipts?mid=${encodeURIComponent(mid)}`);
      const received = r.receipts.filter((x) => x.status !== "failed").length;
      const displayed = r.receipts.filter((x) => x.status === "displayed").length;
      $("#an-hint").textContent = `📡 已送达 ${received}/${total} · 已显示 ${displayed}/${total}`;
      if (received >= total && displayed >= total) return;
    } catch { return; }
  }
}

/* ---------------- 文件 ---------------- */

const dz = $("#file-drop");
dz.addEventListener("click", () => $("#file-input").click());
dz.addEventListener("dragover", (e) => { e.preventDefault(); dz.classList.add("over"); });
dz.addEventListener("dragleave", () => dz.classList.remove("over"));
dz.addEventListener("drop", (e) => {
  e.preventDefault();
  dz.classList.remove("over");
  if (e.dataTransfer.files.length) uploadFile(e.dataTransfer.files[0]);
});
$("#file-input").addEventListener("change", (e) => {
  if (e.target.files.length) uploadFile(e.target.files[0]);
  e.target.value = "";
});

function uploadFile(file) {
  const box = $("#up-box"), bar = $("#up-bar"), label = $("#up-label");
  box.hidden = false; bar.style.width = "0%";
  const finish = (msg, isErr) => {
    label.textContent = msg;
    setTimeout(() => { box.hidden = true; }, isErr ? 4000 : 1200);
    if (!isErr) { toast("文件已发送 🗂️"); refreshFiles().catch(() => {}); }
    else toast(msg, true);
  };
  if (DEMO) {
    let p = 0;
    const t = setInterval(() => {
      p += 12; bar.style.width = Math.min(p, 100) + "%";
      label.textContent = `上传中 ${Math.min(p, 100)}%（演示）`;
      if (p >= 100) { clearInterval(t); finish("演示模式：上传完成", false); state.files.unshift({ fid: "d" + Date.now(), name: file.name, size: file.size, created: Date.now(), targets: "all" }); renderFiles(); }
    }, 140);
    return;
  }
  const fd = new FormData();
  fd.append("file", file);
  fd.append("name", file.name); // workerd 会把非 ASCII filename 换成 ?，真实名走独立字段
  const targets = state.targets.size ? JSON.stringify(Array.from(state.targets)) : "all";
  fd.append("targets", targets);
  const xhr = new XMLHttpRequest();
  xhr.open("POST", "/api/files");
  xhr.setRequestHeader("authorization", "Bearer " + state.token);
  xhr.upload.addEventListener("progress", (e) => {
    if (e.lengthComputable) {
      const pct = Math.round((e.loaded / e.total) * 100);
      bar.style.width = pct + "%";
      label.textContent = `上传中 ${pct}%  ·  ${fmtSize(e.loaded)} / ${fmtSize(e.total)}`;
    }
  });
  xhr.addEventListener("load", () => {
    let data = {};
    try { data = JSON.parse(xhr.responseText); } catch {}
    if (xhr.status >= 200 && xhr.status < 300) finish(`✅ 已发送到 ${data.sent || 0} 台设备`, false);
    else finish(data.message || `上传失败 (${xhr.status})`, true);
  });
  xhr.addEventListener("error", () => finish("网络错误，上传失败", true));
  xhr.send(fd);
}

async function refreshFiles() {
  const r = await api("/files");
  state.files = r.files || [];
  renderFiles();
}

function renderFiles() {
  const list = $("#file-list");
  list.innerHTML = "";
  if (!state.files.length) {
    list.innerHTML = '<p class="muted center">还没有传过文件</p>';
    return;
  }
  for (const f of state.files) {
    const div = document.createElement("div");
    div.className = "list-item";
    const isImg = /\.(jpe?g|png|gif|webp|bmp)$/i.test(f.name);
    const viewBtn = isImg
      ? `<button class="btn btn-tonal btn-sm" style="margin-right:6px" data-view="${f.fid}">查看</button>
         <button class="btn btn-tonal btn-sm" style="margin-right:6px" data-cast="${f.fid}">投屏</button>` : "";
    const pinLabel = f.pinned ? "📌 已置顶" : "📌 置顶";
    div.innerHTML = `<span class="ico">${f.pinned ? "📌" : fileEmoji(f.name)}</span>
      <div class="meta"><b></b><span>${fmtSize(f.size)} · ${fmtTime(f.created)} · ${targetsText(f.targets)}</span></div>
      ${viewBtn}<button class="btn btn-tonal btn-sm" style="margin-right:6px" data-resend="${f.fid}">重新推送</button>
      <button class="btn btn-tonal btn-sm" style="margin-right:6px" data-pin="${f.fid}">${pinLabel}</button>
      <button class="act" title="删除">🗑</button>`;
    div.querySelector("b").textContent = f.name;
    const vb = div.querySelector("[data-view]");
    if (vb) vb.onclick = () => viewFile(f);
    const castBtn = div.querySelector("[data-cast]");
    if (castBtn) castBtn.onclick = () => {
      surface("image", { fid: f.fid, name: f.name }, "已投到教室大屏 🖼️");
    };
    div.querySelector("[data-resend]").onclick = async () => {
      try {
        const r = await api(`/files/${f.fid}/resend`, { method: "POST", json: {} });
        toast(`已重新推送到 ${r.sent} 台设备 🗂️`);
      } catch (ex) { toast(ex.message, true); }
    };
    div.querySelector("[data-pin]").onclick = async () => {
      try {
        await api(`/files/${f.fid}/pin`, { method: "POST", json: { pinned: !f.pinned } });
        toast(f.pinned ? "已取消置顶" : "已置顶（不会被 7 天自动清理）📌");
        refreshFiles().catch(() => {});
      } catch (ex) { toast(ex.message, true); }
    };
    div.querySelector(".act").onclick = () => {
      modal({
        title: "删除文件", body: `确定删除「${f.name}」吗？已下载到桌面的副本不受影响。`,
        danger: true, okText: "删除",
        onOk: async () => { try { await api("/files/" + f.fid, { method: "DELETE" }); refreshFiles().catch(() => {}); } catch (ex) { toast(ex.message, true); } },
      });
    };
    list.appendChild(div);
  }
}

// 应用内查看图片：带鉴权头取 blob（新标签直接开接口 URL 会被 Service Worker 回退到首页且无鉴权）
async function viewFile(f) {
  try {
    const resp = await fetch(`/api/files/${f.fid}/content`, {
      headers: { authorization: "Bearer " + state.token },
    });
    if (!resp.ok) throw new Error(`加载失败 (${resp.status})`);
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    const img = document.createElement("img");
    img.src = url;
    img.style.cssText = "max-width:100%;max-height:68vh;border-radius:12px;display:block;margin:0 auto";
    modal({
      title: f.name, body: img, okText: "新标签打开",
      onOk: () => window.open(url, "_blank"),
    });
    setTimeout(() => URL.revokeObjectURL(url), 5 * 60 * 1000);
  } catch (ex) { toast(ex.message, true); }
}

// 任教学科注册时已填写、此后不可更改（设置页仅展示）
const stSubject = $("#st-subject");
if (stSubject) stSubject.disabled = true;

/* 头像（客户端缩放到 192px JPEG 再上传） */
$("#st-avatar-btn").addEventListener("click", () => $("#st-avatar-input").click());
$("#st-avatar-input").addEventListener("change", async (e) => {
  const file = e.target.files[0];
  e.target.value = "";
  if (!file) return;
  if (!/^image\//.test(file.type)) return toast("请选择图片文件", true);
  try {
    const dataUrl = await downscaleImage(file, 192, 0.85);
    if (dataUrl.length > 60000) return toast("图片压缩后仍过大，请换一张更简单的图", true);
    await api("/me/avatar", { method: "POST", json: { avatar: dataUrl } });
    state.avatar = dataUrl;
    applyAvatar();
    toast("头像已更新 ✅");
  } catch (ex) { toast("头像上传失败：" + ex.message, true); }
});
$("#st-avatar-clear").addEventListener("click", async () => {
  try {
    await api("/me/avatar", { method: "POST", json: { avatar: "" } });
    state.avatar = "";
    applyAvatar();
    toast("已恢复默认头像");
  } catch (ex) { toast(ex.message, true); }
});

function downscaleImage(file, maxSide, quality) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error("读取失败"));
    reader.onload = () => {
      const img = new Image();
      img.onerror = () => reject(new Error("图片解析失败"));
      img.onload = () => {
        const scale = Math.min(1, maxSide / Math.max(img.width, img.height));
        const canvas = document.createElement("canvas");
        canvas.width = Math.max(1, Math.round(img.width * scale));
        canvas.height = Math.max(1, Math.round(img.height * scale));
        canvas.getContext("2d").drawImage(img, 0, 0, canvas.width, canvas.height);
        resolve(canvas.toDataURL("image/jpeg", quality));
      };
      img.src = reader.result;
    };
    reader.readAsDataURL(file);
  });
}

/* ---------------- 智能候课（课程表 + 操作集） ---------------- */

async function loadPreclass() {
  try {
    const r = await api("/timetable");
    renderTimetable(r.timetable);
    const note = $("#tt-updatedby");
    if (note) note.textContent = r.updatedBy ? `当前课表由「${r.updatedBy}」更新（多教师共享，后保存覆盖）` : "";
  } catch { renderTimetable(null); }
  try {
    const a = await api("/actions");
    renderActions(a.actions || []);
  } catch { renderActions(null); }
}

// 课程表编辑器：每节课一张卡片（起止时间 + 周一~日学科），手机/桌面同一套布局
const tt = { showSun: false, periods: [] };

function renderTimetable(info) {
  const t = info && typeof info === "object" ? info : null;
  tt.showSun = Array.isArray(t?.lessons) && t.lessons.some((l) => l.day === 7);
  tt.periods = (Array.isArray(t?.periods) && t.periods.length
    ? t.periods
    : Array.from({ length: 6 }, (_, i) => ({ p: i + 1, start: ["08:00","08:55","10:00","10:55","14:00","14:55"][i], end: "" }))
  ).slice(0, 12).map((p, i) => ({ p: p.p || i + 1, start: p.start || "08:00", end: p.end || "" }));
  tt._saved = t?.lessons || [];
  $("#preclass-minutes").value = t?.preclassMinutes || 5;
  renderTtGrid();
}

function renderTtGrid() {
  const grid = $("#tt-grid");
  grid.innerHTML = "";
  const saved = {};
  for (const l of tt._saved) saved[`${l.day}:${l.period}`] = l.subject;

  const dayCount = tt.showSun ? 7 : 6;
  const dayNames = ["一", "二", "三", "四", "五", "六", "日"];

  for (const period of tt.periods) {
    const p = period.p;
    const card = document.createElement("div");
    card.className = "tt-card";
    card.dataset.period = p;

    const head = document.createElement("div");
    head.className = "tt-card-head";
    head.innerHTML = `<b class="tt-card-name">第 ${p} 节</b>
      <label class="tt-time">起<input class="tt-start" type="time" value="${period.start}"></label>
      <label class="tt-time">止<input class="tt-end" type="time" value="${period.end}" placeholder="自动"></label>
      <button class="btn btn-outline-danger btn-sm tt-del" title="删除此节">✕</button>`;
    card.appendChild(head);

    const daysRow = document.createElement("div");
    daysRow.className = "tt-days";
    for (let day = 1; day <= dayCount; day++) {
      const val = saved[`${day}:${p}`] || "";
      daysRow.insertAdjacentHTML("beforeend",
        `<label class="tt-day"><span>${dayNames[day - 1]}</span><input data-day="${day}" data-period="${p}" value="${val}" placeholder="·"></label>`);
    }
    card.appendChild(daysRow);
    grid.appendChild(card);
  }

  const delBtns = grid.querySelectorAll(".tt-del");
  delBtns.forEach((btn) => {
    btn.onclick = () => {
      const period = +btn.closest(".tt-card").dataset.period;
      tt.periods = tt.periods.filter((x) => x.p !== period);
      renderTtGrid();
    };
  });

  const ops = $("#tt-ops");
  ops.innerHTML = "";
  const mkOp = (label, fn) => {
    const b = document.createElement("button");
    b.className = "btn btn-tonal btn-sm"; b.textContent = label; b.onclick = fn;
    ops.appendChild(b);
  };
  const maxP = tt.periods.length ? Math.max(...tt.periods.map((x) => x.p)) : 0;
  mkOp("＋ 增加一节", () => {
    if (tt.periods.length >= 12) return toast("最多 12 节", true);
    tt.periods.push({ p: maxP + 1, start: "08:00", end: "" });
    renderTtGrid();
  });
  mkOp(tt.showSun ? "隐藏周日" : "显示周日", () => { tt.showSun = !tt.showSun; renderTtGrid(); });
}

$("#tt-save").addEventListener("click", async () => {
  const periods = [];
  const lessons = [];
  document.querySelectorAll("#tt-grid .tt-card").forEach((card) => {
    const p = +card.dataset.period;
    const start = card.querySelector(".tt-start").value || "08:00";
    const end = card.querySelector(".tt-end").value || "";
    periods.push({ p, start, end });
    card.querySelectorAll("input[data-day]").forEach((inp) => {
      const v = inp.value.trim();
      if (v) lessons.push({ day: +inp.dataset.day, period: p, subject: v });
    });
  });
  if (!periods.length) return toast("至少保留一节课", true);
  try {
    const r = await api("/timetable", { method: "POST", json: {
      preclassMinutes: parseInt($("#preclass-minutes").value, 10) || 5,
      lessons,
      periods,
    }});
    toast(`课程表已保存（${periods.length} 节 · 应用到 ${r.devices} 台设备）✅`);
  } catch (ex) { toast(ex.message, true); }
});

$("#tt-paste").addEventListener("click", async () => {
  try {
    const text = await navigator.clipboard.readText();
    const rows = text.trim().split(/\r?\n/).map((r) => r.split(/[\t,，]/));
    if (rows.length < 1) return toast("剪贴板没有表格内容", true);
    const cards = document.querySelectorAll("#tt-grid .tt-card");
    const dayCount = tt.showSun ? 7 : 6;
    cards.forEach((card, r) => {
      const inputs = card.querySelectorAll("input[data-day]");
      inputs.forEach((inp, c) => {
        if (rows[r] && rows[r][c] !== undefined) inp.value = rows[r][c].trim();
      });
    });
    toast("已从剪贴板粘贴（Excel 复制后按行对应各节）");
  } catch { toast("无法读取剪贴板", true); }
});

function renderActions(actions) {
  const list = $("#actions-list");
  list.innerHTML = "";
  for (const a of actions || []) list.appendChild(actionRow(a.type, a.param));
  if (!actions || actions.length === 0) list.appendChild(actionRow("clock", "10"));
}

function actionRow(type = "clock", param = "") {
  const row = document.createElement("div");
  row.className = "row";
  row.style.marginBottom = "6px";
  const sel = document.createElement("select");
  sel.className = "cb-select";
  const ACTION_TYPES = {
    clock: "⏰ 全屏时钟", tts: "🗣 语音播报", open: "📂 打开文件/网址",
    volume: "🔊 音量", board: "📖 打开早读看板", delay: "⏳ 延迟等待",
  };
  const PLACEHOLDERS = {
    clock: "分钟数，如 5（留空 = 纯时钟）", tts: "要朗读的文字，如：请翻开课本第 12 页",
    open: "文件路径或网址", volume: "0-100", board: "（无需参数）", delay: "等待秒数，如 10",
  };
  for (const [t, label] of Object.entries(ACTION_TYPES)) {
    const o = document.createElement("option");
    o.value = t; o.textContent = label;
    if (t === type) o.selected = true;
    sel.appendChild(o);
  }
  const inp = document.createElement("input");
  inp.className = "cd-input"; inp.style.flex = "1";
  inp.value = param;
  const syncHint = () => {
    inp.placeholder = PLACEHOLDERS[sel.value] || "参数";
    inp.disabled = sel.value === "board";
  };
  sel.addEventListener("change", syncHint);
  syncHint();
  const del = document.createElement("button");
  del.className = "btn btn-outline-danger btn-sm"; del.textContent = "删除";
  del.addEventListener("click", () => row.remove());
  row.append(sel, inp, del);
  return row;
}

$("#action-add").addEventListener("click", () => $("#actions-list").appendChild(actionRow()));

$("#actions-save").addEventListener("click", async () => {
  const actions = Array.from(document.querySelectorAll("#actions-list .row")).map((row) => ({
    type: row.querySelector("select").value,
    param: row.querySelector("input").value.trim(),
  })).filter((a) => a.type === "board" || a.param !== "");
  try {
    await api("/actions", { method: "POST", json: { actions } });
    toast(`候课操作集已保存（${actions.length} 项）✅`);
  } catch (ex) { toast(ex.message, true); }
});

$("#preclass-run").addEventListener("click", async () => {
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    await api("/surface/send", { method: "POST", json: { type: "preclass", payload: { action: "run-now" }, targets } });
    toast("已触发立即候课 ✅");
  } catch (ex) { toast(ex.message, true); }
});

// 指定学科候课：管理员可指定任意学科（设备按该学科教师操作集执行）
$("#preclass-override").addEventListener("click", async () => {
  const subject = $("#preclass-subject").value;
  if (!subject) return toast("请选择学科", true);
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    await api("/surface/send", { method: "POST", json: { type: "preclass", payload: { action: "override-now", subject }, targets } });
    toast(`已触发「${subject}」候课 ✅`);
  } catch (ex) { toast(ex.message, true); }
});

/* ---------------- 早读智能看板 ---------------- */

let rbPollTimer = null;

/** 看板成员显示名：新快照只含座号（姓名不出教室电脑），别名对照表存本机 localStorage */
function rbMemberLabel(g, m) {
  if (m.name) return m.name; // 兼容旧设备快照
  const key = g.name + ":" + m.seat;
  try {
    const alias = (JSON.parse(localStorage.getItem("cc_alias") || "{}"))[key];
    if (alias) return alias;
  } catch {}
  return `${g.name}·${m.seat}号`;
}

function renderRbGroups(state2) {
  const box = $("#rb-groups");
  box.innerHTML = "";
  if (!state2 || !state2.groups || !state2.groups.length) {
    box.innerHTML = '<span class="body-s on-var">开启看板后此处显示小组与任务单</span>';
    return;
  }
  const mode = state2.mode || "slash";

  // 任务单（可提前设置；开始即按任务时长倒计时）
  const taskBox = document.createElement("div");
  taskBox.className = "rb-group";
  taskBox.innerHTML = `<b class="body-m">📋 任务单</b>`;
  const tasks = state2.tasks || [];
  if (!tasks.length) {
    const p = document.createElement("p");
    p.className = "body-s on-var";
    p.style.margin = "4px 0 0";
    p.textContent = "暂无任务，用上方「新增任务」提前设置";
    taskBox.appendChild(p);
  }
  tasks.forEach((t, i) => {
    const row = document.createElement("div");
    row.className = "rb-head";
    const label = document.createElement("span");
    label.style.flex = "1";
    label.className = "body-m";
    label.textContent = `${i + 1}. ${t.name}${t.minutes ? `（${t.minutes} 分钟）` : ""}${i === state2.activeTask ? " ▶ 进行中" : ""}`;
    row.appendChild(label);
    const running = i === state2.activeTask && state2.timerRunning;
    const start = document.createElement("button");
    start.className = "btn btn-tonal btn-sm";
    start.textContent = running ? "⏹ 结束" : "▶ 开始";
    start.onclick = () => surface("readboard", {
      action: running ? "stoptask" : "starttask", index: i,
    }, `任务「${t.name}」${running ? "已结束" : "已开始"}`);
    const del = document.createElement("button");
    del.className = "btn btn-outline-danger btn-sm";
    del.textContent = "✕";
    del.title = "删除任务";
    del.onclick = () => surface("readboard", { action: "removetask", index: i }, "任务已删除");
    row.appendChild(start);
    row.appendChild(del);
    taskBox.appendChild(row);
  });
  box.appendChild(taskBox);

  for (const g of state2.groups) {
    const div = document.createElement("div");
    div.className = "rb-group";
    const head = document.createElement("div");
    head.className = "rb-head";
    head.innerHTML = `<b>${g.name}</b><span class="rb-slash">${g.members.length} 人</span>` +
      (mode === "slash"
        ? `<button class="btn btn-tonal btn-sm" data-act="addslash" data-g="${g.name}">🎲 随机＋斜杠</button>`
        : "") +
      `<button class="btn btn-outline-danger btn-sm" data-act="addflower" data-g="${g.name}">🌸 随机＋红花</button>` +
      `<button class="btn btn-tonal btn-sm" data-rbaddmember="${g.name}">＋ 成员</button>`;

    // 名册管理：添加成员（姓名一次性投递到教室电脑，不落云端存储）
    head.querySelector("[data-rbaddmember]").addEventListener("click", () => {
      const inp = document.createElement("input");
      inp.className = "cd-input"; inp.style.width = "100%";
      modal({
        title: `＋ 向「${g.name}」添加成员`,
        body: "姓名将随指令一次性投递到教室电脑（送达即从云端消息中删除，不在云端存储）：",
        okText: "添加",
        onOk: async () => {
          const name = (inp.value || "").trim();
          if (!name) return toast("请输入姓名", true);
          try {
            await api("/surface/send", { method: "POST", json: {
              type: "readboard", payload: { action: "addmember", group: g.name, name },
            }});
            toast(`已添加 ${name} → ${g.name}`);
            setTimeout(() => refreshRbState().catch(() => {}), 1500);
          } catch (ex) { toast(ex.message, true); }
        },
      });
      $("#modal-body").appendChild(inp);
    });

    head.querySelectorAll("button[data-act]").forEach((btn) => {
      btn.addEventListener("click", () => surface("readboard", {
        action: btn.dataset.act, group: btn.dataset.g,
      }, `${btn.dataset.act === "addslash" ? "斜杠" : "小红花"}已派给 ${g.name}`));
    });
    div.appendChild(head);

    // 每生一行（新快照为座号；点 ✎ 可设置本机别名，不上传）
    const members = document.createElement("div");
    members.className = "rb-members";
    for (const m of g.members || []) {
      const row = document.createElement("div");
      row.className = "rb-member";
      const mk = (label, act, cls) => {
        const b = document.createElement("button");
        b.className = "btn btn-sm " + (cls || "btn-tonal");
        b.textContent = label;
        b.onclick = () => surface("readboard", {
          action: act, group: g.name, ...(m.name ? { name: m.name } : { seat: m.seat }),
        }, `${label} → ${rbMemberLabel(g, m)}`);
        return b;
      };
      const name = document.createElement("span");
      name.className = "rb-name";
      name.textContent = rbMemberLabel(g, m);
      const flower = document.createElement("span");
      flower.className = "rb-count";
      flower.textContent = `🌸×${m.flowers || 0}`;
      row.append(name, flower);
      // 红花模式：教师可按人加减红花；斜杠模式红花为自动派生仅展示
      if (mode === "flower") {
        row.appendChild(mk("🌸＋", "addflower"));
        row.appendChild(mk("🌸－", "removeflower", "btn-outline-danger"));
      }
      // ✕ 移出小组（座号定向，姓名不出电脑）
      const rm = document.createElement("button");
      rm.className = "btn btn-outline-danger btn-sm";
      rm.textContent = "✕";
      rm.title = "移出小组";
      rm.onclick = () => surface("readboard", {
        action: "removemember", group: g.name, ...(m.name ? { name: m.name } : { seat: m.seat }),
      }, `已移出 ${rbMemberLabel(g, m)}`);
      row.appendChild(rm);
      if (!m.name) {
        const aliasBtn = document.createElement("button");
        aliasBtn.className = "btn btn-text btn-sm";
        aliasBtn.textContent = "✎";
        aliasBtn.title = "设置本机别名（不上传）";
        aliasBtn.onclick = () => {
          const key = g.name + ":" + m.seat;
          const inp = document.createElement("input");
          inp.className = "cd-input"; inp.style.width = "100%";
          try { inp.value = (JSON.parse(localStorage.getItem("cc_alias") || "{}"))[key] || ""; } catch {}
          modal({
            title: "✎ 本机别名",
            body: `为「${g.name}·${m.seat}号」设置只显示在你手机上的别名（保存在本机，不上传云端）：`,
            okText: "保存",
            onOk: () => {
              let map = {};
              try { map = JSON.parse(localStorage.getItem("cc_alias") || "{}"); } catch {}
              if (inp.value.trim()) map[key] = inp.value.trim(); else delete map[key];
              localStorage.setItem("cc_alias", JSON.stringify(map));
              refreshRbState().catch(() => {});
            },
          });
          $("#modal-body").appendChild(inp);
        };
        row.appendChild(aliasBtn);
      }
      members.appendChild(row);
    }
    div.appendChild(members);
    box.appendChild(div);
  }
}

async function refreshRbState() {
  try {
    const [r, cfg] = await Promise.all([
      api("/readboard/state"),
      api("/readboard/config").catch(() => ({ config: { mode: "slash" } })),
    ]);
    if (r.state) r.state.mode = cfg.config?.mode || "slash";
    renderRbGroups(r.state);
    if (r.state?.easicare === false) $("#rb-eval-hint").textContent = "⚠️ 教室电脑未检测到 EasiCareHelper，评价暂不可用";
    else $("#rb-eval-hint").textContent = "评价将实时写入 EasiCare ✓（学生姓名仅保存在教室电脑，云端只有座号）";
  } catch (ex) { toast(ex.message, true); }
}

/* ---------------- 按人数自动分组 / 任务单 ---------------- */

$("#ag-run").addEventListener("click", async () => {
  const size = parseInt($("#ag-size").value, 10);
  if (!(size >= 2)) return toast("每组人数至少 2", true);
  try {
    await api("/surface/send", { method: "POST", json: {
      type: "readboard", payload: { action: "autogroup", size },
    }});
    toast(`已按每组 ${size} 人重新分组`);
    setTimeout(() => refreshRbState().catch(() => {}), 1500);
  } catch (ex) { toast(ex.message, true); }
});

$("#task-add").addEventListener("click", async () => {
  const name = $("#task-name").value.trim();
  const minutes = parseInt($("#task-minutes").value, 10) || 0;
  if (!name) return toast("请填写任务内容", true);
  try {
    await api("/surface/send", { method: "POST", json: {
      type: "readboard", payload: { action: "addtask", name, minutes },
    }});
    $("#task-name").value = "";
    toast("任务已加入任务单 📋");
    setTimeout(() => refreshRbState().catch(() => {}), 1500);
  } catch (ex) { toast(ex.message, true); }
});

$("#rb-open").addEventListener("click", () => surface("readboard", { action: "open" }, "看板已开启 📖"));
$("#rb-close").addEventListener("click", () => surface("readboard", { action: "close" }, "看板已关闭"));
$("#rb-refresh").addEventListener("click", () => refreshRbState().catch(() => {}));

/* 看板账号级配置：加分模式 + 换算比例 */
async function loadRbConfig() {
  try {
    const r = await api("/readboard/config");
    $("#rb-mode").value = r.config?.mode || "slash";
    $("#rb-ratio").value = r.config?.ratio || 3;
  } catch { /* 默认值即可 */ }
}
$("#rb-config-save").addEventListener("click", async () => {
  try {
    const r = await api("/readboard/config", { method: "POST", json: {
      mode: $("#rb-mode").value,
      ratio: parseInt($("#rb-ratio").value, 10) || 3,
    }});
    toast(`看板规则已保存：${r.config.mode === "flower" ? "直接加花" : "斜杠派花"}，每 ${r.config.ratio} 条斜杠 = 1 朵 🌸`);
  } catch (ex) { toast(ex.message, true); }
});

$("#screen-capture").addEventListener("click", async () => {
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    await api("/surface/send", { method: "POST", json: { type: "screenshot", payload: {}, targets } });
    toast("已触发截屏回传 🖥️");
  } catch (ex) { toast(ex.message, true); }
});

/* ---------------- 课堂时钟 / 常驻通知 / 拍照 ---------------- */

async function surface(type, payload, okText) {
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    const r = await api("/surface/send", { method: "POST", json: { type, payload, targets } });
    toast(okText + `（${r.sent} 台设备）`);
  } catch (ex) { toast(ex.message, true); }
}

$("#clock-open").addEventListener("click", () => surface("clock", { action: "start" }, "时钟已打开 ⏰"));
$("#clock-stop").addEventListener("click", () => surface("clock", { action: "stop" }, "时钟已关闭"));
$("#cd-start").addEventListener("click", () => {
  const m = parseInt($("#cd-minutes").value, 10);
  if (!(m > 0)) return toast("请输入有效分钟数", true);
  surface("clock", { action: "start", minutes: m }, `倒计时 ${m} 分钟已开始 ⏳`);
});

$("#notice-send").addEventListener("click", async () => {
  const text = $("#notice-text").value.trim();
  if (!text) return toast("请输入常驻通知内容", true);
  $("#notice-send").disabled = true;
  try {
    await api("/notice", { method: "POST", json: { text } });
    $("#notice-text").value = "";
    toast("常驻通知已发布 📌（显示在灵动岛）");
  } catch (ex) { toast(ex.message, true); }
  finally { $("#notice-send").disabled = false; }
});

$("#notice-clear").addEventListener("click", async () => {
  try {
    await api("/notice", { method: "DELETE" });
    toast("已清除我的常驻通知");
  } catch (ex) { toast(ex.message, true); }
});

$("#cam-capture").addEventListener("click", async () => {
  $("#cam-capture").disabled = true;
  $("#cam-hint").textContent = "拍照中，请稍候…";
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    await api("/surface/send", { method: "POST", json: { type: "camera", payload: {}, targets } });
    $("#cam-hint").textContent = "已触发，照片稍后出现在下方文件列表 📷";
  } catch (ex) { $("#cam-hint").textContent = ""; toast(ex.message, true); }
  finally {
    setTimeout(() => { $("#cam-capture").disabled = false; $("#cam-hint").textContent = ""; refreshFiles().catch(() => {}); }, 12000);
  }
});

/* ---------------- 设备 ---------------- */

async function refreshDevices() {
  const r = await api("/devices");
  state.devices = r.devices || [];
  renderTargets();
  renderDevices();
  renderSysDevices();
}

function renderDevices() {
  const list = $("#dev-list");
  list.innerHTML = "";
  if (!state.devices.length) {
    list.innerHTML = '<p class="muted center">还没有绑定设备</p>';
    return;
  }
  for (const d of state.devices) {
    const div = document.createElement("div");
    div.className = "list-item";
    div.innerHTML = `<span class="ico">🖥️</span>
      <div class="meta"><b></b><span><span class="dot ${d.online ? "on" : ""}"></span> ${d.online ? "在线" : "离线"} · ID ${d.did} · 最近活跃 ${fmtTime(d.lastSeen)}</span></div>
      <button class="act">解绑</button>`;
    div.querySelector("b").textContent = d.name;
    div.querySelector(".act").onclick = () => {
      modal({
        title: "解绑设备", body: `确定解绑「${d.name}」吗？该设备需要重新配对才能使用。`,
        danger: true, okText: "解绑",
        onOk: async () => { try { await api("/devices/" + d.did, { method: "DELETE" }); refreshDevices().catch(() => {}); } catch (ex) { toast(ex.message, true); } },
      });
    };
    list.appendChild(div);
  }
}

$("#pair-btn").addEventListener("click", async () => {
  try {
    const r = await api("/devices/pair", { method: "POST" });
    $("#pair-card").hidden = false;
    $("#pair-code").textContent = r.code;
    const deadline = r.expires;
    if (state.pairTimer) clearInterval(state.pairTimer);
    state.pairTimer = setInterval(() => {
      const left = deadline - Date.now();
      if (left <= 0) {
        clearInterval(state.pairTimer);
        $("#pair-card").hidden = true;
        toast("配对码已过期", true);
        return;
      }
      const m = Math.floor(left / 60000), s = Math.floor((left % 60000) / 1000);
      $("#pair-timer").textContent = `${m}:${String(s).padStart(2, "0")} 后过期`;
    }, 500);
    $("#pair-timer").textContent = "10:00 后过期";
  } catch (ex) {
    toast(ex.message, true);
  }
});

/* ---------------- 系统控制 ---------------- */

function renderSysDevices() {
  const sel = $("#sys-devices");
  sel.innerHTML = "";
  const optAll = document.createElement("option");
  optAll.value = "all";
  optAll.textContent = "全部设备";
  sel.appendChild(optAll);
  for (const d of state.devices) {
    const o = document.createElement("option");
    o.value = d.did;
    o.textContent = `${d.name}${d.online ? "" : "（离线）"}`;
    sel.appendChild(o);
  }
  updateCapLocks();
}

/** 能力锁态：设备端未开启的功能在手机端置灰并说明（数据来自设备心跳上报的 caps） */
function updateCapLocks() {
  const sel = $("#sys-devices");
  if (!sel) return;
  const did = sel.value !== "all" ? sel.value : null;
  const d = did ? state.devices.find((x) => x.did === did) : null;
  const powerOff = d && d.caps && d.caps.power === false;
  for (const id of ["#btn-reboot", "#btn-shutdown"]) {
    const b = $(id);
    if (!b) continue;
    b.disabled = powerOff;
    b.title = powerOff ? "该设备未开启远程关机/重启（教室电脑 设置 → 权限与隐私）" : "";
  }
}
$("#sys-devices").addEventListener("change", updateCapLocks);

$("#vol").addEventListener("input", () => ($("#vol-val").textContent = $("#vol").value + "%"));

async function sendCommand(name, value) {
  const t = $("#sys-devices").value;
  const targets = t === "all" ? undefined : [t];
  const r = await api("/command", { method: "POST", json: { name, value, targets } });
  toast(`指令已发送（${r.sent} 台设备）`);
}

$("#btn-vol").addEventListener("click", () => sendCommand("set_volume", parseInt($("#vol").value, 10)).catch(e => toast(e.message, true)));
$("#btn-mute").addEventListener("click", () => sendCommand("mute", true).catch(e => toast(e.message, true)));
$("#btn-lock").addEventListener("click", () => sendCommand("lock").catch(e => toast(e.message, true)));
$("#btn-screen").addEventListener("click", () => sendCommand("screen_off").catch(e => toast(e.message, true)));
const confirmCmd = (btn, title, name) =>
  btn.addEventListener("click", () =>
    modal({ title, body: `确定要对所选设备执行「${title}」吗？请确认教室内无正在进行的授课。`, danger: true, okText: title, onOk: () => sendCommand(name).catch(e => toast(e.message, true)) }));
confirmCmd($("#btn-reboot"), "重启电脑", "restart");
confirmCmd($("#btn-shutdown"), "关机", "shutdown");

/* ---------------- 设置 / PWA ---------------- */

$("#btn-logout").addEventListener("click", () => modal({
  title: "退出登录", body: "确定要退出当前账号吗？", danger: true, okText: "退出",
  onOk: () => logout(),
}));

function showLegal(which) {
  const legal = LEGAL[which];
  const div = document.createElement("div");
  div.innerHTML = legal;
  modal({ title: which === "legal" ? "用户协议（摘要）" : "隐私政策（摘要）", body: div, okText: "我已阅读", onOk: null });
  $("#modal-cancel").textContent = "关闭";
  setTimeout(() => ($("#modal-cancel").textContent = "取消"), 0);
}
$("#btn-legal").addEventListener("click", () => showLegal("privacy"));
$("#link-legal").addEventListener("click", (e) => { e.preventDefault(); showLegal("legal"); });
$("#link-privacy").addEventListener("click", (e) => { e.preventDefault(); showLegal("privacy"); });
$("#hdr-user").addEventListener("click", () => switchTab("settings"));

const LEGAL = {
  legal: `1. 本软件（Classify）由 Linkium DevTeam 提供，用于教室大屏的喊话展示、文件接收与受控的系统操作。
2. 使用本软件需妥善保管账号、密码、配对码与绑定二维码；因保管不善造成的损失由使用者承担。
3. 远程关机、重启等危险操作默认关闭，开启后也仅应在确认教室设备空闲时使用，由此造成的未保存数据丢失由操作者负责。
4. 本软件按"现状"提供，不对适用性作任何担保；在法律允许的最大范围内，作者不对使用本软件造成的直接或间接损失承担责任。
5. 当前版本 V1.1（2026-10-01）。完整协议文本随安装包附带（docs/用户协议.md），安装即视为已阅读并同意。`,
  privacy: `1. 我们不收集任何个人身份信息。唯一的数据是你自行设置的用户名与密码（密码经 PBKDF2 加盐哈希后存储，不可还原）。
2. 喊话内容与文件经云端中继（Cloudflare Workers）：喊话在收件箱最多保留 24 小时，文件默认保留 7 天后自动删除；设备与教师解绑后即刻失效。
3. 启用 Azure 神经语音时，喊话文本会发送至微软 Azure 语音服务用于合成；默认本地引擎不出设备。
4. 本软件不包含任何统计 SDK、广告或追踪组件。
5. 当前版本 V1.1（2026-10-01）。完整政策见随附的 docs/隐私政策.md。`,
};

/* PWA 安装 */
window.addEventListener("beforeinstallprompt", (e) => {
  e.preventDefault();
  state.deferredInstall = e;
  $("#install-btn").hidden = false;
  $("#btn-install2").hidden = false;
  $("#pwa-hint").textContent = "点击下方按钮，一键安装到主屏幕。";
});
async function tryInstall() {
  if (!state.deferredInstall) return toast("请使用浏览器菜单中的「添加到主屏幕」", true);
  state.deferredInstall.prompt();
  const choice = await state.deferredInstall.userChoice;
  if (choice.outcome === "accepted") toast("安装成功 🎉");
  state.deferredInstall = null;
  $("#install-btn").hidden = true;
  $("#btn-install2").hidden = true;
}
$("#install-btn").addEventListener("click", tryInstall);
$("#btn-install2").addEventListener("click", tryInstall);
if (location.protocol === "https:" && "serviceWorker" in navigator) {
  navigator.serviceWorker.register("/sw.js").catch(() => {});
}
if (/iPhone|iPad|iPod/i.test(navigator.userAgent)) {
  $("#pwa-hint").innerHTML = 'iOS：在 Safari 中点击 <b>分享 ⬆️ → 添加到主屏幕</b> 即可安装。';
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
  const hm = `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
  return sameDay ? `今天 ${hm}` : `${d.getMonth() + 1}/${d.getDate()} ${hm}`;
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

/* ---------------- 启动 ---------------- */

$$(".nav-item").forEach(b => b.addEventListener("click", () => switchTab(b.dataset.tab)));

if (DEMO) {
  $("#demo-banner").hidden = false;
  state.uid = "demo0001";
  state.username = "wanglaoshi";
  showApp();
} else {
  const bindReq = new URLSearchParams(location.search).get("req");
  if (bindReq) state.pendingBind = bindReq;
  if (state.token) {
    api("/me").then((me) => {
      state.uid = me.uid;
      state.username = me.username;
      showApp();
    }).catch(() => {});
  } else {
    showAuth();
  }
}
