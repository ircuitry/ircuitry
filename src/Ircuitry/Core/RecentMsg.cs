namespace Ircuitry.Core;

/// <summary>One recently-seen IRC message the bot can act on: who said what, where, and its msgid.
/// The runtime keeps a small ring of these so SuperAI's <c>recent_messages</c> tool can hand the model
/// what just happened (server CHATHISTORY arrives async; this is the synchronous "what I've seen") and the
/// model can then react to a specific one by its <see cref="Msgid"/>.</summary>
public sealed record RecentMsg(string Nick, string Channel, string Text, string Msgid);
