/* Classify WebUI — 喊话、快捷语、历史、目标设备、文件上传与图片投屏
 * 经典脚本：依赖 core.js 的全局 $ / state / api / toast / modal / DEMO 与工具函数。
 * 事件在 boot.js 中统一绑定；本文件只提供渲染与业务动作。
 */
"use strict";

/* ---------------- 常用语（本机）与喊话历史（云端，最近 20 条） ---------------- */

function getQuick() {
  try { return JSON.parse(storage.getItem("cc_quick") || "[]"); } catch { return []; }
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
    c.innerHTML = '<span style="max-width:140px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"></span><b style="margin-left:6px">✕</b>';
    c.querySelector("span").textContent = q;
    c.querySelector("span").onclick = () => { $("#an-text").value = q; $("#an-text").dispatchEvent(new Event("input")); };
    c.querySelector("b").onclick = (e) => {
      e.stopPropagation();
      storage.setItem("cc_quick", JSON.stringify(getQuick().filter((x) => x !== q)));
      renderQuick();
    };
    box.appendChild(c);
  }
}

function saveQuick() {
  const t = $("#an-text").value.trim();
  if (!t) return toast("先输入内容再保存为常用语", true);
  if (t.length > 60) return toast("常用语建议 60 字以内", true);
  const list = getQuick().filter((x) => x !== t);
  list.unshift(t);
  storage.setItem("cc_quick", JSON.stringify(list.slice(0, 12)));
  renderQuick();
  toast("已存为常用语 ⭐");
}

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
    c.innerHTML = '<span style="max-width:130px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"></span>';
    c.querySelector("span").textContent = h.text;
    c.onclick = () => { $("#an-text").value = h.text; $("#an-text").dispatchEvent(new Event("input")); };
    box.appendChild(c);
  }
}

/* ---------------- 目标设备 ---------------- */

function renderTargets() {
  const box = $("#an-targets");
  box.innerHTML = "";
  const mk = (label, isSel, onClick, online = true) => {
    const c = document.createElement("button");
    c.type = "button";
    c.className = "chip" + (isSel ? " sel" : "");
    c.innerHTML = (online ? '<span class="dot ' + (online ? "on" : "") + '"></span>' : "") + label;
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
    mk(d.name + (d.online ? "" : "（离线）"), state.targets.has(d.did), () => {
      if (state.targets.has(d.did)) state.targets.delete(d.did); else state.targets.add(d.did);
      renderTargets();
    }, d.online);
  }
}

/* ---------------- 喊话发送与送达回执 ---------------- */

function updateAnnounceCount() {
  $("#an-count").textContent = $("#an-text").value.length + " / 2000";
}

async function sendAnnounce() {
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
    $("#an-hint").textContent = "✅ 已发送到 " + total + " 台设备";
    toast("喊话已发送 📢");
    if (r.mid && total > 0) pollReceipts(r.mid, total); // 送达/显示回执
    renderHistory().catch(() => {});
  } catch (ex) {
    $("#an-hint").textContent = "";
    toast(ex.message, true);
  } finally {
    $("#an-send").disabled = false;
  }
}

// 送达回执轮询：设备上报 received/displayed，实时更新在发送按钮下方
async function pollReceipts(mid, total) {
  for (let i = 0; i < 8; i++) {
    await new Promise((s) => setTimeout(s, 2500));
    try {
      const r = await api("/announce/receipts?mid=" + encodeURIComponent(mid));
      const received = r.receipts.filter((x) => x.status !== "failed").length;
      const displayed = r.receipts.filter((x) => x.status === "displayed").length;
      $("#an-hint").textContent = "📡 已送达 " + received + "/" + total + " · 已显示 " + displayed + "/" + total;
      if (received >= total && displayed >= total) return;
    } catch { return; }
  }
}

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
      toast(data.message || ("上传失败 (" + xhr.status + ")"), true);
      if (hint) hint.textContent = "";
    }
  });
  xhr.addEventListener("error", () => { toast("网络错误", true); if (hint) hint.textContent = ""; });
  xhr.send(fd);
}

function onCastPicked(e) {
  if (e.target.files.length) castImage(e.target.files[0]);
  e.target.value = "";
}

/* ---------------- 文件 ---------------- */

function onDropzoneClick(e) {
  // 程序化 click 会冒泡回 dropzone，忽略 input 自身的事件避免递归
  if (e && e.target && e.target.id === "file-input") return;
  $("#file-input").click();
}

function onDropzoneDragOver(e) {
  e.preventDefault();
  e.currentTarget.classList.add("over");
}

function onDropzoneDragLeave(e) {
  e.currentTarget.classList.remove("over");
}

function onDropzoneDrop(e) {
  e.preventDefault();
  e.currentTarget.classList.remove("over");
  if (e.dataTransfer.files.length) uploadFile(e.dataTransfer.files[0]);
}

function onFileInputChange(e) {
  if (e.target.files.length) uploadFile(e.target.files[0]);
  e.target.value = "";
}

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
      label.textContent = "上传中 " + Math.min(p, 100) + "%（演示）";
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
      label.textContent = "上传中 " + pct + "%  ·  " + fmtSize(e.loaded) + " / " + fmtSize(e.total);
    }
  });
  xhr.addEventListener("load", () => {
    let data = {};
    try { data = JSON.parse(xhr.responseText); } catch {}
    if (xhr.status >= 200 && xhr.status < 300) finish("✅ 已发送到 " + (data.sent || 0) + " 台设备", false);
    else finish(data.message || ("上传失败 (" + xhr.status + ")"), true);
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
      ? '<button type="button" class="btn btn-tonal btn-sm" style="margin-right:6px" data-view="' + f.fid + '">查看</button>' +
        '<button type="button" class="btn btn-tonal btn-sm" style="margin-right:6px" data-cast="' + f.fid + '">投屏</button>' : "";
    const pinLabel = f.pinned ? "📌 已置顶" : "📌 置顶";
    div.innerHTML = '<span class="ico">' + (f.pinned ? "📌" : fileEmoji(f.name)) + '</span>' +
      '<div class="meta"><b></b><span>' + fmtSize(f.size) + ' · ' + fmtTime(f.created) + ' · ' + targetsText(f.targets) + '</span></div>' +
      viewBtn +
      '<button type="button" class="btn btn-tonal btn-sm" style="margin-right:6px" data-resend="' + f.fid + '">重新推送</button>' +
      '<button type="button" class="btn btn-tonal btn-sm" style="margin-right:6px" data-pin="' + f.fid + '">' + pinLabel + '</button>' +
      '<button type="button" class="act" title="删除">🗑</button>';
    div.querySelector("b").textContent = f.name;
    const vb = div.querySelector("[data-view]");
    if (vb) vb.onclick = () => viewFile(f);
    const castBtn = div.querySelector("[data-cast]");
    if (castBtn) castBtn.onclick = () => {
      surface("image", { fid: f.fid, name: f.name }, "已投到教室大屏 🖼️");
    };
    div.querySelector("[data-resend]").onclick = async () => {
      try {
        const r = await api("/files/" + f.fid + "/resend", { method: "POST", json: {} });
        toast("已重新推送到 " + r.sent + " 台设备 🗂️");
      } catch (ex) { toast(ex.message, true); }
    };
    div.querySelector("[data-pin]").onclick = async () => {
      try {
        await api("/files/" + f.fid + "/pin", { method: "POST", json: { pinned: !f.pinned } });
        toast(f.pinned ? "已取消置顶" : "已置顶（不会被 7 天自动清理）📌");
        refreshFiles().catch(() => {});
      } catch (ex) { toast(ex.message, true); }
    };
    div.querySelector(".act").onclick = () => {
      modal({
        title: "删除文件", body: "确定删除「" + f.name + "」吗？已下载到桌面的副本不受影响。",
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
    const resp = await fetch("/api/files/" + f.fid + "/content", {
      headers: { authorization: "Bearer " + state.token },
    });
    if (!resp.ok) throw new Error("加载失败 (" + resp.status + ")");
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
