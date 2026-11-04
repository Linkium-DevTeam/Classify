/* Classify WebUI — 早读看板、课程表/候课操作集、课堂表面
 * 经典脚本：依赖 core.js（$ / state / api / toast / modal）与 compose.js（refreshFiles / renderTargets）。
 * 事件在 boot.js 中统一绑定；本文件只提供渲染与业务动作。
 */
"use strict";

/* ---------------- 早读智能看板 ---------------- */

/** 看板成员显示名：新快照只含座号（姓名不出教室电脑），别名对照表存本机 localStorage */
function rbMemberLabel(g, m) {
  if (m.name) return m.name; // 兼容旧设备快照
  const key = g.name + ":" + m.seat;
  try {
    const alias = (JSON.parse(localStorage.getItem("cc_alias") || "{}"))[key];
    if (alias) return alias;
  } catch {}
  return g.name + "·" + m.seat + "号";
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
  taskBox.innerHTML = '<b class="body-m">📋 任务单</b>';
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
    label.textContent = (i + 1) + ". " + t.name + (t.minutes ? "（" + t.minutes + " 分钟）" : "") + (i === state2.activeTask ? " ▶ 进行中" : "");
    row.appendChild(label);
    const running = i === state2.activeTask && state2.timerRunning;
    const start = document.createElement("button");
    start.type = "button";
    start.className = "btn btn-tonal btn-sm";
    start.textContent = running ? "⏹ 结束" : "▶ 开始";
    start.onclick = () => surface("readboard", {
      action: running ? "stoptask" : "starttask", index: i,
    }, "任务「" + t.name + "」" + (running ? "已结束" : "已开始"));
    const del = document.createElement("button");
    del.type = "button";
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
    head.innerHTML = '<b>' + g.name + '</b><span class="rb-slash">' + g.members.length + ' 人</span>' +
      (mode === "slash"
        ? '<button type="button" class="btn btn-tonal btn-sm" data-act="addslash" data-g="' + g.name + '">🎲 随机＋斜杠</button>'
        : "") +
      '<button type="button" class="btn btn-outline-danger btn-sm" data-act="addflower" data-g="' + g.name + '">🌸 随机＋红花</button>' +
      '<button type="button" class="btn btn-tonal btn-sm" data-rbaddmember="' + g.name + '">＋ 成员</button>';

    // 名册管理：添加成员（姓名一次性投递到教室电脑，不落云端存储）
    head.querySelector("[data-rbaddmember]").addEventListener("click", () => {
      const inp = document.createElement("input");
      inp.className = "cd-input"; inp.style.width = "100%";
      modal({
        title: "＋ 向「" + g.name + "」添加成员",
        body: "姓名将随指令一次性投递到教室电脑（送达即从云端消息中删除，不在云端存储）：",
        okText: "添加",
        onOk: async () => {
          const name = (inp.value || "").trim();
          if (!name) return toast("请输入姓名", true);
          try {
            await api("/surface/send", { method: "POST", json: {
              type: "readboard", payload: { action: "addmember", group: g.name, name },
            }});
            toast("已添加 " + name + " → " + g.name);
            setTimeout(() => refreshRbState().catch(() => {}), 1500);
          } catch (ex) { toast(ex.message, true); }
        },
      });
      $("#modal-body").appendChild(inp);
    });

    head.querySelectorAll("button[data-act]").forEach((btn) => {
      btn.addEventListener("click", () => surface("readboard", {
        action: btn.dataset.act, group: btn.dataset.g,
      }, (btn.dataset.act === "addslash" ? "斜杠" : "小红花") + "已派给 " + g.name));
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
        b.type = "button";
        b.className = "btn btn-sm " + (cls || "btn-tonal");
        b.textContent = label;
        b.onclick = () => surface("readboard", {
          action: act, group: g.name, ...(m.name ? { name: m.name } : { seat: m.seat }),
        }, label + " → " + rbMemberLabel(g, m));
        return b;
      };
      const name = document.createElement("span");
      name.className = "rb-name";
      name.textContent = rbMemberLabel(g, m);
      const flower = document.createElement("span");
      flower.className = "rb-count";
      flower.textContent = "🌸×" + (m.flowers || 0);
      row.append(name, flower);
      // 红花模式：教师可按人加减红花；斜杠模式红花为自动派生仅展示
      if (mode === "flower") {
        row.appendChild(mk("🌸＋", "addflower"));
        row.appendChild(mk("🌸－", "removeflower", "btn-outline-danger"));
      }
      // ✕ 移出小组（座号定向，姓名不出电脑）
      const rm = document.createElement("button");
      rm.type = "button";
      rm.className = "btn btn-outline-danger btn-sm";
      rm.textContent = "✕";
      rm.title = "移出小组";
      rm.onclick = () => surface("readboard", {
        action: "removemember", group: g.name, ...(m.name ? { name: m.name } : { seat: m.seat }),
      }, "已移出 " + rbMemberLabel(g, m));
      row.appendChild(rm);
      if (!m.name) {
        const aliasBtn = document.createElement("button");
        aliasBtn.type = "button";
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
            body: "为「" + g.name + "·" + m.seat + "号」设置只显示在你手机上的别名（保存在本机，不上传云端）：",
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

async function runAutoGroup() {
  const size = parseInt($("#ag-size").value, 10);
  if (!(size >= 2)) return toast("每组人数至少 2", true);
  try {
    await api("/surface/send", { method: "POST", json: {
      type: "readboard", payload: { action: "autogroup", size },
    }});
    toast("已按每组 " + size + " 人重新分组");
    setTimeout(() => refreshRbState().catch(() => {}), 1500);
  } catch (ex) { toast(ex.message, true); }
}

async function addTask() {
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
}

function openReadboard() { surface("readboard", { action: "open" }, "看板已开启 📖"); }
function closeReadboard() { surface("readboard", { action: "close" }, "看板已关闭"); }
function refreshReadboard() { refreshRbState().catch(() => {}); }

/* 看板账号级配置：加分模式 + 换算比例 */
async function loadRbConfig() {
  try {
    const r = await api("/readboard/config");
    $("#rb-mode").value = r.config?.mode || "slash";
    $("#rb-ratio").value = r.config?.ratio || 3;
  } catch { /* 默认值即可 */ }
}

async function saveRbConfig() {
  try {
    const r = await api("/readboard/config", { method: "POST", json: {
      mode: $("#rb-mode").value,
      ratio: parseInt($("#rb-ratio").value, 10) || 3,
    }});
    toast("看板规则已保存：" + (r.config.mode === "flower" ? "直接加花" : "斜杠派花") + "，每 " + r.config.ratio + " 条斜杠 = 1 朵 🌸");
  } catch (ex) { toast(ex.message, true); }
}

async function captureScreen() {
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    await api("/surface/send", { method: "POST", json: { type: "screenshot", payload: {}, targets } });
    toast("已触发截屏回传 🖥️");
  } catch (ex) { toast(ex.message, true); }
}

/* ---------------- 智能候课（课程表 + 操作集） ---------------- */

async function loadPreclass() {
  try {
    const r = await api("/timetable");
    renderTimetable(r.timetable);
    const note = $("#tt-updatedby");
    if (note) note.textContent = r.updatedBy ? "当前课表由「" + r.updatedBy + "」更新（多教师共享，后保存覆盖）" : "";
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
  for (const l of tt._saved) saved[l.day + ":" + l.period] = l.subject;

  const dayCount = tt.showSun ? 7 : 6;
  const dayNames = ["一", "二", "三", "四", "五", "六", "日"];

  for (const period of tt.periods) {
    const p = period.p;
    const card = document.createElement("div");
    card.className = "tt-card";
    card.dataset.period = p;

    const head = document.createElement("div");
    head.className = "tt-card-head";
    head.innerHTML = '<b class="tt-card-name">第 ' + p + ' 节</b>' +
      '<label class="tt-time">起<input class="tt-start" type="time" value="' + period.start + '"></label>' +
      '<label class="tt-time">止<input class="tt-end" type="time" value="' + period.end + '" placeholder="自动"></label>' +
      '<button type="button" class="btn btn-outline-danger btn-sm tt-del" title="删除此节">✕</button>';
    card.appendChild(head);

    const daysRow = document.createElement("div");
    daysRow.className = "tt-days";
    for (let day = 1; day <= dayCount; day++) {
      const val = saved[day + ":" + p] || "";
      daysRow.insertAdjacentHTML("beforeend",
        '<label class="tt-day"><span>' + dayNames[day - 1] + '</span><input data-day="' + day + '" data-period="' + p + '" value="' + val + '" placeholder="·"></label>');
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
    b.type = "button";
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

async function saveTimetable() {
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
    toast("课程表已保存（" + periods.length + " 节 · 应用到 " + r.devices + " 台设备）✅");
  } catch (ex) { toast(ex.message, true); }
}

async function pasteTimetable() {
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
}

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
  del.type = "button";
  del.className = "btn btn-outline-danger btn-sm"; del.textContent = "删除";
  del.addEventListener("click", () => row.remove());
  row.append(sel, inp, del);
  return row;
}

function addActionRow() { $("#actions-list").appendChild(actionRow()); }

async function saveActions() {
  const actions = Array.from(document.querySelectorAll("#actions-list .row")).map((row) => ({
    type: row.querySelector("select").value,
    param: row.querySelector("input").value.trim(),
  })).filter((a) => a.type === "board" || a.param !== "");
  try {
    await api("/actions", { method: "POST", json: { actions } });
    toast("候课操作集已保存（" + actions.length + " 项）✅");
  } catch (ex) { toast(ex.message, true); }
}

async function runPreclass() {
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    await api("/surface/send", { method: "POST", json: { type: "preclass", payload: { action: "run-now" }, targets } });
    toast("已触发立即候课 ✅");
  } catch (ex) { toast(ex.message, true); }
}

// 指定学科候课：管理员可指定任意学科（设备按该学科教师操作集执行）
async function runPreclassOverride() {
  const subject = $("#preclass-subject").value;
  if (!subject) return toast("请选择学科", true);
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    await api("/surface/send", { method: "POST", json: { type: "preclass", payload: { action: "override-now", subject }, targets } });
    toast("已触发「" + subject + "」候课 ✅");
  } catch (ex) { toast(ex.message, true); }
}

/* ---------------- 课堂表面（时钟 / 通知 / 拍照 / 点名 / 图片） ---------------- */

async function surface(type, payload, okText) {
  try {
    const targets = state.targets.size ? Array.from(state.targets) : undefined;
    const r = await api("/surface/send", { method: "POST", json: { type, payload, targets } });
    toast(okText + "（" + r.sent + " 台设备）");
  } catch (ex) { toast(ex.message, true); }
}

function openClock() { surface("clock", { action: "start" }, "时钟已打开 ⏰"); }
function stopClock() { surface("clock", { action: "stop" }, "时钟已关闭"); }

function startCountdown() {
  const m = parseInt($("#cd-minutes").value, 10);
  if (!(m > 0)) return toast("请输入有效分钟数", true);
  surface("clock", { action: "start", minutes: m }, "倒计时 " + m + " 分钟已开始 ⏳");
}

function initCountdownPresets() {
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
        toast("⏳ " + m + " 分钟倒计时已开始");
      } catch (ex) { toast(ex.message, true); }
    };
    box.appendChild(b);
  }
}

async function publishNotice() {
  const text = $("#notice-text").value.trim();
  if (!text) return toast("请输入常驻通知内容", true);
  $("#notice-send").disabled = true;
  try {
    await api("/notice", { method: "POST", json: { text } });
    $("#notice-text").value = "";
    toast("常驻通知已发布 📌（显示在灵动岛）");
  } catch (ex) { toast(ex.message, true); }
  finally { $("#notice-send").disabled = false; }
}

async function clearNotice() {
  try {
    await api("/notice", { method: "DELETE" });
    toast("已清除我的常驻通知");
  } catch (ex) { toast(ex.message, true); }
}

async function capturePhoto() {
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
}

function runPicker() {
  modal({
    title: "🎲 随机点名", body: "将从早读看板名册中随机抽一名学生，大屏滚动后定格并朗读姓名。开始？",
    okText: "开始", onOk: () => surface("picker", {}, "点名进行中 🎲"),
  });
}
