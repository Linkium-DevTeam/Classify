// 本页由主站 /lite 托管，API 走 /lite/api/* 反代到 clite worker（同源免 CORS）
const API = "/lite/api";
const $ = s => document.querySelector(s);
function showMsg(id, text) { const el = $(id); el.textContent = text; el.style.display = "block"; setTimeout(() => el.style.display = "none", 4000); }
function hideMsgs() { $("#msg-ok").style.display = "none"; $("#msg-err").style.display = "none"; }

async function checkDevice() {
  const code = $("#code").value.replace(/\D/g, "");
  if (code.length !== 8) { $("#status").innerHTML = '<span class="dot off"></span>输入 8 位设备口令'; return; }
  try {
    const r = await fetch(API + "/info?code=" + code);
    if (r.ok) { const d = await r.json(); $("#status").innerHTML = '<span class="dot on"></span>已连接：' + (d.name || "Clite Device"); }
    else { $("#status").innerHTML = '<span class="dot off"></span>设备未上线'; }
  } catch { $("#status").innerHTML = '<span class="dot off"></span>连接失败'; }
}

$("#code").addEventListener("input", checkDevice);
$("#code").addEventListener("change", checkDevice);
checkDevice();

async function doSend() {
  hideMsgs();
  const code = $("#code").value.replace(/\D/g, "");
  const text = $("#text").value.trim();
  if (code.length !== 8) return showMsg("#msg-err", "请输入 8 位设备口令");
  if (!text) return showMsg("#msg-err", "请输入喊话内容");
  const btn = $("#send"); btn.disabled = true; btn.textContent = "发送中…";
  try {
    const r = await fetch(API + "/send", { method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ code, text }) });
    const d = await r.json();
    if (r.ok) {
      showMsg("#msg-ok", "已发送 ✓"); $("#text").value = "";
      // 首次发送成功后把捐助卡高亮提醒一次（每浏览器一次）
      try {
        if (!localStorage.getItem("donateNudged")) {
          const d = $("#donate");
          d.classList.add("nudge");
          d.scrollIntoView({ behavior: "smooth", block: "nearest" });
          setTimeout(() => d.classList.remove("nudge"), 3500);
          localStorage.setItem("donateNudged", "1");
        }
      } catch { }
    }
    else showMsg("#msg-err", d.error || "发送失败");
  } catch { showMsg("#msg-err", "网络错误"); }
  finally { btn.disabled = false; btn.textContent = "📢 发送到大屏"; }
}

$("#send").addEventListener("click", doSend);
