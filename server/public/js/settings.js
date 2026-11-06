/* Classify WebUI — 设备与系统控制、账号/头像、协议/安装、登录注册
 * 经典脚本：依赖 core.js（$ / state / api / toast / modal / DEMO）与
 * compose.js（renderTargets）、classroom.js（loadPreclass 由 core.switchTab 调用）。
 * 事件在 boot.js 中统一绑定；本文件只提供渲染与业务动作。
 */
"use strict";

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
    body.innerHTML = '<p style="margin:0 0 4px">发现设备：<b></b></p>' +
      '<p class="muted" style="margin:0">绑定后，这台设备的喊话与文件将发送到你的账号。</p>';
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
        body.innerHTML = '<p style="margin:0;color:#b3261e"></p>';
        body.querySelector("p").textContent = ex.message;
      } finally { ok.disabled = false; }
    };
  } catch (ex) {
    body.innerHTML = '<p style="margin:0;color:#b3261e"></p>';
    body.querySelector("p").textContent = ex.message;
    $("#bind-cancel").textContent = "关闭";
  }
}

function cancelBind() {
  state.pendingBind = null;
  stripBindQuery();
  $("#bind-overlay").hidden = true;
  $("#bind-cancel").textContent = "暂不绑定";
}

/* ---------------- 反馈 ---------------- */

function openFeedback() {
  const box = document.createElement("div");
  box.innerHTML = '<select id="fb-cat" class="cb-select" style="width:100%;margin-bottom:8px">' +
    '<option value="">选择类别（可选）</option>' +
    '<option>功能异常</option><option>体验建议</option><option>连接问题</option>' +
    '<option>看板/EasiCare</option><option>其他</option>' +
    '</select>' +
    '<textarea id="fb-msg" rows="4" maxlength="500" class="area" ' +
    'placeholder="请描述遇到的问题或建议（≤500 字）。为保护隐私，请不要填写学生姓名等个人信息。" style="width:100%"></textarea>' +
    '<p class="body-s on-var" style="margin:6px 0 0">复杂问题可邮件 <a href="mailto:support@linkium.top">support@linkium.top</a>，教室电脑端还可附脱敏日志。</p>';
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
}

/* ---------------- 登录 / 注册 / 自助重置 ---------------- */

let authMode = "login";

function setAuthMode(mode) {
  authMode = mode;
  $$(".auth-tab").forEach(b => b.classList.toggle("active", b.dataset.mode === mode));
  $("#pwd2-row").hidden = authMode === "login";
  const sr = $("#subject-row");
  if (sr) sr.hidden = authMode === "login";
  $("#auth-submit").textContent = authMode === "login" ? "登 录" : "注 册";
  $("#auth-error").hidden = true;
}

// 忘记密码：展开自助重置（需重置码：注册时展示过，或在绑定教室电脑上生成）
function toggleResetPanel() {
  const p = $("#reset-panel");
  p.hidden = !p.hidden;
  $("#btn-forgot").textContent = p.hidden ? "忘记密码？" : "收起重置";
}

async function onPasswordReset() {
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
    storage.setItem("cc_token", data.token);
    toast("密码已重置并登录 ✅");
    showApp();
  } catch (ex) {
    err.textContent = ex.message;
    err.hidden = false;
  } finally {
    $("#reset-submit").disabled = false;
  }
}

async function onAuthSubmit(e) {
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
    storage.setItem("cc_token", data.token);
    showApp();
    if (data.resetCode) {
      // 重置码仅此一次展示
      const div = document.createElement("div");
      div.innerHTML = '<p style="margin:0 0 8px">请<b>立即抄写并保存</b>你的密码重置码（忘记密码时凭它自助重置，仅展示这一次）：</p>' +
        '<b style="font-size:22px;letter-spacing:3px;display:block;text-align:center;background:var(--surface-c-high);border-radius:8px;padding:10px"></b>' +
        '<p class="body-s on-var" style="margin:8px 0 0">也可在已绑定的教室电脑「设置 → 密码重置码」随时重新生成。</p>';
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
}

// Turnstile：配置了站点密钥才渲染人机验证
async function initTurnstile() {
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
}

/* ---------------- 协议 ---------------- */

const LEGAL = {
  legal: "1. 本软件（Classify）由 Linkium DevTeam 提供，用于教室大屏的喊话展示、文件接收与受控的系统操作。\n" +
    "2. 使用本软件需妥善保管账号、密码、配对码与绑定二维码；因保管不善造成的损失由使用者承担。\n" +
    "3. 远程关机、重启等危险操作默认关闭，开启后也仅应在确认教室设备空闲时使用，由此造成的未保存数据丢失由操作者负责。\n" +
    "4. 本软件按\"现状\"提供，不对适用性作任何担保；在法律允许的最大范围内，作者不对使用本软件造成的直接或间接损失承担责任。\n" +
    "5. 当前版本 V1.1（2026-10-01）。完整协议文本随安装包附带（docs/用户协议.md），安装即视为已阅读并同意。",
  privacy: "1. 我们不收集任何个人身份信息。唯一的数据是你自行设置的用户名与密码（密码经 PBKDF2 加盐哈希后存储，不可还原）。\n" +
    "2. 喊话内容与文件经云端中继（Cloudflare Workers）：喊话在收件箱最多保留 24 小时，文件默认保留 7 天后自动删除；设备与教师解绑后即刻失效。\n" +
    "3. 启用 Azure 神经语音时，喊话文本会发送至微软 Azure 语音服务用于合成；默认本地引擎不出设备。\n" +
    "4. 本软件不包含任何统计 SDK、广告或追踪组件。\n" +
    "5. 当前版本 V1.1（2026-10-01）。完整政策见随附的 docs/隐私政策.md。",
};

function showLegal(which) {
  const legal = LEGAL[which];
  const div = document.createElement("div");
  div.innerHTML = legal;
  modal({ title: which === "legal" ? "用户协议（摘要）" : "隐私政策（摘要）", body: div, okText: "我已阅读", onOk: null });
  $("#modal-cancel").textContent = "关闭";
  setTimeout(() => ($("#modal-cancel").textContent = "取消"), 0);
}

/* ---------------- 头像 ---------------- */

async function onAvatarPicked(e) {
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
}

async function onAvatarClear() {
  try {
    await api("/me/avatar", { method: "POST", json: { avatar: "" } });
    state.avatar = "";
    applyAvatar();
    toast("已恢复默认头像");
  } catch (ex) { toast(ex.message, true); }
}

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

/* ---------------- 注销账号 / 退出登录 ---------------- */

function openDeleteAccount() {
  const box = document.createElement("div");
  box.innerHTML = '<p style="margin:0 0 8px">将<b>永久删除</b>你的账号：全部设备解绑、云端文件清空、看板/课表/操作集等数据一并清除，<b>不可恢复</b>。</p>' +
    '<p class="body-s on-var" style="margin:0 0 8px">请输入用户名 <b id="del-un-label"></b> 确认，并输入密码：</p>' +
    '<input id="del-un" class="cd-input" style="width:100%;margin-bottom:8px" placeholder="输入用户名确认">' +
    '<input id="del-pw" type="password" class="cd-input" style="width:100%" placeholder="登录密码">';
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
}

function confirmLogout() {
  modal({
    title: "退出登录", body: "确定要退出当前账号吗？", danger: true, okText: "退出",
    onOk: () => logout(),
  });
}

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
    div.innerHTML = '<span class="ico">🖥️</span>' +
      '<div class="meta"><b></b><span><span class="dot ' + (d.online ? "on" : "") + '"></span> ' +
      (d.online ? "在线" : "离线") + ' · ID ' + d.did + ' · 最近活跃 ' + fmtTime(d.lastSeen) + '</span></div>' +
      '<button type="button" class="act">解绑</button>';
    div.querySelector("b").textContent = d.name;
    div.querySelector(".act").onclick = () => {
      modal({
        title: "解绑设备", body: "确定解绑「" + d.name + "」吗？该设备需要重新配对才能使用。",
        danger: true, okText: "解绑",
        onOk: async () => { try { await api("/devices/" + d.did, { method: "DELETE" }); refreshDevices().catch(() => {}); } catch (ex) { toast(ex.message, true); } },
      });
    };
    list.appendChild(div);
  }
}

async function createPairCode() {
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
      $("#pair-timer").textContent = m + ":" + String(s).padStart(2, "0") + " 后过期";
    }, 500);
    $("#pair-timer").textContent = "10:00 后过期";
  } catch (ex) {
    toast(ex.message, true);
  }
}

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
    o.textContent = d.name + (d.online ? "" : "（离线）");
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

function onVolumeInput() {
  $("#vol-val").textContent = $("#vol").value + "%";
}

async function sendCommand(name, value) {
  const t = $("#sys-devices").value;
  const targets = t === "all" ? undefined : [t];
  const r = await api("/command", { method: "POST", json: { name, value, targets } });
  toast("指令已发送（" + r.sent + " 台设备）");
}

function confirmRunCommand(title, name) {
  modal({
    title, body: "确定要对所选设备执行「" + title + "」吗？请确认教室内无正在进行的授课。",
    danger: true, okText: title,
    onOk: () => sendCommand(name).catch(e => toast(e.message, true)),
  });
}

/* ---------------- PWA 安装 ---------------- */

function onBeforeInstallPrompt(e) {
  e.preventDefault();
  state.deferredInstall = e;
  $("#install-btn").hidden = false;
  $("#btn-install2").hidden = false;
  $("#pwa-hint").textContent = "点击下方按钮，一键安装到主屏幕。";
}

async function tryInstall() {
  if (!state.deferredInstall) return toast("请使用浏览器菜单中的「添加到主屏幕」", true);
  state.deferredInstall.prompt();
  const choice = await state.deferredInstall.userChoice;
  if (choice.outcome === "accepted") toast("安装成功 🎉");
  state.deferredInstall = null;
  $("#install-btn").hidden = true;
  $("#btn-install2").hidden = true;
}

function initPwa() {
  if (location.protocol === "https:" && "serviceWorker" in navigator) {
    navigator.serviceWorker.register("/sw.js").catch(() => {});
  }
  if (/iPhone|iPad|iPod/i.test(navigator.userAgent)) {
    const hint = $("#pwa-hint");
    if (hint) hint.innerHTML = 'iOS：在 Safari 中点击 <b>分享 ⬆️ → 添加到主屏幕</b> 即可安装。';
  }
}
