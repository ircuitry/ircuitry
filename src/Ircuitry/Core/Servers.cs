using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>A reusable connection profile, saved so several bots can share one server.</summary>
public sealed class ServerProfile
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 6697;
    public bool UseTls { get; set; } = true;
    public bool AcceptInvalidCerts { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public string Nick { get; set; } = "";
    public string Channels { get; set; } = "";
    public string SaslUser { get; set; } = "";
    public string SaslPass { get; set; } = "";   // a {{secret.X}} reference, never a raw password
}

/// <summary>
/// Saved servers (~/ircuitry/servers.json): reusable connection profiles, so a user fills a server in once
/// and points many bots at it. Passwords are stored as {{secret.NAME}} references, so this file holds no
/// raw credentials.
/// </summary>
public static class Servers
{
    private static readonly object Gate = new();
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");
    public static string FilePath => Path.Combine(Dir, "servers.json");

    public static List<ServerProfile> All()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<List<ServerProfile>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch { return new(); }
        }
    }

    public static ServerProfile? Get(string name) =>
        All().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Insert or replace a profile by name.</summary>
    public static void Save(ServerProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) return;
        lock (Gate)
        {
            var list = All().Where(s => !string.Equals(s.Name, p.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(p);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Write(list);
        }
    }

    public static void Delete(string name)
    {
        lock (Gate)
        {
            var list = All().Where(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            Write(list);
        }
    }

    private static void Write(List<ServerProfile> list)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* disk unavailable - best effort */ }
    }
}
