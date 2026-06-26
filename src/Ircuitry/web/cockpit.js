"use strict";
// ircuitry cockpit - a thin WebSocket client of the control server (same /ws + protocol the desktop uses).
(function () {
  var el = function (id) { return document.getElementById(id); };
  var ws = null, token = localStorage.getItem("ircuitry.token") || "", reconnect = null, bots = [];

  // i18n: auto-detect Chinese from the browser (English default). Static text via data-i18n*, dynamic via T().
  var ZH = (navigator.language || "").toLowerCase().indexOf("zh") === 0;
  var I18N = {
    "cockpit": "驾驶舱", "Connect": "连接", "Access token": "访问令牌", "token": "令牌",
    "Open in app": "在应用中打开", "Open this server in the ircuitry desktop app": "在 ircuitry 桌面应用中打开此服务器",
    "Sign out": "退出登录", "Bots": "机器人", "Console": "控制台",
    "Enter the access token printed by <code>ircuitry --server</code>.": "输入 <code>ircuitry --server</code> 打印的访问令牌。",
    "offline": "离线", "connecting…": "连接中…", "rejected": "已拒绝", "invalid token": "无效令牌",
    "disconnected": "已断开", "error": "错误", "No bots in this workspace yet.": "此工作区还没有机器人。",
    "running": "运行中", "stopped": "已停止", "Stop": "停止", "Start": "启动", "nodes": "个节点"
  };
  function T(s) { return (ZH && I18N[s]) || s; }
  function applyI18n() {
    if (!ZH) return;
    document.documentElement.lang = "zh";
    document.querySelectorAll("[data-i18n]").forEach(function (n) { n.textContent = T(n.getAttribute("data-i18n")); });
    document.querySelectorAll("[data-i18n-html]").forEach(function (n) { n.innerHTML = T(n.getAttribute("data-i18n-html")); });
    document.querySelectorAll("[data-i18n-ph]").forEach(function (n) { n.placeholder = T(n.getAttribute("data-i18n-ph")); });
    document.querySelectorAll("[data-i18n-title]").forEach(function (n) { n.title = T(n.getAttribute("data-i18n-title")); });
  }

  function setConn(state, text) {
    var dot = el("dot");
    dot.className = "dot" + (state === "on" ? " on" : state === "bad" ? " bad" : "");
    el("connText").textContent = text;
  }
  function show(view) { el("login").hidden = view !== "login"; el("dash").hidden = view !== "dash"; }

  function connect() {
    if (!token) { show("login"); setConn("", T("offline")); return; }
    setConn("", T("connecting…"));
    var proto = location.protocol === "https:" ? "wss" : "ws";
    ws = new WebSocket(proto + "://" + location.host + "/ws");
    var ready = false;
    ws.onopen = function () { ws.send(JSON.stringify({ token: token })); };
    ws.onmessage = function (e) {
      var m; try { m = JSON.parse(e.data); } catch (x) { return; }
      if (!ready) {
        ready = true;
        if (m.evt === "hello" && m.ok) {
          localStorage.setItem("ircuitry.token", token);
          setConn("on", (m.server && m.server.name || "ircuitry") + (m.user ? " · " + m.user : ""));
          show("dash"); el("console").innerHTML = "";
          ws.send(JSON.stringify({ op: "subscribe", topics: ["logs", "status", "runs"] }));
          ws.send(JSON.stringify({ id: 1, op: "snapshot" }));
        } else {
          setConn("bad", T("rejected")); el("loginErr").textContent = (m && m.error) || T("invalid token");
          localStorage.removeItem("ircuitry.token"); token = ""; show("login"); try { ws.close(); } catch (x) {}
        }
        return;
      }
      handle(m);
    };
    ws.onclose = function () {
      if (!token) { return; }
      setConn("bad", T("disconnected")); scheduleReconnect();
    };
    ws.onerror = function () { setConn("bad", T("error")); };
  }

  function scheduleReconnect() { clearTimeout(reconnect); reconnect = setTimeout(connect, 2500); }

  function handle(m) {
    if (m.result && m.result.bots) { bots = m.result.bots; renderBots(); return; }
    if (m.evt === "log") { logLine(m); return; }
    if (m.evt === "run") { logLine({ bot: m.bot, level: "RUN", text: "ran " + m.trigger + " - " + (m.summary || "") }); return; }
    if (m.evt === "status") { ws.send(JSON.stringify({ id: 1, op: "snapshot" })); return; }
  }

  function renderBots() {
    var host = el("bots");
    if (!bots.length) { host.innerHTML = '<p class="muted">' + T("No bots in this workspace yet.") + '</p>'; return; }
    host.innerHTML = bots.map(function (b) {
      var live = b.running;
      return '<div class="bot' + (live ? " live" : "") + '">' +
        '<div class="name"><span class="sdot"></span>' + esc(b.name) + '</div>' +
        '<div class="meta">' + (live ? T("running") : T("stopped")) + " · " + esc(b.state || "") + " · " + b.nodes + " " + T("nodes") + "</div>" +
        '<button class="btn small ' + (live ? "stop" : "run") + '" data-bot="' + esc(b.name) + '" data-act="' + (live ? "stop" : "start") + '">' +
        (live ? T("Stop") : T("Start")) + "</button></div>";
    }).join("");
    host.querySelectorAll("[data-bot]").forEach(function (btn) {
      btn.addEventListener("click", function () {
        ws.send(JSON.stringify({ op: btn.getAttribute("data-act"), bot: btn.getAttribute("data-bot") }));
        setTimeout(function () { ws && ws.readyState === 1 && ws.send(JSON.stringify({ id: 1, op: "snapshot" })); }, 400);
      });
    });
  }

  function logLine(m) {
    var c = el("console"); var atBottom = c.scrollHeight - c.scrollTop - c.clientHeight < 40;
    var cls = m.level === "Error" || m.level === "Warn" ? "er" : m.level === "Out" ? "ou" : "lvl";
    var div = document.createElement("div"); div.className = "l";
    div.innerHTML = '<span class="' + cls + '">[' + esc(m.bot || "") + "] " + esc(m.time || "") + " " + esc(m.level || "") + "</span> " + esc(m.text || "");
    c.appendChild(div);
    while (c.childNodes.length > 800) c.removeChild(c.firstChild);
    if (atBottom) c.scrollTop = c.scrollHeight;
  }

  function esc(s) { return String(s == null ? "" : s).replace(/[&<>"]/g, function (ch) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch]; }); }

  el("loginBtn").addEventListener("click", function () { token = el("token").value.trim(); el("loginErr").textContent = ""; if (token) connect(); });
  el("token").addEventListener("keydown", function (e) { if (e.key === "Enter") el("loginBtn").click(); });
  el("logout").addEventListener("click", function () { localStorage.removeItem("ircuitry.token"); token = ""; try { ws.close(); } catch (x) {} show("login"); setConn("", T("offline")); });
  // hand this server off to the desktop app: ircuitry://connect?url=<origin>&token=<token>. Uses the full
  // https origin so the app dials wss; the desktop opens its Remote panel pre-filled and connects.
  el("openApp").addEventListener("click", function () {
    window.location.href = "ircuitry://connect?url=" + encodeURIComponent(location.origin) + "&token=" + encodeURIComponent(token);
  });

  if ("serviceWorker" in navigator) navigator.serviceWorker.register("sw.js").catch(function () {});
  applyI18n();
  connect();
})();
