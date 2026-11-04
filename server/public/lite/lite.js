/* 自动生成，请改 lite/web（tools/build-web.ps1） */
/* Classify Lite 控制页 — 主站 /lite 与独立 worker 共用同一份逻辑
 * API 前缀优先级：window.LITE_API_BASE > <body data-api-base> > /api
 * 主站页面为 /lite/api（同源反代），独立 worker 页面为 /api。
 */
(function () {
  "use strict";

  var body = document.body;
  var API_BASE = String(
    window.LITE_API_BASE || body.getAttribute("data-api-base") || "/api"
  ).replace(/\/+$/, "");
  var CODE_KEY = "cc_lite_code";

  var codeEl = document.getElementById("code");
  var textEl = document.getElementById("text");
  var sendEl = document.getElementById("send");
  var statusEl = document.getElementById("status");
  var msgOk = document.getElementById("msg-ok");
  var msgErr = document.getElementById("msg-err");
  if (!codeEl || !textEl || !sendEl || !statusEl || !msgOk || !msgErr) return;

  var statusSeq = 0;
  var statusTimer = null;
  var msgTimer = null;

  function readCode() {
    return codeEl.value.replace(/\D/g, "").slice(0, 8);
  }

  function saveCode(code) {
    try { if (code) localStorage.setItem(CODE_KEY, code); } catch (e) { /* 隐私模式忽略 */ }
  }

  function loadCode() {
    try { return localStorage.getItem(CODE_KEY) || ""; } catch (e) { return ""; }
  }

  function setStatus(online, text) {
    statusEl.textContent = "";
    var dot = document.createElement("span");
    dot.className = "dot " + (online ? "on" : "off");
    statusEl.appendChild(dot);
    statusEl.appendChild(document.createTextNode(text));
  }

  function setMsg(el, text) {
    hideMsgs();
    el.textContent = text;
    el.style.display = "block";
    clearTimeout(msgTimer);
    msgTimer = setTimeout(function () { el.style.display = "none"; }, 4000);
  }

  function hideMsgs() {
    msgOk.style.display = "none";
    msgErr.style.display = "none";
  }

  async function checkDevice() {
    var code = readCode();
    var seq = ++statusSeq;
    if (code.length !== 8) {
      setStatus(false, "输入 8 位设备口令");
      return;
    }
    try {
      var r = await fetch(API_BASE + "/info?code=" + encodeURIComponent(code), { cache: "no-store" });
      if (seq !== statusSeq) return; // 已有更新的输入，丢弃过期结果
      if (r.ok) {
        var d = await r.json().catch(function () { return {}; });
        if (seq !== statusSeq) return;
        setStatus(true, "已连接：" + (d.name || "Clite Device"));
      } else {
        setStatus(false, "设备未上线");
      }
    } catch (e) {
      if (seq === statusSeq) setStatus(false, "连接失败");
    }
  }

  // 输入防抖：避免每按一个数字就请求一次状态
  function scheduleCheck() {
    clearTimeout(statusTimer);
    statusTimer = setTimeout(checkDevice, 450);
  }

  async function doSend() {
    hideMsgs();
    var code = readCode();
    var text = textEl.value.trim();
    if (code.length !== 8) return setMsg(msgErr, "请输入 8 位设备口令");
    if (!text) return setMsg(msgErr, "请输入喊话内容");
    sendEl.disabled = true;
    sendEl.textContent = "发送中…";
    try {
      var r = await fetch(API_BASE + "/send", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ code: code, text: text }),
      });
      var d = await r.json().catch(function () { return {}; });
      if (r.ok) {
        saveCode(code); // 发送成功后保留口令，下次自动填充
        setMsg(msgOk, "已发送 ✓");
        textEl.value = "";
        // 首次发送成功后把捐助卡高亮提醒一次（每浏览器一次）
        try {
          if (!localStorage.getItem("donateNudged")) {
            var donate = document.getElementById("donate");
            if (donate) {
              donate.classList.add("nudge");
              donate.scrollIntoView({ behavior: "smooth", block: "nearest" });
              setTimeout(function () { donate.classList.remove("nudge"); }, 3500);
            }
            localStorage.setItem("donateNudged", "1");
          }
        } catch (e) { /* localStorage 不可用时忽略 */ }
      } else {
        setMsg(msgErr, d.error || "发送失败");
      }
    } catch (e) {
      setMsg(msgErr, "网络错误");
    } finally {
      sendEl.disabled = false;
      sendEl.textContent = "📢 发送到大屏";
    }
  }

  // 8 位口令存入 localStorage，下次打开自动填充
  var saved = loadCode();
  if (/^\d{8}$/.test(saved)) codeEl.value = saved;

  var fullLink = document.getElementById("full-app-link");
  var appUrl = body.getAttribute("data-app-url");
  if (fullLink && appUrl) fullLink.href = appUrl;

  codeEl.addEventListener("input", function () {
    var code = readCode();
    if (code.length === 8) saveCode(code);
    scheduleCheck();
  });
  codeEl.addEventListener("change", function () {
    var code = readCode();
    if (code.length === 8) saveCode(code);
    clearTimeout(statusTimer);
    checkDevice();
  });

  sendEl.addEventListener("click", doSend);

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", checkDevice, { once: true });
  } else {
    checkDevice();
  }
})();
