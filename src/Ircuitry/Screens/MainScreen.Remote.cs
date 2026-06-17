using System.Collections.Generic;
using System.IO;
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
    private readonly List<(string label, string url, string token)> _rmSaved = new();

    public void OpenRemote()
    {
        _remoteOpen = true; _remoteJustOpened = true; _rmMsg = "";
        LoadServers();
        if (_rmUrl.Length == 0 && _rmSaved.Count > 0) { _rmUrl = _rmSaved[0].url; _rmToken = _rmSaved[0].token; }
    }

    /// <summary>Keep the remote session live every frame (drains its reply/event callbacks).</summary>
    private void RemotePump() => _remote?.Pump();

    private string ServersFile => Path.Combine(AppModel.WorkspaceDir, "servers.json");

    private void LoadServers()
    {
        _rmSaved.Clear();
        try
        {
            if (!File.Exists(ServersFile)) return;
            using var d = JsonDocument.Parse(File.ReadAllText(ServersFile));
            foreach (var e in d.RootElement.EnumerateArray())
                _rmSaved.Add((e.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                              e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                              e.TryGetProperty("token", out var t) ? t.GetString() ?? "" : ""));
        }
        catch { }
    }

    private void SaveServer(string url, string token)
    {
        _rmSaved.RemoveAll(s => s.url == url);
        _rmSaved.Insert(0, (url, url, token));
        try { File.WriteAllText(ServersFile, JsonSerializer.Serialize(_rmSaved.ConvertAll(s => new { label = s.label, url = s.url, token = s.token }), new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    private void ConnectRemote()
    {
        _remote?.Dispose();
        _remote = new ControlClient();
        _remote.Connect(_rmUrl.Trim(), _rmToken.Trim(), _rmUrl.Trim());
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
            { _remote?.Disconnect(); _remote?.Dispose(); _remote = null; _rmMsg = "disconnected"; }
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
                var act = new RectF(row.Right - 12 - 84, row.Center.Y - 13, 84, 26);
                if (_ui.Button("rm.ss." + b.Name, act, b.Running ? "Stop" : "Start", b.Running ? Theme.Alert : Theme.Ok, primary: !b.Running))
                { if (b.Running) rc.Stop(b.Name); else rc.Start(b.Name); }
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
                for (int i = 0; i < _rmSaved.Count && i < 4; i++)
                {
                    var s = _rmSaved[i];
                    var bw = r.Fonts.Get(FontKind.SansBold, 12).MeasureString(s.url).X + 26;
                    if (_ui.Button("rm.saved" + i, new RectF(cx, y, bw, 28), s.url, Theme.Sky))
                    { _rmUrl = s.url; _rmToken = s.token; }
                    cx += bw + 8;
                }
                y += 38;
            }

            r.Text(r.Fonts.Get(FontKind.SansBold, 12), "SERVER", new Vector2(x, y), Theme.TextDim); y += 18;
            _rmUrl = _ui.TextField("rm.url", new RectF(x, y, w, 34), _rmUrl, "mita:48700"); y += 44;
            r.Text(r.Fonts.Get(FontKind.SansBold, 12), "ACCESS TOKEN", new Vector2(x, y), Theme.TextDim); y += 18;
            _rmToken = _ui.TextField("rm.token", new RectF(x, y, w, 34), _rmToken, "token", password: true); y += 46;

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

    public void DebugOpenRemote()
    {
        _l = Layout.Compute(_vw, _vh, _consoleH); OpenRemote();
        var u = System.Environment.GetEnvironmentVariable("IRCUITRY_REMOTE_URL");
        var t = System.Environment.GetEnvironmentVariable("IRCUITRY_REMOTE_TOKEN");
        if (!string.IsNullOrEmpty(u)) { _rmUrl = u; _rmToken = t ?? ""; ConnectRemote(); }   // headless connected-view check
    }
}
