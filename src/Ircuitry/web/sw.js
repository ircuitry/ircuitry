// Minimal service worker: cache the app shell so the cockpit installs and opens offline.
// Live data is always fetched over the WebSocket, never cached.
var SHELL = "ircuitry-cockpit-v1";
var FILES = ["./", "index.html", "cockpit.css", "cockpit.js", "manifest.webmanifest", "icon.png"];

self.addEventListener("install", function (e) {
  e.waitUntil(caches.open(SHELL).then(function (c) { return c.addAll(FILES).catch(function () {}); }).then(function () { return self.skipWaiting(); }));
});
self.addEventListener("activate", function (e) {
  e.waitUntil(caches.keys().then(function (keys) {
    return Promise.all(keys.filter(function (k) { return k !== SHELL; }).map(function (k) { return caches.delete(k); }));
  }).then(function () { return self.clients.claim(); }));
});
self.addEventListener("fetch", function (e) {
  var url = new URL(e.request.url);
  if (url.pathname.indexOf("/ws") === 0 || e.request.method !== "GET") return;   // never intercept the socket
  e.respondWith(
    fetch(e.request).then(function (r) {
      var copy = r.clone(); caches.open(SHELL).then(function (c) { c.put(e.request, copy).catch(function () {}); });
      return r;
    }).catch(function () { return caches.match(e.request).then(function (m) { return m || caches.match("index.html"); }); })
  );
});
