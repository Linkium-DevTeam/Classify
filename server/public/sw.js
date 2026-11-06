/* Classify WebUI Service Worker — 外壳缓存 + API 直通 */
const CACHE = "cc-shell-v13";
const SHELL = [
  "/",
  "/app",
  "/admin",
  "/lite",
  "/css/app.css",
  "/css/welcome.css",
  "/js/core.js",
  "/js/compose.js",
  "/js/classroom.js",
  "/js/settings.js",
  "/js/boot.js",
  "/js/welcome.js",
  "/js/admin.js",
  "/lite/lite.css",
  "/lite/lite.js",
  "/manifest.webmanifest",
  "/icons/icon-192.png",
  "/icons/icon-512.png",
  "/icons/maskable-512.png",
  "/icons/favicon-32.png",
];

self.addEventListener("install", (e) => {
  // 逐个 add：任一静态资源 404/部署缺失不应让整个 Service Worker 安装失败
  e.waitUntil(
    caches.open(CACHE).then(async (c) => {
      await Promise.all(SHELL.map((url) => c.add(url).catch(() => {})));
      return self.skipWaiting();
    })
  );
});

self.addEventListener("activate", (e) => {
  e.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))
    ).then(() => self.clients.claim())
  );
});

/** 导航请求的缓存键：只接管应用外壳，下载等其它导航交给浏览器 */
function navKey(pathname) {
  if (pathname === "/") return "/";
  if (pathname === "/app" || pathname === "/app/") return "/app";
  if (pathname === "/admin" || pathname === "/admin/") return "/admin";
  if (pathname === "/lite" || pathname.startsWith("/lite/")) return "/lite";
  return null;
}

self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  // 只处理同源请求；API 一律直连，不缓存
  if (url.origin !== location.origin) return;
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/lite/api/")) return;
  if (url.pathname.startsWith("/download/")) return;
  if (e.request.method !== "GET") return;

  // 导航请求：/app、/lite、/ 网络优先，失败回退各自外壳（离线可打开）
  if (e.request.mode === "navigate") {
    const key = navKey(url.pathname);
    if (!key) return; // 非外壳页面（安装包下载等）不拦截
    e.respondWith(
      fetch(e.request)
        .then((r) => {
          if (r.ok) {
            const copy = r.clone();
            caches.open(CACHE).then((c) => c.put(key, copy));
          }
          return r;
        })
        .catch(() =>
          caches.match(key).then((hit) => hit || caches.match("/app"))
        )
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
