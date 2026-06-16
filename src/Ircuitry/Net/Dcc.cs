using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ircuitry.Net;

/// <summary>
/// DCC (Direct Client-to-Client) plumbing: parse the CTCP offers a peer sends over IRC
/// (<c>DCC SEND/CHAT/RESUME/ACCEPT</c>), convert the quirky 32-bit integer IPs, and stream a file over the
/// direct TCP socket (with the 4-byte big-endian acks classic DCC expects). The socket orchestration +
/// CTCP signalling lives in ServerConn; this holds the pure, testable parts.
/// </summary>
public static class Dcc
{
    public readonly record struct Offer(string Type, string File, string Ip, int Port, long Size, string Token, long Position);

    /// <summary>Parse a CTCP DCC payload (the text BETWEEN the \x01 markers). Returns false if it isn't DCC.</summary>
    public static bool TryParse(string s, out Offer offer)
    {
        offer = default;
        var p = Tokenize(s);
        if (p.Count < 2 || !p[0].Equals("DCC", StringComparison.OrdinalIgnoreCase)) return false;
        string type = p[1].ToUpperInvariant();
        try
        {
            switch (type)
            {
                case "SEND":      // DCC SEND <name> <ip> <port> <size> [token]    (port 0 + token = passive/reverse)
                    if (p.Count < 6) return false;
                    offer = new Offer("send", SanitizeName(p[2]), IpFromInt(UL(p[3])), (int)UL(p[4]), (long)UL(p[5]), p.Count > 6 ? p[6] : "", 0);
                    return true;
                case "CHAT":      // DCC CHAT chat <ip> <port>
                    if (p.Count < 5) return false;
                    offer = new Offer("chat", "chat", IpFromInt(UL(p[3])), (int)UL(p[4]), 0, "", 0);
                    return true;
                case "RESUME":    // DCC RESUME <name> <port> <position> [token]
                    if (p.Count < 5) return false;
                    offer = new Offer("resume", SanitizeName(p[2]), "", (int)UL(p[3]), 0, p.Count > 5 ? p[5] : "", (long)UL(p[4]));
                    return true;
                case "ACCEPT":    // DCC ACCEPT <name> <port> <position> [token]
                    if (p.Count < 5) return false;
                    offer = new Offer("accept", SanitizeName(p[2]), "", (int)UL(p[3]), 0, p.Count > 5 ? p[5] : "", (long)UL(p[4]));
                    return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    private static ulong UL(string s) => ulong.TryParse(s.Trim(), out var v) ? v : 0;

    // classic DCC carries the IP as a 32-bit integer in host order
    public static string IpFromInt(ulong v) => $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
    public static ulong IpToInt(string dotted)
    {
        var b = IPAddress.Parse(dotted.Trim()).GetAddressBytes();
        return ((ulong)b[0] << 24) | ((ulong)b[1] << 16) | ((ulong)b[2] << 8) | b[3];
    }

    /// <summary>A peer-supplied filename, reduced to a safe basename (no path traversal, no control chars).</summary>
    public static string SanitizeName(string f)
    {
        f = (f ?? "").Trim().Trim('"');
        f = Path.GetFileName(f);
        var sb = new StringBuilder();
        foreach (var c in f) if (c >= ' ' && c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar) sb.Append(c);
        f = sb.ToString().Trim();
        return f.Length > 0 ? f : "file";
    }

    /// <summary>Quote a filename for the CTCP if it contains spaces.</summary>
    public static string QuoteName(string f) => f.Contains(' ') ? "\"" + f + "\"" : f;

    // CTCP delimiter (SOH, U+0001). Written with \u (exactly 4 hex digits) NOT \x (greedy 1-4) - "\x01DCC"
    // would parse as U+01DC ('ǜ') + "C", silently corrupting the marker.
    public const char Marker = '\u0001';
    /// <summary>The literal a CTCP DCC line starts with (<c>SOH + "DCC "</c>), for detecting incoming offers.</summary>
    public const string Prefix = "\u0001DCC ";

    /// <summary>Build a complete CTCP DCC SEND line (incl. the SOH markers): <c>\x01DCC SEND name ip port size [token]\x01</c>.</summary>
    public static string SendLine(string name, ulong ipInt, int port, long size, string token = "")
        => $"{Marker}DCC SEND {QuoteName(name)} {ipInt} {port} {size}{(token.Length > 0 ? " " + token : "")}{Marker}";

    /// <summary>Strip the surrounding SOH markers from a CTCP payload.</summary>
    public static string Strip(string ctcp) => ctcp.Trim(Marker);

    /// <summary>Send a file over an open DCC socket, draining the receiver's acks. Returns bytes sent.</summary>
    public static long StreamOut(Stream net, string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var buf = new byte[16384];
        long sent = 0; int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) { net.Write(buf, 0, n); sent += n; }
        net.Flush();
        return sent;
    }

    /// <summary>Receive a file over an open DCC socket, writing to <paramref name="savePath"/> and sending the
    /// 4-byte big-endian total-received ack after each chunk. Reads until <paramref name="size"/> bytes (or the
    /// socket closes if size is unknown). Returns bytes received.</summary>
    public static long StreamIn(Stream net, string savePath, long size)
    {
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(savePath);
        var buf = new byte[16384];
        var ack = new byte[4];
        long got = 0; int n;
        while ((size <= 0 || got < size) && (n = net.Read(buf, 0, buf.Length)) > 0)
        {
            fs.Write(buf, 0, n);
            got += n;
            ack[0] = (byte)(got >> 24); ack[1] = (byte)(got >> 16); ack[2] = (byte)(got >> 8); ack[3] = (byte)got;
            try { net.Write(ack, 0, 4); } catch { /* some senders close early; the file is already written */ }
        }
        fs.Flush();
        return got;
    }

    /// <summary>This machine's primary LAN address (the source a route to the internet would use). Used to
    /// advertise where a peer should connect for a DCC SEND when no explicit IP is configured.</summary>
    public static string LocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    // split on whitespace, honouring "double quoted" filenames
    private static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(s[i])) i++;
            if (i >= n) break;
            if (s[i] == '"')
            {
                int j = s.IndexOf('"', i + 1);
                if (j < 0) { list.Add(s.Substring(i + 1)); break; }
                list.Add(s.Substring(i + 1, j - i - 1));
                i = j + 1;
            }
            else
            {
                int j = i;
                while (j < n && !char.IsWhiteSpace(s[j])) j++;
                list.Add(s.Substring(i, j - i));
                i = j;
            }
        }
        return list;
    }
}
