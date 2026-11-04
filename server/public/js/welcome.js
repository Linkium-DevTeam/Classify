/* Classify 官网 — 版本号从 /api/version 读取，填充 [data-app-version]（不硬编码） */
(function () {
  "use strict";
  var appNodes = document.querySelectorAll("[data-app-version]");
  var liteNodes = document.querySelectorAll("[data-lite-version]");
  if (!appNodes.length && !liteNodes.length) return;
  fetch("/api/version", { cache: "no-store" })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (d) {
      if (!d) return;
      var app = d.version ? "V" + String(d.version).replace(/^v/i, "") : "";
      var lite = d.liteVersion ? "V" + String(d.liteVersion).replace(/^v/i, "") : "";
      if (app) appNodes.forEach(function (el) { el.textContent = app; });
      if (lite) liteNodes.forEach(function (el) { el.textContent = lite; });
    })
    .catch(function () { /* 离线时保留 V— 占位 */ });
})();
