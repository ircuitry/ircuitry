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

    /// <summary>The server's status for the open bot when it's remotely hosted (fleet-health + live IRC metrics),
    /// or null for a local bot - so per-bot gauges show the server's real numbers, not this desktop's zeros.</summary>
    private ControlClient.RemoteBot? RemBot() => Bot.IsRemote ? Bot.Remote?.BotInfo(Bot.Name) : null;

    /// <summary>Handle an <c>ircuitry://connect</c> deep link (the cockpit's "Open in desktop app" button):
    /// pop the Remote panel pre-filled with this server and, if a token came along, connect straight away.</summary>
    private void HandleConnectLink(string link)
    {
        if (!Ircuitry.App.DeepLink.TryParseConnect(link, out var url, out var token))
        { Bot.Log.Add(LogLevel.Error, "unrecognised connect link: " + link); return; }
        OpenRemoteFromLink(url, token);
    }

    /// <summary>Open the Remote server panel for a given URL (+ optional token) and connect.</summary>
    public void OpenRemoteFromLink(string url, string token)
    {
        OpenRemote();                       // show the panel + load saved servers
        _rmUrl = url.Trim(); _rmReplaceToken = false;
        if (token.Trim().Length > 0)
        {
            _rmToken = token.Trim();
            SaveServer(_rmUrl, _rmToken);   // remember it (token -> key store), then connect
            ConnectRemote();
            Notify(Ircuitry.Core.Icons.Glyph("cloud") + " connecting to " + _rmUrl);
        }
        else { _rmToken = ""; _rmMsg = "paste the access token to connect"; }
    }

    // remote control-plane servers - kept in their OWN file. (servers.json belongs to the IRC server-profile
    // store, Core/Servers.cs; an older build wrongly shared it, which clobbered users' saved IRC servers.)
    private string ServersFile => Path.Combine(AppModel.WorkspaceDir, "remote-servers.json");
    private string LegacyServersFile => Path.Combine(AppModel.WorkspaceDir, "servers.json");
    private static string ServerTokenKey(string url) => "server:" + url.Trim();
    private string SavedKeyFor(string url) { foreach (var s in _rmSaved) if (s.url == url.Trim()) return s.tokenKey ?? ""; return ""; }

    private void LoadServers()
    {
        _rmSaved.Clear();
        bool dirty = false;
        try
        {
            string path = ServersFile;
            if (!File.Exists(path))
            {
                if (!File.Exists(LegacyServersFile)) return;
                path = LegacyServersFile; dirty = true;   // one-time rescue of remote entries from the old shared file
            }
            using var d = JsonDocument.Parse(File.ReadAllText(path));
            if (d.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var e in d.RootElement.EnumerateArray())
            {
                // only remote-shaped (url-bearing) entries are ours; IRC server profiles in the legacy file are ignored
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("url", out var u)) continue;
                string url = u.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(url)) { dirty = true; continue; }
                string label = e.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                string key = e.TryGetProperty("tokenKey", out var k) ? k.GetString() ?? "" : "";
                if (key.Length == 0 && e.TryGetProperty("token", out var t))   // legacy raw token -> move into the key store
                {
                    string raw = t.GetString() ?? "";
                    key = ServerTokenKey(url);
                    if (raw.Length > 0) Ircuitry.Core.Secrets.Set(key, raw);
                    dirty = true;
                }
                _rmSaved.Add((label, url, key));
            }
        }
        catch { }
        if (dirty) PersistServers();   // writes remote-servers.json only - never touches servers.json
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

    private enum RmView { Bots, Vault, Tokens, Info }
    private RmView _rmView;
    private string[] _vaultNames = System.Array.Empty<string>();
    private ControlClient.TokenLine[] _tokenList = System.Array.Empty<ControlClient.TokenLine>();
    private ControlClient.ServerInfo? _serverInfo;
    private string _vaultNewName = "", _vaultNewVal = "", _tokUser = "", _tokRole = "editor", _botMenu = "", _newBotName = "";
    private string _renameRemote = "", _renameVal = "";
    private string _lastMint = "";

    private void SwitchRmView(RmView v)
    {
        _rmView = v; _botMenu = "";
        var rc = _remote; if (rc == null) return;
        if (v == RmView.Vault) rc.ListSecrets(n => _vaultNames = n);
        else if (v == RmView.Tokens) rc.Tokens(t => _tokenList = t);
        else if (v == RmView.Info) rc.Info(i => _serverInfo = i);
    }

    private void DrawRemoteModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = 580, ph = 588;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Remote server", Theme.Sky);
        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 16;

        var rc = _remote;
        bool connected = rc != null && rc.Connected;

        if (connected)
        {
            Hud.SoftDot(r, new Vector2(x + 5, y + 7), 4.5f, Theme.Ok);
            r.Text(r.Fonts.Get(FontKind.Display, 15), rc!.ServerName, new Vector2(x + 18, y - 1), Theme.Text);
            r.Text(r.Fonts.Get(FontKind.Sans, 12), rc.User + "  ·  " + rc.Role + "  ·  " + (rc.Peers.Length > 1 ? rc.Peers.Length + " online" : "just you"), new Vector2(x + 18, y + 16), Theme.TextDim);
            if (_ui.Button("rm.disc", new RectF(panel.Right - 22 - 110, y - 2, 110, 28), "Disconnect", Theme.Alert))
                DisconnectRemote();
            y += 40;

            // view tabs
            string[] tabs = { "Bots", "Vault", "Info" };
            bool admin = rc.Role == "admin";
            var tabList = admin ? new[] { "Bots", "Vault", "Tokens", "Info" } : tabs;
            float tx = x;
            foreach (var t in tabList)
            {
                var tv = System.Enum.Parse<RmView>(t);
                bool sel = _rmView == tv;
                var tr = new RectF(tx, y, 78, 28);
                if (_ui.Button("rm.tab." + t, tr, t, sel ? Theme.Sky : Theme.Idle, primary: sel)) SwitchRmView(tv);
                tx += 84;
            }
            y += 38;

            if (_rmView == RmView.Vault) DrawVaultView(r, panel, x, w, ref y, rc);
            else if (_rmView == RmView.Tokens && admin) DrawTokensView(r, panel, x, w, ref y, rc);
            else if (_rmView == RmView.Info) DrawInfoView(r, panel, x, w, ref y, rc);
            else DrawBotsView(r, panel, x, w, ref y, rc);
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
                    var bw = r.Fonts.Get(FontKind.SansBold, 12).MeasureString(Ircuitry.Render.Renderer.SafeText(s.url)).X + 26;
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

    // ---------------- Bots view: list, create, edit, pull, rename, delete, push-local ----------------
    private void DrawBotsView(Renderer r, RectF panel, float x, float w, ref float y, ControlClient rc)
    {
        bool canCreate = rc.Role is "admin" or "editor";
        _newBotName = _ui.TextField("rm.newbot", new RectF(x, y, w - 92, 30), _newBotName, "new circuit name");
        if (_ui.Button("rm.newbtn", new RectF(panel.Right - 22 - 82, y, 82, 30), Ircuitry.Core.Icons.Glyph("plus") + " New", Theme.Ok, primary: true, enabled: canCreate && _newBotName.Trim().Length > 0))
        { var nm = _newBotName.Trim(); _newBotName = ""; rc.CreateBot(nm, _ => { rc.Snapshot(); OpenRemoteBotInEditor(rc, nm); }); }
        y += 40;

        var bf = r.Fonts.Get(FontKind.SansBold, 12);
        r.Text(bf, "SERVER BOTS", new Vector2(x, y), Theme.TextDim); y += 18;
        var bots = rc.Bots; int shown = 0;
        foreach (var b in bots)
        {
            if (shown++ >= 5) break;
            var row = new RectF(x, y, w, 40);
            r.RoundFill(row, Theme.PanelHi, 10f); r.RoundOutline(row, Theme.Hairline, 10f);
            if (_renameRemote == b.Name)
            {
                _renameVal = _ui.TextField("rm.rn", new RectF(row.X + 10, row.Center.Y - 13, w - 10 - 150, 26), _renameVal, "name");
                if (_ui.Button("rm.rn.ok", new RectF(row.Right - 12 - 70, row.Center.Y - 13, 70, 26), "Save", Theme.Ok, primary: true) && _renameVal.Trim().Length > 0)
                { rc.RenameBot(b.Name, _renameVal.Trim(), () => rc.Snapshot()); _renameRemote = ""; }
                if (_ui.Button("rm.rn.x", new RectF(row.Right - 12 - 70 - 8 - 60, row.Center.Y - 13, 60, 26), "Cancel", Theme.Idle)) _renameRemote = "";
                y += 46; continue;
            }
            Hud.SoftDot(r, new Vector2(row.X + 14, row.Center.Y), 4f, b.Running ? Theme.Ok : Theme.Idle);
            r.Text(r.Fonts.Get(FontKind.SansBold, 13), b.Name, new Vector2(row.X + 28, row.Y + 6), Theme.Text);
            string tag = b.Private ? Ircuitry.Core.Icons.Glyph("lock") + " private" : b.Editable ? "shared" : "public";
            r.Text(r.Fonts.Get(FontKind.Sans, 11), (b.Running ? "running" : "stopped") + "  ·  " + b.Nodes + " nodes  ·  " + tag, new Vector2(row.X + 28, row.Y + 23), Theme.TextDim);

            if (_botMenu == b.Name)   // ... action strip: pull / rename / delete
            {
                bool owns = b.Mine || rc.Role == "admin";
                var del = new RectF(row.Right - 12 - 58, row.Center.Y - 13, 58, 26);
                if (owns && _ui.Button("rm.del." + b.Name, del, "Delete", Theme.Alert)) { rc.DeleteBot(b.Name, () => rc.Snapshot()); _botMenu = ""; }
                var ren = new RectF(del.X - 8 - 62, row.Center.Y - 13, 62, 26);
                if (b.CanEdit && _ui.Button("rm.ren." + b.Name, ren, "Rename", Theme.Sky)) { _renameRemote = b.Name; _renameVal = b.Name; _botMenu = ""; }
                var pull = new RectF(ren.X - 8 - 52, row.Center.Y - 13, 52, 26);
                if (_ui.Button("rm.pull." + b.Name, pull, "Pull", Theme.Idle)) { PullRemoteBot(rc, b.Name); _botMenu = ""; }
                var close = new RectF(pull.X - 6 - 24, row.Center.Y - 13, 24, 26);
                if (_ui.Button("rm.menux." + b.Name, close, "×", Theme.Idle)) _botMenu = "";
            }
            else
            {
                var act = new RectF(row.Right - 12 - 62, row.Center.Y - 13, 62, 26);
                if (b.CanEdit && _ui.Button("rm.ss." + b.Name, act, b.Running ? "Stop" : "Start", b.Running ? Theme.Alert : Theme.Ok, primary: !b.Running))
                { if (b.Running) rc.Stop(b.Name); else rc.Start(b.Name); }
                else if (!b.CanEdit) { r.RoundFill(act, Theme.WithAlpha(Theme.Idle, 0.14f), 8f); r.TextCentered(r.Fonts.Get(FontKind.Sans, 10), b.Running ? "run" : "idle", act, Theme.TextFaint); }
                var ed = new RectF(act.X - 8 - 54, row.Center.Y - 13, 54, 26);
                if (_ui.Button("rm.ed." + b.Name, ed, b.CanEdit ? "Edit" : "View", b.CanEdit ? Theme.Sky : Theme.Idle)) OpenRemoteBotInEditor(rc, b.Name);
                var more = new RectF(ed.X - 6 - 26, row.Center.Y - 13, 26, 26);
                if (_ui.Button("rm.more." + b.Name, more, Ircuitry.Core.Icons.Glyph("dots-three"), Theme.Idle)) _botMenu = b.Name;
            }
            y += 46;
        }
        if (bots.Count == 0) { r.Text(r.Fonts.Get(FontKind.Sans, 12), "No bots you can see here.", new Vector2(x, y), Theme.TextFaint); y += 22; }

        // push a local workflow up to the server
        var locals = _app.Bots.Where(lb => !lb.IsRemote).ToList();
        if (canCreate && locals.Count > 0)
        {
            y += 4; r.Text(bf, Ircuitry.Core.Icons.Glyph("upload-simple") + " PUSH A LOCAL BOT TO THE SERVER", new Vector2(x, y), Theme.TextDim); y += 18;
            float px = x;
            int ls = 0;
            foreach (var lb in locals)
            {
                if (ls++ >= 4) break;
                var sz = r.Fonts.Get(FontKind.SansBold, 12).MeasureString(Ircuitry.Render.Renderer.SafeText(lb.Name)).X + 30;
                if (px + sz > x + w) { px = x; y += 32; }
                if (_ui.Button("rm.push." + lb.Name, new RectF(px, y, sz, 28), Ircuitry.Core.Icons.Glyph("upload-simple") + " " + lb.Name, Theme.Sky))
                    PushLocalBot(lb, rc);
                px += sz + 8;
            }
            y += 34;
        }
    }

    /// <summary>Copy a server bot down into this workspace as an ordinary local bot.</summary>
    private void PullRemoteBot(ControlClient session, string remoteName)
    {
        session.GetGraph(remoteName, json =>
        {
            try
            {
                var (graph, _) = Ircuitry.Graph.GraphSerializer.Load(json);
                string nm = remoteName; for (int k = 2; _app.Bots.Any(x => x.Name == nm); k++) nm = remoteName + " " + k;
                var bot = new Bot(nm) { Graph = graph };
                var rb = session.Bots.FirstOrDefault(x => x.Name == remoteName);
                if (rb != null)
                {
                    if (rb.Servers.Count > 0) { bot.Servers.Clear(); foreach (var s in rb.Servers) bot.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = s.Label, Host = s.Host, Port = s.Port, UseTls = s.Tls, Nick = s.Nick, Channels = s.Channels, RealName = s.RealName, ConnectOnStartup = s.ConnectOnStartup }); }
                    foreach (var kv in rb.Vars) bot.State[kv.Key] = kv.Value;
                }
                _app.Bots.Add(bot); _app.Active = _app.Bots.Count - 1; _app.MarkDirty();
                Notify(Ircuitry.Core.Icons.Glyph("download-simple") + " pulled " + remoteName + " into your workspace");
            }
            catch (System.Exception ex) { Bot.Log.Add(LogLevel.Error, "pull failed: " + ex.Message); }
        });
    }

    /// <summary>Publish a local bot to the connected server (create or overwrite same-name), then offer to copy
    /// any {{secret.X}} it needs that the server's vault is missing.</summary>
    private void PushLocalBot(Bot local, ControlClient rc)
    {
        string nm = local.Name;
        var existing = rc.Bots.FirstOrDefault(b => b.Name == nm);
        long baseRev = existing?.Rev ?? 0;
        void Push()
        {
            rc.PushGraph(nm, Ircuitry.Graph.GraphSerializer.Save(local.Graph, nm), baseRev, (rev, stale) => { if (stale) Notify("server already has a newer " + nm + " - open it to merge"); });
            rc.PushServers(nm, local.Servers);
            rc.PushState(nm, new Dictionary<string, string>(local.State));
            OfferSecretCopy(rc, local);
            rc.Snapshot();
            Notify(Ircuitry.Core.Icons.Glyph("upload-simple") + " pushed " + nm + " to the server");
        }
        if (existing == null) rc.CreateBot(nm, _ => Push());
        else Push();
    }

    private static readonly System.Text.RegularExpressions.Regex _secretRef = new(@"\{\{\s*secret\.([^}\s]+)\s*\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private List<string> _secretCopy = new(); private ControlClient? _secretCopySession;

    /// <summary>After a push, find the {{secret.X}} keys the bot needs that the server lacks but we have, and
    /// offer to copy their values up.</summary>
    private void OfferSecretCopy(ControlClient rc, Bot local)
    {
        var refs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        void Scan(string v) { foreach (System.Text.RegularExpressions.Match m in _secretRef.Matches(v ?? "")) refs.Add(m.Groups[1].Value); }
        foreach (var n in local.Graph.Nodes) foreach (var p in n.Params) Scan(p.Value);
        foreach (var s in local.Servers) { Scan(s.SaslUser); Scan(s.SaslPass); Scan(s.ServerPass); Scan(s.ClientCertPass); }
        var mine = refs.Where(Ircuitry.Core.Secrets.Has).ToList();   // only ones we can actually supply
        if (mine.Count == 0) return;
        rc.ListSecrets(serverNames =>
        {
            var have = new HashSet<string>(serverNames, System.StringComparer.OrdinalIgnoreCase);
            var missing = mine.Where(n => !have.Contains(n)).ToList();
            if (missing.Count > 0) { _secretCopy = missing; _secretCopySession = rc; }
        });
    }

    private void DrawSecretCopyModal(Renderer r)
    {
        var rc = _secretCopySession; if (rc == null || _secretCopy.Count == 0) { _secretCopy.Clear(); return; }
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = 460, ph = 130 + Math.Min(6, _secretCopy.Count) * 20 + 60;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Copy keys to the server?", Theme.Violet);
        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 14;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 13), "This circuit uses keys the server's vault doesn't have. Copy their values up (over the encrypted connection) so it can run there?", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 13), line, new Vector2(x, y), Theme.TextDim); y += 18; }
        y += 4;
        for (int i = 0; i < _secretCopy.Count && i < 6; i++) { r.Text(r.Fonts.Get(FontKind.SansBold, 12), Ircuitry.Core.Icons.Glyph("key") + "  " + _secretCopy[i], new Vector2(x + 6, y), Theme.Lime); y += 20; }
        var copy = new RectF(x, panel.Bottom - 50, 150, 34);
        var skip = new RectF(panel.Right - 22 - 90, panel.Bottom - 50, 90, 34);
        if (_ui.Button("sc.copy", copy, "Copy " + _secretCopy.Count + " key(s)", Theme.Lime, primary: true))
        { foreach (var n in _secretCopy) rc.SetSecret(n, Ircuitry.Core.Secrets.Get(n)); Notify("copied " + _secretCopy.Count + " key(s) to the server vault"); _secretCopy.Clear(); _secretCopySession = null; }
        if (_ui.Button("sc.skip", skip, "Skip", Theme.Idle)) { _secretCopy.Clear(); _secretCopySession = null; }
        r.End();
    }

    // ---------------- Vault view: the server's stored credentials ----------------
    private void DrawVaultView(Renderer r, RectF panel, float x, float w, ref float y, ControlClient rc)
    {
        bool canEdit = rc.Role is "admin" or "editor";
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 12), "The server's stored keys, referenced as {{secret.NAME}} by its bots. Values are write-only - they're never sent back.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 12), line, new Vector2(x, y), Theme.TextDim); y += 16; }
        y += 6;
        if (canEdit)
        {
            _vaultNewName = _ui.TextField("rm.vname", new RectF(x, y, 150, 30), _vaultNewName, "name e.g. openai");
            _vaultNewVal = _ui.TextField("rm.vval", new RectF(x + 158, y, w - 158 - 78, 30), _vaultNewVal, "value", password: true);
            if (_ui.Button("rm.vadd", new RectF(panel.Right - 22 - 70, y, 70, 30), "Add", Theme.Ok, primary: true) && _vaultNewName.Trim().Length > 0)
            { rc.SetSecret(_vaultNewName.Trim(), _vaultNewVal, () => rc.ListSecrets(n => _vaultNames = n)); _vaultNewName = ""; _vaultNewVal = ""; }
            y += 40;
        }
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), _vaultNames.Length + " KEY(S)", new Vector2(x, y), Theme.TextDim); y += 20;
        int shown = 0;
        foreach (var name in _vaultNames)
        {
            if (shown++ >= 9) break;
            var row = new RectF(x, y, w, 32);
            r.RoundFill(row, Theme.PanelHi, 8f);
            r.Text(r.Fonts.Get(FontKind.SansBold, 12), Ircuitry.Core.Icons.Glyph("key") + "  " + name, new Vector2(row.X + 10, row.Center.Y - 8), Theme.Text);
            if (canEdit && _ui.Button("rm.vdel." + name, new RectF(row.Right - 8 - 62, row.Center.Y - 12, 62, 24), "Delete", Theme.Alert))
                rc.DeleteSecret(name, () => rc.ListSecrets(n => _vaultNames = n));
            y += 36;
        }
        if (_vaultNames.Length == 0) { r.Text(r.Fonts.Get(FontKind.Sans, 12), "No keys on the server yet.", new Vector2(x, y), Theme.TextFaint); y += 20; }
    }

    // ---------------- Tokens view (admin): access tokens ----------------
    private void DrawTokensView(Renderer r, RectF panel, float x, float w, ref float y, ControlClient rc)
    {
        _tokUser = _ui.TextField("rm.tu", new RectF(x, y, 150, 30), _tokUser, "user name");
        if (_ui.Button("rm.trole", new RectF(x + 158, y, 90, 30), _tokRole, Theme.Idle))
            _tokRole = _tokRole == "viewer" ? "editor" : _tokRole == "editor" ? "admin" : "viewer";
        if (_ui.Button("rm.tmint", new RectF(panel.Right - 22 - 80, y, 80, 30), "Mint", Theme.Ok, primary: true) && _tokUser.Trim().Length > 0)
        { rc.MintToken(_tokUser.Trim(), _tokRole, tk => { _lastMint = tk; rc.Tokens(t => _tokenList = t); }); _tokUser = ""; }
        y += 38;
        if (_lastMint.Length > 0)
        {
            var box = new RectF(x, y, w, 34); r.RoundFill(box, Theme.WithAlpha(Theme.Ok, 0.16f), 8f);
            r.Text(r.Fonts.Get(FontKind.Mono, 12), r.Ellipsize(r.Fonts.Get(FontKind.Mono, 12), _lastMint, w - 150), new Vector2(box.X + 10, box.Center.Y - 8), Theme.Text);
            if (_ui.Button("rm.tmcopy", new RectF(box.Right - 8 - 120, box.Y + 5, 120, 24), "Copy & dismiss", Theme.Ok))
            { try { Ircuitry.Core.Clipboard.SetText(_lastMint); } catch { } _lastMint = ""; }
            y += 42;
        }
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), _tokenList.Length + " TOKEN(S)", new Vector2(x, y), Theme.TextDim); y += 20;
        int shown = 0;
        foreach (var t in _tokenList)
        {
            if (shown++ >= 8) break;
            var row = new RectF(x, y, w, 32); r.RoundFill(row, Theme.PanelHi, 8f);
            r.Text(r.Fonts.Get(FontKind.SansBold, 12), t.User, new Vector2(row.X + 10, row.Center.Y - 8), Theme.Text);
            r.Text(r.Fonts.Get(FontKind.Mono, 11), t.Id + "  ·  " + t.Role, new Vector2(row.X + 120, row.Center.Y - 7), Theme.TextDim);
            if (_ui.Button("rm.trev." + t.Id, new RectF(row.Right - 8 - 64, row.Center.Y - 12, 64, 24), "Revoke", Theme.Alert))
                rc.RevokeToken(t.Id, () => rc.Tokens(tk => _tokenList = tk));
            y += 36;
        }
    }

    // ---------------- Info view: server overview ----------------
    private void DrawInfoView(Renderer r, RectF panel, float x, float w, ref float y, ControlClient rc)
    {
        if (_ui.Button("rm.inforef", new RectF(panel.Right - 22 - 80, y - 2, 80, 26), "Refresh", Theme.Idle)) rc.Info(i => _serverInfo = i);
        var info = _serverInfo;
        if (info == null) { r.Text(r.Fonts.Get(FontKind.Sans, 13), "loading…", new Vector2(x, y), Theme.TextFaint); return; }
        var up = System.TimeSpan.FromSeconds(info.UptimeSec);
        var rows = new (string k, string v)[]
        {
            ("version", "ircuitry " + info.Version),
            ("uptime", up.Days > 0 ? $"{up.Days}d {up.Hours}h" : up.Hours > 0 ? $"{up.Hours}h {up.Minutes}m" : $"{up.Minutes}m {up.Seconds}s"),
            ("bots", info.Bots.ToString()),
            ("code nodes", info.Code),
        };
        foreach (var (k, v) in rows)
        { r.Text(r.Fonts.Get(FontKind.Sans, 13), k, new Vector2(x, y), Theme.TextDim); r.Text(r.Fonts.Get(FontKind.SansBold, 13), v, new Vector2(x + 130, y), Theme.Text); y += 24; }
        y += 6;
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), info.Clients.Length + " CONNECTED", new Vector2(x, y), Theme.TextDim); y += 20;
        foreach (var c in info.Clients)
        {
            var row = new RectF(x, y, w, 30); r.RoundFill(row, Theme.PanelHi, 8f);
            var (cr, cg, cb) = ControlClient.PeerColor(c.user);
            Hud.SoftDot(r, new Vector2(row.X + 13, row.Center.Y), 4f, new Color(cr, cg, cb));
            r.Text(r.Fonts.Get(FontKind.SansBold, 12), c.user, new Vector2(row.X + 26, row.Center.Y - 8), Theme.Text);
            r.Text(r.Fonts.Get(FontKind.Sans, 11), c.role + (c.editing.Length > 0 ? "  ·  editing " + c.editing : ""), new Vector2(row.X + 120, row.Center.Y - 7), Theme.TextDim);
            y += 34;
        }
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
                // mirror the server's connection rows + variables so the inspector shows (and can edit) the real settings
                var rb = session.Bots.FirstOrDefault(x => x.Name == remoteName);
                if (rb != null)
                {
                    bot.RemoteRev = rb.Rev;   // optimistic-concurrency base: what we loaded from
                    if (rb.Servers.Count > 0)
                    {
                        bot.Servers.Clear();
                        foreach (var s in rb.Servers)
                            bot.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = s.Label, Host = s.Host, Port = s.Port, UseTls = s.Tls, Nick = s.Nick, Channels = s.Channels, RealName = s.RealName, ConnectOnStartup = s.ConnectOnStartup });
                        bot.SelectedServer = 0;
                    }
                    bot.State.Clear();
                    foreach (var kv in rb.Vars) bot.State[kv.Key] = kv.Value;
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
        // mirror server runs into the remote tab's run history, so the History panel shows real server activity
        session.OnRun = (botName, trigger, summary, actions) =>
        {
            var b = _app.Bots.Find(x => x.IsRemote && x.Remote == session && x.RemoteName == botName);
            b?.Runtime.AddHistory(new Ircuitry.Runtime.RunRecord { Time = System.DateTime.Now, Trigger = trigger, Summary = summary, Actions = actions, Fired = true });
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
    // signature of ALL server rows + the bot's variables, so editing any of them triggers a debounced push
    private static long RemoteConnSig(Bot b)
    {
        long h = 17;
        foreach (var s in b.Servers)
            foreach (var v in new object[] { s.Host, s.Port, s.UseTls, s.Nick, s.Channels, s.RealName, s.SaslUser, s.SaslPass, s.SaslMech, s.ClientCertPath, s.ClientCertPass, s.ServerPass, s.ConnectOnStartup })
                h = unchecked(h * 31 + (v?.GetHashCode() ?? 0));
        foreach (var kv in b.State) h = unchecked(h * 31 + kv.Key.GetHashCode() * 17 + (kv.Value?.GetHashCode() ?? 0));
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
                if (_remoteGraphDirty)
                {
                    _remoteGraphDirty = false;
                    var tab = b;   // capture for the async reply
                    b.Remote!.PushGraph(b.RemoteName, Ircuitry.Graph.GraphSerializer.Save(b.Graph, b.RemoteName), b.RemoteRev, (rev, stale) =>
                    {
                        if (stale) { _staleRemote = tab; _staleServerRev = rev; }   // a co-editor pushed first -> prompt
                        else if (rev > 0) tab.RemoteRev = rev;   // our push landed; advance our base
                    });
                }
                if (_remoteConnDirty) { _remoteConnDirty = false; b.Remote!.PushServers(b.RemoteName, b.Servers); b.Remote!.PushState(b.RemoteName, new Dictionary<string, string>(b.State)); }
            }
            catch { }
        }
    }

    /// <summary>Apply the on-screen graph to a running REMOTE bot without a restart - the remote twin of the
    /// local "apply" floppy. If there are edits not yet pushed (debounce still pending), flush them first so the
    /// server applies exactly what's on screen; otherwise ask it to hot-swap directly. The apply request carries
    /// no base revision, so it can't race the optimistic-concurrency check - it just applies whatever graph the
    /// server currently holds, and it's ordered after any in-flight push on the same socket.</summary>
    private void ApplyRemote(Bot b)
    {
        if (b.Remote?.Connected != true) return;
        var tab = b;
        void Apply() => tab.Remote!.ApplyGraph(tab.RemoteName, ok => { if (ok) Notify(Ircuitry.Core.Icons.Glyph("arrows-clockwise") + " Applied changes to the live remote bot"); });

        bool unpushed = b == _remoteLastBot && _remoteGraphDirty;   // local edits the debounce hasn't sent yet
        if (b == _remoteLastBot) { _remoteGraphDirty = false; _remotePushAt = -1; }   // we own the push from here
        if (!unpushed) { Apply(); return; }                        // server already has (or is about to have) the latest

        b.Remote.PushGraph(b.RemoteName, Ircuitry.Graph.GraphSerializer.Save(b.Graph, b.RemoteName), b.RemoteRev, (rev, stale) =>
        {
            if (stale) { _staleRemote = tab; _staleServerRev = rev; return; }   // a co-editor really did change it first
            if (rev > 0) tab.RemoteRev = rev;
            Apply();
        });
    }

    // ---- D9: stale-push (a co-editor changed the bot first) -> ask to reload or overwrite ----
    private Bot? _staleRemote; private long _staleServerRev;

    private void DrawStaleModal(Renderer r)
    {
        var b = _staleRemote; if (b == null || b.Remote == null) return;
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = 460, ph = 200;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Remote changed", Theme.Amber);
        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 16;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 13), $"Someone else edited '{b.RemoteName}' on the server while you were working. Reload to get their version (you lose your unpushed edits here), or overwrite the server with yours.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 13), line, new Vector2(x, y), Theme.TextDim); y += 18; }
        y = panel.Bottom - 52;
        var reload = new RectF(x, y, 130, 34);
        var overwrite = new RectF(reload.Right + 10, y, 130, 34);
        var cancel = new RectF(panel.Right - 22 - 90, y, 90, 34);
        if (_ui.Button("stale.reload", reload, Ircuitry.Core.Icons.Glyph("arrows-clockwise") + " Reload", Theme.Sky, primary: true))
        { var s = b.Remote; var nm = b.RemoteName; _staleRemote = null; OpenRemoteBotInEditor(s!, nm); }   // re-pull graph + rev
        if (_ui.Button("stale.overwrite", overwrite, Ircuitry.Core.Icons.Glyph("upload-simple") + " Overwrite", Theme.Alert))
        {
            var tab = b; tab.RemoteRev = _staleServerRev;   // adopt the server's rev so our push is accepted
            tab.Remote!.PushGraph(tab.RemoteName, Ircuitry.Graph.GraphSerializer.Save(tab.Graph, tab.RemoteName), tab.RemoteRev, (rev, stale) => { if (!stale && rev > 0) tab.RemoteRev = rev; });
            _staleRemote = null;
        }
        if (_ui.Button("stale.cancel", cancel, "Keep editing", Theme.Idle)) _staleRemote = null;
        r.End();
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
            var m = f.MeasureString(Ircuitry.Render.Renderer.SafeText(p.User));
            var pill = new RectF(sp.X + 9, sp.Y + 7, m.X + 12, m.Y + 5);
            r.RoundFill(pill, col, 6f);
            r.Text(f, p.User, new Vector2(pill.X + 6, pill.Y + 2), Theme.TextInk);
        }
        r.End();
    }

    public void DebugOpenRemote()
    {
        _l = DockLayout(); OpenRemote();
        var u = System.Environment.GetEnvironmentVariable("IRCUITRY_REMOTE_URL");
        var t = System.Environment.GetEnvironmentVariable("IRCUITRY_REMOTE_TOKEN");
        if (!string.IsNullOrEmpty(u)) { _rmUrl = u; _rmToken = t ?? ""; ConnectRemote(); }   // headless connected-view check
    }

    /// <summary>--showremoteedit: connect (env URL/token) and open the first remote bot straight into the editor,
    /// so the remote canvas (with co-editor cursors) can be captured headlessly.</summary>
    public void DebugOpenRemoteEdit() { DebugOpenRemote(); _autoOpenRemote = true; }
}
