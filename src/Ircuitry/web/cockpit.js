"use strict";
// ircuitry cockpit - a thin WebSocket client of the control server (same /ws + protocol the desktop uses).
(function () {
  var el = function (id) { return document.getElementById(id); };
  var ws = null, token = localStorage.getItem("ircuitry.token") || "", reconnect = null, bots = [];

  function setConn(state, text) {
    var dot = el("dot");
    dot.className = "dot" + (state === "on" ? " on" : state === "bad" ? " bad" : "");
    el("connText").textContent = text;
  }
  function show(view) { el("login").hidden = view !== "login"; el("dash").hidden = view !== "dash"; }

  function connect() {
    if (!token) { show("login"); setConn("", "offline"); return; }
    setConn("", "connecting…");
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
          setConn("bad", "rejected"); el("loginErr").textContent = (m && m.error) || "invalid token";
          localStorage.removeItem("ircuitry.token"); token = ""; show("login"); try { ws.close(); } catch (x) {}
        }
        return;
      }
      handle(m);
    };
    ws.onclose = function () {
      if (!token) { return; }
      setConn("bad", "disconnected"); scheduleReconnect();
    };
    ws.onerror = function () { setConn("bad", "error"); };
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
    if (!bots.length) { host.innerHTML = '<p class="muted">No bots in this workspace yet.</p>'; return; }
    host.innerHTML = bots.map(function (b) {
      var live = b.running;
      return '<div class="bot' + (live ? " live" : "") + '">' +
        '<div class="name"><span class="sdot"></span>' + esc(b.name) + '</div>' +
        '<div class="meta">' + (live ? "running" : "stopped") + " · " + esc(b.state || "") + " · " + b.nodes + " nodes</div>" +
        '<button class="btn small ' + (live ? "stop" : "run") + '" data-bot="' + esc(b.name) + '" data-act="' + (live ? "stop" : "start") + '">' +
        (live ? "Stop" : "Start") + "</button></div>";
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
  el("logout").addEventListener("click", function () { localStorage.removeItem("ircuitry.token"); token = ""; try { ws.close(); } catch (x) {} show("login"); setConn("", "offline"); });

  if ("serviceWorker" in navigator) navigator.serviceWorker.register("sw.js").catch(function () {});
  connect();
})();
