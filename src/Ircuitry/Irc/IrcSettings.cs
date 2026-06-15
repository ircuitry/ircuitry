using System.Collections.Generic;
using System.Linq;

namespace Ircuitry.Irc;

public sealed class IrcSettings
{
    public string Label = "";         // friendly name for this server (a bot can hold several)
    public string Host = "";          // no default network - the user fills this in
    public int Port = 6697;
    public bool UseTls = true;
    public bool AcceptInvalidCerts = true;   // many IRC servers use self-signed certs
    public bool AutoReconnect = true;        // reconnect with backoff on an unexpected drop
    public bool ConnectOnStartup = false;    // bring this server online when the app launches

    // IRCv3 bot-tools spec (draft/bot-cmds + draft/bot-tools) - advanced ("Obby"), off by default
    public bool BotMode = true;               // set +B so clients know we're a bot
    public bool AdvertiseCommands = false;    // expose On Command nodes as structured slash commands
    public bool StreamWorkflows = false;      // stream tool steps as a workflow when a fire uses tools

    public string Nick = "ircuitry-bot";
    public string User = "ircuitry";
    public string RealName = "ircuitry • IRCv3 Bot Bakery";
    public string ServerPass = "";

    public string SaslUser = "";
    public string SaslPass = "";
    public bool UseSasl => SaslPass.Length > 0;

    public string Channels = "#ircuitry-test";   // comma/space separated

    public IEnumerable<string> ChannelList =>
        Channels.Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());

    /// <summary>What to show/route by: the label if set, else the host, else a placeholder.</summary>
    public string DisplayName => Label.Length > 0 ? Label : (Host.Length > 0 ? Host : "(no server)");

    public IrcSettings Clone() => (IrcSettings)MemberwiseClone();
}
