/* Classify WebUI — 启动：DOM 就绪后统一绑定事件并初始化
 * 这是 index.html 中最后一个脚本。所有 addEventListener 集中在此，
 * 功能脚本（core/compose/classroom/settings）只提供可复用的函数。
 */
"use strict";

function bindEvents() {
  // 主导航 / 顶栏账号
  $$(".nav-item").forEach((b) => b.addEventListener("click", () => switchTab(b.dataset.tab)));
  $("#hdr-user").addEventListener("click", () => switchTab("settings"));

  // 登录 / 注册 / 自助重置
  $$(".auth-tab").forEach((btn) => btn.addEventListener("click", () => setAuthMode(btn.dataset.mode)));
  $("#btn-forgot").addEventListener("click", toggleResetPanel);
  $("#reset-submit").addEventListener("click", onPasswordReset);
  $("#auth-form").addEventListener("submit", onAuthSubmit);
  $("#link-legal").addEventListener("click", (e) => { e.preventDefault(); showLegal("legal"); });
  $("#link-privacy").addEventListener("click", (e) => { e.preventDefault(); showLegal("privacy"); });
  $("#btn-legal").addEventListener("click", () => showLegal("privacy"));

  // 扫码绑定
  $("#bind-cancel").addEventListener("click", cancelBind);

  // 反馈
  $("#fb-send-btn").addEventListener("click", openFeedback);

  // 喊话 / 常用语 / 历史
  $("#an-text").addEventListener("input", updateAnnounceCount);
  $("#an-save-quick").addEventListener("click", saveQuick);
  $("#an-send").addEventListener("click", sendAnnounce);

  // 随机点名
  $("#picker-run").addEventListener("click", runPicker);

  // 图片快投
  $("#cam-cast-btn").addEventListener("click", () => $("#cam-cast-input").click());
  $("#cam-cast-input").addEventListener("change", onCastPicked);

  // 文件拖拽 / 上传
  $("#file-drop").addEventListener("click", onDropzoneClick);
  $("#file-drop").addEventListener("dragover", onDropzoneDragOver);
  $("#file-drop").addEventListener("dragleave", onDropzoneDragLeave);
  $("#file-drop").addEventListener("drop", onDropzoneDrop);
  $("#file-input").addEventListener("change", onFileInputChange);

  // 账号 / 头像
  $("#st-avatar-btn").addEventListener("click", () => $("#st-avatar-input").click());
  $("#st-avatar-input").addEventListener("change", onAvatarPicked);
  $("#st-avatar-clear").addEventListener("click", onAvatarClear);
  $("#btn-delete-account").addEventListener("click", openDeleteAccount);
  $("#btn-logout").addEventListener("click", confirmLogout);

  // 设备
  $("#pair-btn").addEventListener("click", createPairCode);

  // 系统控制
  $("#sys-devices").addEventListener("change", updateCapLocks);
  $("#vol").addEventListener("input", onVolumeInput);
  $("#btn-vol").addEventListener("click", () => sendCommand("set_volume", parseInt($("#vol").value, 10)).catch((e) => toast(e.message, true)));
  $("#btn-mute").addEventListener("click", () => sendCommand("mute", true).catch((e) => toast(e.message, true)));
  $("#btn-lock").addEventListener("click", () => sendCommand("lock").catch((e) => toast(e.message, true)));
  $("#btn-screen").addEventListener("click", () => sendCommand("screen_off").catch((e) => toast(e.message, true)));
  $("#btn-reboot").addEventListener("click", () => confirmRunCommand("重启电脑", "restart"));
  $("#btn-shutdown").addEventListener("click", () => confirmRunCommand("关机", "shutdown"));

  // 课堂时钟 / 常驻通知 / 拍照
  $("#clock-open").addEventListener("click", openClock);
  $("#clock-stop").addEventListener("click", stopClock);
  $("#cd-start").addEventListener("click", startCountdown);
  $("#notice-send").addEventListener("click", publishNotice);
  $("#notice-clear").addEventListener("click", clearNotice);
  $("#cam-capture").addEventListener("click", capturePhoto);

  // 早读看板 / 任务单 / 规则 / 截屏
  $("#rb-open").addEventListener("click", openReadboard);
  $("#rb-close").addEventListener("click", closeReadboard);
  $("#rb-refresh").addEventListener("click", refreshReadboard);
  $("#ag-run").addEventListener("click", runAutoGroup);
  $("#task-add").addEventListener("click", addTask);
  $("#rb-config-save").addEventListener("click", saveRbConfig);
  $("#screen-capture").addEventListener("click", captureScreen);

  // 智能候课（课程表 + 操作集）
  $("#tt-save").addEventListener("click", saveTimetable);
  $("#tt-paste").addEventListener("click", pasteTimetable);
  $("#action-add").addEventListener("click", addActionRow);
  $("#actions-save").addEventListener("click", saveActions);
  $("#preclass-run").addEventListener("click", runPreclass);
  $("#preclass-override").addEventListener("click", runPreclassOverride);

  // PWA 安装
  window.addEventListener("beforeinstallprompt", onBeforeInstallPrompt);
  $("#install-btn").addEventListener("click", tryInstall);
  $("#btn-install2").addEventListener("click", tryInstall);
}

function init() {
  // 学科注册时已确定，设置页仅展示
  const stSubject = $("#st-subject");
  if (stSubject) stSubject.disabled = true;

  bindEvents();
  initCountdownPresets();
  initPwa();
  initTurnstile();       // /api/config 决定是否渲染 Turnstile
  refreshAppVersion();   // [data-app-version] 不硬编码

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
}

// defer 脚本执行时 DOM 已解析完毕；保留 readyState 判断以便未来改为非 defer 也不出错
if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", init, { once: true });
} else {
  init();
}
