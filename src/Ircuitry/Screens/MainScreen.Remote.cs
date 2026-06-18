using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Ircuitry.App;
using Ircuitry.App.Server;
using Ircuitry.Core;
using Ircuitry.Render;

namespace Ircuitry.Screens;

/// <summary>
/// Connect the desktop to a headless <c>ircuitry --server</c> (local or remote) and manage its bots: live status,
/// start/stop, a streaming console, and who else is connected. Uses the same control protocol as the cockpit.
/// (Live in-editor remote graph building rides on this session next; for now this is remote management.)
/// </summary>
public partial class MainScreen
{
    private bool _remoteOpen, _remoteJustOpened;
    private ControlClient? _remote;
    private string _rmUrl = "", _rmToken = "", _rmMsg = "";
    private bool _rmReplaceToken;   // user chose to replace an already-saved token
    private readonly List<(string label, string url, string tokenKey)> _rmSaved = new();

    public void OpenRemote()
    {
        _remoteOpen = true; _remoteJustOpened = true; _rmMsg = ""; _rmToken = ""; _rmReplaceToken = false;
        LoadServers();
        // the token is resolved from the key store at connect time - never pre-filled into the field
        if (_rmUrl.Length == 0 && _rmSaved.Count > 0) _rmUrl = _rmSaved[0].url;
    }

    /// <summary>Keep the remote session live every frame (drains its reply/event callbacks).</summary>
    private void RemotePump() => _remote?.Pump();

    private string ServersFile => Path.Combine(AppModel.WorkspaceDir, "servers.json");
    private static string ServerTokenKey(string url) => "server:" + url.Trim();
    private string SavedKeyFor(string url) { foreach (var s in _rmSaved) if (s.url == url.Trim()) return s.tokenKey ?? ""; return ""; }

    private void LoadServers()
    {
        _rmSaved.Clear();
        bool migrated = false;
        try
        {
            if (!File.Exists(ServersFile)) return;
            using var d = JsonDocument.Parse(File.ReadAllText(ServersFile));
            foreach (var e in d.RootElement.EnumerateArray())
            {
                string label = e.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                string url = e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string key = e.TryGetProperty("tokenKey", out var k) ? k.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(url)) { migrated = true; continue; }   // drop blank/junk entries and re-persist clean
                // legacy: a raw token was stored in servers.json - move it into the key store and scrub the file
                if (key.Length == 0 && e.TryGetProperty("token", out var t))
                {
                    string raw = t.GetString() ?? "";
                    key = ServerTokenKey(url);
                    if (raw.Length > 0) Ircuitry.Core.Secrets.Set(key, raw);
                    migrated = true;
                }
                _rmSaved.Add((label, url, key));
            }
        }
        catch { }
        if (migrated) PersistServers();   // rewrite servers.json with key references only
    }

    private void PersistServers()
    {
        try { File.WriteAllText(ServersFile, JsonSerializer.Serialize(_rmSaved.ConvertAll(s => new { label = s.label, url = s.url, tokenKey = s.tokenKey }), new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    private void SaveServer(string url, string token)
    {
        if (string.IsNullOrWhiteSpace(url)) return;   // never save a blank server
        string key = ServerTokenKey(url);
        if (token.Length > 0) Ircuitry.Core.Secrets.Set(key, token);   // the credential lives in the key store, never in servers.json
        _rmSaved.RemoveAll(s => s.url == url);
        _rmSaved.Insert(0, (url, url, key));
        PersistServers();
    }

    private void ConnectRemote()
    {
        string url = _rmUrl.Trim();
        string token = _rmToken.Trim();
        if (token.Length == 0) { var key = SavedKeyFor(url); if (key.Length > 0) token = Ircuitry.Core.Secrets.Get(key); }   // resolve from the stored key
        _remote?.Dispose();
        _remote = new ControlClient();
        _remote.Connect(url, token, url);
        _rmToken = ""; _rmReplaceToken = false;   // never retain the raw token in UI state
        _rmMsg = "";
    }

    private void DrawRemoteModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = 560, ph = 564;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Remote server", Theme.Sky);
        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 16;

        var rc = _remote;
        bool connected = rc != null && rc.Connected;

        if (connected)
        {
            Hud.SoftDot(r, new Vector2(x + 5, y + 7), 4.5f, Theme.Ok);
            r.Text(r.Fonts.Get(FontKind.Display, 15), rc!.ServerName, new Vector2(x + 18, y - 1), Theme.Text);
            r.Text(r.Fonts.Get(FontKind.Sans, 12), rc.User + "  ·  " + (rc.Peers.Length > 1 ? rc.Peers.Length + " online" : "just you"), new Vector2(x + 18, y + 16), Theme.TextDim);
            if (_ui.Button("rm.disc", new RectF(panel.Right - 22 - 110, y - 2, 110, 28), "Disconnect", Theme.Alert))
                DisconnectRemote();
            y += 40;

            r.Text(r.Fonts.Get(FontKind.SansBold, 12), "BOTS", new Vector2(x, y), Theme.TextDim); y += 20;
            var bots = rc.Bots;
            int shown = 0;
            foreach (var b in bots)
            {
                if (shown++ >= 6) break;
                var row = new RectF(x, y, w, 40);
                r.RoundFill(row, Theme.PanelHi, 10f); r.RoundOutline(row, Theme.Hairline, 10f);
                Hud.SoftDot(r, new Vector2(row.X + 14, row.Center.Y), 4f, b.Running ? Theme.Ok : Theme.Idle);
                r.Text(r.Fonts.Get(FontKind.SansBold, 13), b.Name, new Vector2(row.X + 28, row.Y + 6), Theme.Text);
                r.Text(r.Fonts.Get(FontKind.Sans, 11), (b.Running ? "running" : "stopped") + "  ·  " + b.Stat + "  ·  " + b.Nodes + " nodes", new Vector2(row.X + 28, row.Y + 23), Theme.TextDim);
                var act = new RectF(row.Right - 12 - 70, row.Center.Y - 13, 70, 26);
                if (b.CanEdit && _ui.Button("rm.ss." + b.Name, act, b.Running ? "Stop" : "Start", b.Running ? Theme.Alert : Theme.Ok, primary: !b.Running))
                { if (b.Running) rc.Stop(b.Name); else rc.Start(b.Name); }
                else if (!b.CanEdit) { r.RoundFill(act, Theme.WithAlpha(Theme.Idle, 0.14f), 8f); r.TextCentered(r.Fonts.Get(FontKind.Sans, 10), b.Running ? "running" : "stopped", act, Theme.TextFaint); }
                var ed = new RectF(act.X - 8 - 58, row.Center.Y - 13, 58, 26);
                if (_ui.Button("rm.ed." + b.Name, ed, b.CanEdit ? "Edit" : "View", b.CanEdit ? Theme.Sky : Theme.Idle))
                    OpenRemoteBotInEditor(rc, b.Name);

                // sharing chip: lock = private, globe = public (read), globe = shared (public + others can edit).
                // The owner (or an admin) can click to cycle it; everyone else sees it as a read-only badge.
                bool canShare = b.Mine || rc.User.Length > 0 && rc.Role == "admin";
                var (shIco, shLbl, shCol) = b.Private ? ("lock", "Private", Theme.Amber)
                    : b.Editable ? ("globe", "Shared", Theme.Ok)
                    : ("globe", "Public", Theme.Sky);
                var chip = new RectF(ed.X - 8 - 82, row.Center.Y - 13, 82, 26);
                string chipLbl = Ircuitry.Core.Icons.Glyph(shIco) + " " + shLbl;
                if (canShare)
                {
                    if (_ui.Button("rm.sh." + b.Name, chip, chipLbl, shCol))
                    {
                        if (b.Private) rc.SetAcl(b.Name, "public", false);          // private -> public (read only)
                        else if (!b.Editable) rc.SetAcl(b.Name, "public", true);    // public read -> shared (editable)
                        else rc.SetAcl(b.Name, "private", false);                   // shared -> private
                    }
                }
                else
                {
                    r.RoundFill(chip, Theme.WithAlpha(shCol, 0.16f), 8f);
                    r.TextCentered(r.Fonts.Get(FontKind.Sans, 11), chipLbl, chip, shCol);
                }
                y += 46;
            }
            if (bots.Count == 0) { r.Text(r.Fonts.Get(FontKind.Sans, 12), "No bots in this workspace.", new Vector2(x, y), Theme.TextFaint); y += 24; }

            y += 6;
            r.Text(r.Fonts.Get(FontKind.SansBold, 12), "CONSOLE", new Vector2(x, y), Theme.TextDim); y += 18;
            var box = new RectF(x, y, w, panel.Bottom - 58 - y);
            r.RoundFill(box, new Color(42, 36, 25), 10f);
            var lines = rc.RecentLog(12);
            float ly = box.Y + 8;
            var lf = r.Fonts.Get(FontKind.Mono, 11);
            for (int i = 0; i < lines.Length && ly < box.Bottom - 14; i++)
            { r.Text(lf, r.Ellipsize(lf, lines[i], w - 18), new Vector2(box.X + 10, ly), new Color(225, 215, 190)); ly += 16; }
        }
        else
        {
            r.Text(r.Fonts.Get(FontKind.Sans, 13), "Connect to a headless ircuitry --server to manage its bots.", new Vector2(x, y), Theme.TextDim); y += 26;

            if (_rmSaved.Count > 0)
            {
                r.Text(r.Fonts.Get(FontKind.SansBold, 12), "SAVED", new Vector2(x, y), Theme.TextDim); y += 20;
                float cx = x;
                int shown = 0;
                for (int i = 0; i < _rmSaved.Count && shown < 4; i++)
                {
                    var s = _rmSaved[i];
                    if (string.IsNullOrWhiteSpace(s.url)) continue;   // never render a blank chip
                    var bw = r.Fonts.Get(FontKind.SansBold, 12).MeasureString(s.url).X + 26;
                    if (_ui.Button("rm.saved" + i, new RectF(cx, y, bw, 28), s.url, Theme.Sky))
                    { _rmUrl = s.url; _rmToken = ""; _rmReplaceToken = false; }   // token comes from the key store, not here
                    cx += bw + 8; shown++;
                }
                y += 38;
            }

            r.Text(r.Fonts.Get(FontKind.SansBold, 12), "SERVER", new Vector2(x, y), Theme.TextDim); y += 18;
            _rmUrl = _ui.TextField("rm.url", new RectF(x, y, w, 34), _rmUrl, "mita:48700"); y += 44;
            r.Text(r.Fonts.Get(FontKind.SansBold, 12), "ACCESS TOKEN", new Vector2(x, y), Theme.TextDim); y += 18;
            string savedKey = SavedKeyFor(_rmUrl);
            bool hasSaved = savedKey.Length > 0 && Ircuitry.Core.Secrets.Has(savedKey);
            if (hasSaved && !_rmReplaceToken)
            {
                // a token is already saved as a stored key - show that, never the value
                var box = new RectF(x, y, w, 34);
                r.RoundFill(box, Theme.PanelHi, 8f); r.RoundOutline(box, Theme.Hairline, 8f);
                r.Text(r.Fonts.Get(FontKind.Sans, 13), Ircuitry.Core.Icons.Glyph("key") + "  token saved as a stored key", new Vector2(x + 12, box.Center.Y - 8), Theme.Ok);
                if (_ui.Button("rm.repl", new RectF(box.Right - 90, box.Y + 4, 84, 26), "Replace", Theme.Idle)) { _rmReplaceToken = true; _rmToken = ""; }
                y += 46;
            }
            else
            {
                _rmToken = _ui.TextField("rm.token", new RectF(x, y, w, 34), _rmToken, "paste access token", password: true); y += 46;
            }

            bool connecting = rc != null && rc.State == ControlClient.Conn.Connecting;
            if (_ui.Button("rm.connect", new RectF(x, y, 150, 36), connecting ? "Connecting…" : "Connect", Theme.Sky, primary: true, enabled: !connecting && _rmUrl.Trim().Length > 0))
            { SaveServer(_rmUrl.Trim(), _rmToken.Trim()); ConnectRemote(); }
            y += 46;

            string status = _rmMsg;
            if (rc != null && rc.State == ControlClient.Conn.Failed) status = "failed: " + rc.Error;
            else if (connecting) status = "connecting…";
            if (status.Length > 0) r.Text(r.Fonts.Get(FontKind.Sans, 12), r.Ellipsize(r.Fonts.Get(FontKind.Sans, 12), status, w), new Vector2(x, y), rc?.State == ControlClient.Conn.Failed ? Theme.Alert : Theme.TextDim);
        }

        if (_ui.Button("rm.close", new RectF(panel.Right - 22 - 100, panel.Bottom - 46, 100, 32), "DONE", Theme.Sky, primary: true))
            _remoteOpen = false;
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_remoteJustOpened) _remoteOpen = false;
        _remoteJustOpened = false;
    }

    // ---------------- remote editing: open a remote bot as a tab, push edits, mirror its log ----------------
    private long _remoteSig;
    private double _remotePushAt = -1;
    private Bot? _remoteLastBot;

    /// <summary>Open a bot living on a connected server as an editable tab. Edits push back to the server; its
    /// console + glow stream from the session. The tab bar doubles as your local/remote switcher.</summary>
    public void OpenRemoteBotInEditor(ControlClient session, string remoteName)
    {
        session.GetGraph(remoteName, json =>   // runs on the UI thread (Pump)
        {
            try
            {
                var (graph, _) = Ircuitry.Graph.GraphSerializer.Load(json);
                int existing = _app.Bots.FindIndex(b => b.IsRemote && b.Remote == session && b.RemoteName == remoteName);
                Bot bot;
                if (existing >= 0) { bot = _app.Bots[existing]; bot.Graph.ReplaceWith(graph); _app.Active = existing; }
                else
                {
                    bot = new Bot(remoteName + " @ " + session.Label) { Graph = graph, Remote = session, RemoteName = remoteName };
                    _app.Bots.Add(bot); _app.Active = _app.Bots.Count - 1;
                }
                // mirror the server's connection rows so the inspector shows (and can edit) the real settings
                var rb = session.Bots.FirstOrDefault(x => x.Name == remoteName);
                if (rb != null && rb.Servers.Count > 0)
                {
                    bot.Servers.Clear();
                    foreach (var s in rb.Servers)
                        bot.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = s.Label, Host = s.Host, Port = s.Port, UseTls = s.Tls, Nick = s.Nick, Channels = s.Channels, RealName = s.RealName, ConnectOnStartup = s.ConnectOnStartup });
                    bot.SelectedServer = 0;
                }
                HookRemoteLog(session);
                _remoteLastBot = null;     // force a fresh push baseline on the next tick (no spurious re-push)
                _remoteOpen = false;       // drop the modal, show the canvas
            }
            catch (System.Exception ex) { _rmMsg = "open failed: " + ex.Message; }
        });
    }

    /// <summary>Drop the remote session and close any tabs that were viewing its bots (they stay on the server).</summary>
    private void DisconnectRemote()
    {
        var s = _remote;
        if (s != null)
        {
            for (int i = _app.Bots.Count - 1; i >= 0; i--)
                if (_app.Bots[i].IsRemote && _app.Bots[i].Remote == s && _app.Bots.Count > 1) _app.RemoveBot(i);
            try { s.Disconnect(); s.Dispose(); } catch { }
        }
        _remote = null; _remoteLastBot = null; _rmMsg = "disconnected";
    }

    private void HookRemoteLog(ControlClient session)
    {
        session.OnLog = (botName, level, text) =>   // UI thread
        {
            var b = _app.Bots.Find(x => x.IsRemote && x.Remote == session && x.RemoteName == botName);
            if (b != null) b.Log.Add(System.Enum.TryParse<LogLevel>(level, out var lv) ? lv : LogLevel.System, text);
        };
    }

    // cheap change signature including node positions, so moves/edits all trigger a debounced push
    private static long RemoteSig(Graph.NodeGraph g)
    {
        long h = g.BehaviorSignature();
        foreach (var n in g.Nodes) h = unchecked(h * 31 + (long)n.Pos.X * 92821 + (long)n.Pos.Y * 53 + n.Id.GetHashCode());
        return h;
    }

    // signature of the selected server's connection settings, so editing host/port/nick/etc. triggers a push too
    private static long RemoteConnSig(Bot b)
    {
        var s = b.Settings;
        long h = 17;
        foreach (var v in new object[] { s.Host, s.Port, s.UseTls, s.Nick, s.Channels, s.RealName, s.SaslUser, s.SaslPass, s.ServerPass })
            h = unchecked(h * 31 + (v?.GetHashCode() ?? 0));
        return h;
    }

    private bool _autoOpenRemote;   // debug/--showremoteedit: open the first remote bot once the session is up
    private long _remoteConnSig;
    private bool _remoteGraphDirty, _remoteConnDirty;

    /// <summary>Each frame: if the active tab is a connected remote bot, debounce-push its graph AND its
    /// connection settings to the server.</summary>
    private void RemoteEditTick(Clock clock)
    {
        if (_autoOpenRemote && _remote?.Connected == true && _remote.Bots.Count > 0 && !Bot.IsRemote)
        { _autoOpenRemote = false; OpenRemoteBotInEditor(_remote, _remote.Bots[0].Name); }
        var b = Bot;
        if (b != _remoteLastBot)   // switched tabs: re-baseline, nothing dirty yet
        {
            _remoteLastBot = b; _remotePushAt = -1; _remoteGraphDirty = _remoteConnDirty = false;
            _remoteSig = b.IsRemote ? RemoteSig(b.Graph) : 0;
            _remoteConnSig = b.IsRemote ? RemoteConnSig(b) : 0;
        }
        if (!b.IsRemote || b.Remote?.Connected != true) return;
        long sig = RemoteSig(b.Graph), csig = RemoteConnSig(b);
        if (sig != _remoteSig) { _remoteSig = sig; _remoteGraphDirty = true; _remotePushAt = clock.Time + 0.8; }    // settle 0.8s after the last edit
        if (csig != _remoteConnSig) { _remoteConnSig = csig; _remoteConnDirty = true; _remotePushAt = clock.Time + 0.8; }
        if (_remotePushAt >= 0 && clock.Time >= _remotePushAt)
        {
            _remotePushAt = -1;
            try
            {
                if (_remoteGraphDirty) { _remoteGraphDirty = false; b.Remote!.PushGraph(b.RemoteName, Ircuitry.Graph.GraphSerializer.Save(b.Graph, b.RemoteName)); }
                if (_remoteConnDirty) { _remoteConnDirty = false; b.Remote!.PushConnection(b.RemoteName, b.Settings); }
            }
            catch { }
        }
    }

    // ---------------- collaborative editing: live cursors + soft node locks ----------------

    /// <summary>While editing a remote bot, tell the server where my cursor is (graph coords) and which node I'm
    /// holding, so co-editors see my presence and a soft lock on the node I'm working.</summary>
    private void RemoteCursorTick()
    {
        var b = Bot;
        if (!b.IsRemote || b.Remote?.Connected != true || Modal) return;
        if (!_l.Canvas.Contains(In.Mouse)) return;   // only while the cursor is over the shared canvas
        var w = _editor.Cam.ScreenToWorld(In.Mouse);
        // the node I'm holding: the one I'm dragging, or my single selection (focused for param edits)
        string held = (_editor.IsGrabbing || _editor.Selection.Count == 1) && _editor.Selection.Count > 0 ? _editor.Selection.First() : "";
        b.Remote!.SendCursor(b.RemoteName, w.X, w.Y, held);
    }

    /// <summary>Overlay co-editors' cursors and the soft locks they hold onto the active remote bot's canvas.</summary>
    private void DrawRemotePeers(Renderer r)
    {
        var b = Bot;
        if (!b.IsRemote || b.Remote == null) return;
        var peers = b.Remote.PeersOn(b.RemoteName);
        if (peers.Count == 0) return;
        r.Begin(BlendMode.Alpha, _l.Canvas.ToRectangle());
        foreach (var p in peers)
        {
            var (cr, cg, cb) = ControlClient.PeerColor(p.User);
            var col = new Color(cr, cg, cb);
            // soft lock: ring the node this peer is holding (advisory - you can still edit it)
            if (p.Node.Length > 0)
            {
                var node = b.Graph.Find(p.Node);
                if (node != null)
                {
                    var rect = _editor.NodeScreenRect(node);
                    r.RoundOutline(rect.Inflate(2.5f, 2.5f), col, 10f);
                    r.RoundOutline(rect.Inflate(4.5f, 4.5f), Theme.WithAlpha(col, 0.35f), 11f);
                }
            }
            // the cursor + a name pill
            var sp = _editor.Cam.WorldToScreen(new Vector2(p.X, p.Y));
            if (!_l.Canvas.Contains(sp)) continue;
            Hud.SoftDot(r, sp, 5.5f, col);
            r.Disc(sp, 2.6f, Color.White);
            var f = r.Fonts.Get(FontKind.SansBold, 11);
            var m = f.MeasureString(p.User);
            var pill = new RectF(sp.X + 9, sp.Y + 7, m.X + 12, m.Y + 5);
            r.RoundFill(pill, col, 6f);
            r.Text(f, p.User, new Vector2(pill.X + 6, pill.Y + 2), Theme.TextInk);
        }
        r.End();
    }

    public void DebugOpenRemote()
    {
        _l = Layout.Compute(_vw, _vh, _consoleH); OpenRemote();
        var u = System.Environment.GetEnvironmentVariable("IRCUITRY_REMOTE_URL");
        var t = System.Environment.GetEnvironmentVariable("IRCUITRY_REMOTE_TOKEN");
        if (!string.IsNullOrEmpty(u)) { _rmUrl = u; _rmToken = t ?? ""; ConnectRemote(); }   // headless connected-view check
    }

    /// <summary>--showremoteedit: connect (env URL/token) and open the first remote bot straight into the editor,
    /// so the remote canvas (with co-editor cursors) can be captured headlessly.</summary>
    public void DebugOpenRemoteEdit() { DebugOpenRemote(); _autoOpenRemote = true; }
}
