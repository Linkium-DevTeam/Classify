/* Classify 开发者控制台（/admin）— 仅部署者账号（KV owners） */
"use strict";

const $ = (s) => document.querySelector(s);
const token = localStorage.getItem("cc_token") || null;

let toastTimer = null;
function toast(msg, isErr = false) {
  const t = $("#toast");
  t.textContent = msg;
  t.classList.toggle("err", isErr);
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), 2600);
}

async function api(path, opts = {}) {
  const headers = opts.headers || {};
  if (token) headers["authorization"] = "Bearer " + token;
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
  const data = await resp.json().catch(() => ({}));
  if (resp.status === 401) throw new Error("登录已过期，请回教师端重新登录");
  if (resp.status === 403 && data.error === "forbidden") throw new Error("仅开发者账号可访问此控制台");
  if (!resp.ok) throw new Error(data.message || `请求失败 (${resp.status})`);
  return data;
}

function fmtTime(ts) {
  if (!ts) return "—";
  const d = new Date(ts);
  const today = new Date();
  const hm = `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
  return d.toDateString() === today.toDateString() ? `今天 ${hm}` : `${d.getMonth() + 1}/${d.getDate()} ${hm}`;
}

function deny(msg) {
  $("#gate").innerHTML = `<h2>🚫 ${msg}</h2><p class="body-m on-var">此控制台仅限部署者账号访问。</p><a class="btn btn-filled" href="/">返回教师端</a>`;
}

async function boot() {
  if (!token) return deny("请先登录");
  try {
    const me = await api("/me");
    if (me.role !== "admin") return deny("此控制台仅限部署者账号访问");
  } catch (ex) {
    return deny(ex.message);
  }
  $("#gate").hidden = true;
  $("#console").hidden = false;
  await Promise.all([refreshPanel(), loadFeedback(), loadCrashes(), refreshBroadcastText()]);
  setInterval(() => { refreshPanel().catch(() => {}); }, 30000);
}

/* ---------- 总览 + 用户 + 设备 ---------- */

let panelCache = null;

async function refreshPanel() {
  try {
    const r = await api("/admin/panel");
    panelCache = r;
    const online = r.devices.filter((d) => Date.now() - d.lastSeen < 90000).length;
    const stats = $("#stats");
    stats.innerHTML = "";
    const mk = (num, label) => {
      const d = document.createElement("div");
      d.className = "stat";
      d.innerHTML = "<b></b><span></span>";
      d.querySelector("b").textContent = num;
      d.querySelector("span").textContent = label;
      stats.appendChild(d);
    };
    mk(r.users.length, "注册教师");
    mk(`${online}/${r.devices.length}`, "设备在线");
    mk(r.links.length, "绑定关系");
    const reg = document.getElementById("reg-state");
    if (reg) reg.textContent = r.registrationOpen !== false ? "注册当前开放" : "注册当前已关闭";
    renderUsers(r.users);
    renderDevices(r.devices);
  } catch (ex) {
    const ul = $("#user-list");
    if (ul) ul.textContent = "加载失败：" + ex.message;
  }
}

function renderUsers(users) {
  const box = $("#user-list");
  box.innerHTML = "";
  for (const u of users) {
    const div = document.createElement("div");
    div.className = "dev-item";
    const row = document.createElement("div");
    row.className = "row";
    const info = document.createElement("span");
    info.className = "body-s on-var";
    info.style.flex = "1";
    info.innerHTML = `<b>${u.username}</b> · ${u.subject} · ${u.uid}${u.banned ? ' <span class="banned-tag">已封禁</span>' : ""}`;
    row.appendChild(info);
    const mkBtn = (label, cls, fn) => {
      const b = document.createElement("button");
      b.className = "btn btn-sm " + cls;
      b.textContent = label;
      b.onclick = fn;
      row.appendChild(b);
    };
    mkBtn(u.banned ? "解封" : "封禁", u.banned ? "btn-tonal" : "btn-outline-danger", () => banFlow(u));
    mkBtn("重置码", "btn-tonal", () => genResetCode(u.username));
    mkBtn("删除", "btn-outline-danger", () => deleteUserFlow(u));
    div.appendChild(row);
    if (u.banned && u.banReason) {
      const why = document.createElement("p");
      why.className = "body-s on-var";
      why.style.margin = "4px 0 0";
      why.textContent = "封禁原因：" + u.banReason;
      div.appendChild(why);
    }
    box.appendChild(div);
  }
}

function banFlow(u) {
  const banning = !u.banned;
  const reason = banning ? (prompt(`封禁 ${u.username} 的原因（会展示给对方，可留空）：`) ?? "") : "";
  if (banning && !confirm(`确定封禁 ${u.username}？其将无法登录 WebUI、绑定设备或调用接口。`)) return;
  if (!banning && !confirm(`解除对 ${u.username} 的封禁？解封后需重新登录。`)) return;
  api("/admin/" + (banning ? "ban" : "unban"), { method: "POST", json: { username: u.username, reason } })
    .then(() => { toast((banning ? "已封禁 " : "已解封 ") + u.username); refreshPanel(); })
    .catch((ex) => toast(ex.message, true));
}

function deleteUserFlow(u) {
  if (!confirm(`永久删除 ${u.username}？其设备将解绑、云端文件与全部数据清除，不可恢复。`)) return;
  api("/admin/delete-user", { method: "POST", json: { username: u.username } })
    .then((r) => { toast(`已删除 ${u.username}（连带设备 ${r.removedDevices || 0} 台）`); refreshPanel(); })
    .catch((ex) => toast(ex.message, true));
}

function genResetCode(username) {
  api("/admin/reset-code", { method: "POST", json: { username } })
    .then((r) => { $("#rc-out").textContent = `${r.username} 的重置码：${r.code}（30 分钟内有效，一次性）`; toast("重置码已生成 🔑"); })
    .catch((ex) => toast(ex.message, true));
}

function renderDevices(devices) {
  const box = $("#dev-list");
  box.innerHTML = "";
  if (!devices.length) { box.textContent = "暂无设备"; return; }
  for (const d of devices) {
    const p = document.createElement("p");
    p.className = "dev-item";
    const online = Date.now() - d.lastSeen < 90000;
    p.textContent = `${d.name} (${d.did}) · v${d.version || "?"} · ${online ? "在线" : "离线"} · 最近活跃 ${fmtTime(d.lastSeen)}`
      + (d.caps ? ` · 摄像头${d.caps.camera === false ? "关" : "开"}/电源${d.caps.power === false ? "关" : "开"}` : "");
    box.appendChild(p);
  }
}

/* ---------- 公告 / 注册开关 / 清缓存 ---------- */

async function refreshBroadcastText() {
  try {
    const b = (await fetch("/api/broadcast").then((r) => r.json())).broadcast;
    if (b) $("#bc-text").value = b.text;
  } catch {}
}

$("#bc-pub").addEventListener("click", async () => {
  const text = $("#bc-text").value.trim();
  if (!text) return toast("请填写公告内容", true);
  try {
    await api("/admin/broadcast", { method: "POST", json: {
      text, level: $("#bc-level").value, hours: parseInt($("#bc-hours").value, 10) || 72,
    }});
    toast("公告已发布 📢");
  } catch (ex) { toast(ex.message, true); }
});

$("#bc-clear").addEventListener("click", async () => {
  if (!confirm("撤下当前公告？")) return;
  try { await api("/admin/broadcast", { method: "DELETE" }); toast("公告已撤下"); }
  catch (ex) { toast(ex.message, true); }
});

$("#reg-toggle").addEventListener("click", async () => {
  try {
    const r = await api("/admin/panel");
    const open = r.registrationOpen !== false;
    if (!confirm(open ? "关闭注册？新用户将无法注册（已注册教师不受影响）。" : "重新开放注册？")) return;
    const rr = await api("/admin/registration", { method: "POST", json: { open: !open } });
    toast(rr.open ? "注册已开放" : "注册已关闭");
    refreshPanel();
  } catch (ex) { toast(ex.message, true); }
});

$("#rb-purge").addEventListener("click", () => {
  if (!confirm("清空云端全部看板快照？（设备端本地数据不受影响）")) return;
  api("/admin/readboard-purge", { method: "POST", json: {} })
    .then(() => toast("看板快照缓存已清空"))
    .catch((ex) => toast(ex.message, true));
});

$("#rc-gen").addEventListener("click", async () => {
  const username = $("#rc-username").value.trim();
  if (!username) return toast("请输入用户名", true);
  try {
    const r = await api("/admin/reset-code", { method: "POST", json: { username } });
    $("#rc-out").textContent = `${r.username} 的重置码：${r.code}（30 分钟内有效，一次性）`;
  } catch (ex) { toast(ex.message, true); }
});

/* ---------- 反馈 / 崩溃 ---------- */

async function loadFeedback() {
  const box = $("#fb-list");
  try {
    const r = await api("/admin/feedback");
    const list = r.feedback || [];
    box.innerHTML = list.length ? "" : "暂无反馈";
    for (const f of list.slice(0, 30)) {
      const div = document.createElement("div");
      div.className = "dev-item";
      div.innerHTML = `<div class="row"><span class="body-s on-var"></span></div><p class="body-m" style="margin:2px 0"></p>`;
      div.querySelector("span").textContent = `${f.category || "未分类"} · ${fmtTime(f.at)} · ${f.by || ""}`;
      div.querySelector("p").textContent = f.message;
      if (f.logs) {
        const d = document.createElement("details");
        d.innerHTML = `<summary class="body-s on-var">附日志</summary><pre class="dev-logs"></pre>`;
        d.querySelector(".dev-logs").textContent = f.logs;
        div.appendChild(d);
      }
      box.appendChild(div);
    }
  } catch (ex) { box.textContent = "加载失败：" + ex.message; }
}

async function loadCrashes() {
  const box = $("#crash-list");
  try {
    const r = await api("/admin/crashes");
    const list = r.crashes || [];
    box.innerHTML = list.length ? "" : "7 天内无崩溃上报 ✓";
    for (const c of list.slice(0, 20)) {
      const div = document.createElement("div");
      div.className = "dev-item";
      div.innerHTML = `<span class="body-s on-var"></span><pre class="dev-logs"></pre>`;
      div.querySelector("span").textContent = `v${c.version} · ${fmtTime(c.at)}`;
      div.querySelector(".dev-logs").textContent = (c.detail || "").slice(0, 800) || "（无详情）";
      box.appendChild(div);
    }
  } catch (ex) { box.textContent = "加载失败：" + ex.message; }
}

boot();
