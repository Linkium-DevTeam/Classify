/* Classify WebUI Service Worker — 外壳缓存 + API 直通 */
const CACHE = "cc-shell-v11";
const SHELL = [
  "/app",
  "/admin",
  "/css/app.css",
  "/js/app.js",
  "/js/admin.js",
  "/manifest.webmanifest",
  "/icons/icon-192.png",
  "/icons/icon-512.png",
  "/icons/maskable-512.png",
  "/icons/favicon-32.png",
];

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (e) => {
  e.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))
    ).then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/lite/api/")) return; // API 一律直连，不缓存
  if (e.request.method !== "GET") return;

  // 导航请求：网络优先，失败回退外壳（离线可打开）
  if (e.request.mode === "navigate") {
    e.respondWith(
      fetch(e.request)
        .then((r) => {
          const copy = r.clone();
          caches.open(CACHE).then((c) => c.put("/app", copy));
          return r;
        })
        .catch(() => caches.match("/app"))
    );
    return;
  }

  // 页面外壳与 JS/CSS：网络优先——前端活跃迭代，网络失败才回退缓存
  // （此前缓存优先导致手机端长期吃旧版本）
  const isShell =
    url.pathname === "/app" || url.pathname === "/lite" ||
    url.pathname.endsWith(".js") ||
    url.pathname.endsWith(".css") ||
    url.pathname.endsWith(".html") ||
    url.pathname.endsWith(".webmanifest");
  if (isShell) {
    e.respondWith(
      fetch(e.request)
        .then((r) => {
          if (r.ok) {
            const copy = r.clone();
            caches.open(CACHE).then((c) => c.put(e.request, copy));
          }
          return r;
        })
        .catch(() => caches.match(e.request))
    );
    return;
  }

  // 图标等极少变化资源：缓存优先，后台更新
  e.respondWith(
    caches.match(e.request).then((hit) => {
      const fetching = fetch(e.request)
        .then((r) => {
          if (r.ok) {
            const copy = r.clone();
            caches.open(CACHE).then((c) => c.put(e.request, copy));
          }
          return r;
        })
        .catch(() => hit);
      return hit || fetching;
    })
  );
});
