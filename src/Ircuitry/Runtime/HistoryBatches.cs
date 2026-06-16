using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Ircuitry.Core;
using Ircuitry.Irc;

namespace Ircuitry.Runtime;

/// <summary>
/// Tracks IRCv3 CHATHISTORY <c>BATCH</c>es and routes their messages to whoever asked for them.
///
/// Two jobs, both safe to call from the IRC read thread:
///  - <see cref="OnBatch"/> opens/closes a batch (a <c>BATCH +ref chathistory #target</c> / <c>BATCH -ref</c> line);
///  - <see cref="Capture"/> decides whether an incoming message belongs to an open chathistory batch. If it does,
///    the message is DATA ONLY - it is accumulated and the caller must NOT fire any trigger for it (PRIVMSG,
///    TAGMSG, JOIN, NOTICE - anything inside a chathistory batch is unactionable).
///
/// A node requests history by registering a <see cref="Waiter"/> (from a worker thread) BEFORE sending its
/// CHATHISTORY command; when the batch closes we hand the collected messages to the oldest matching waiter.
/// </summary>
internal sealed class HistoryBatches
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <summary>A pending history request, completed (or abandoned on timeout) by the read thread.</summary>
    internal sealed class Waiter
    {
        public string Target = "";
        public volatile bool Abandoned;
        public volatile List<RecentMsg> Result = new();   // published by the read thread before Done.Set()
        public readonly ManualResetEventSlim Done = new(false);
    }

    private sealed class Accum
    {
        public readonly string Target;
        public readonly List<RecentMsg> Msgs = new();
        public Accum(string target) { Target = target; }
    }

    private readonly Dictionary<string, Accum> _open = new();        // touched only on the read thread
    private readonly ConcurrentQueue<Waiter> _waiters = new();       // enqueued by workers, drained by read thread

    /// <summary>Register a history request before its CHATHISTORY command is sent, so a fast batch can't close
    /// before there's a waiter to receive it.</summary>
    public void Await(Waiter w) => _waiters.Enqueue(w);

    /// <summary>True while any chathistory batch is open (so the dispatcher can short-circuit).</summary>
    public bool AnyOpen => _open.Count > 0;

    /// <summary>Process a <c>BATCH</c> line. A BATCH is never itself a trigger. On open we remember a chathistory
    /// batch (and inherit chathistory-ness for a nested batch whose parent is one); on close we deliver.</summary>
    public void OnBatch(IrcMessage m)
    {
        string tok = m.P(0);
        if (tok.Length < 1) return;
        char sign = tok[0];
        string bref = tok[1..];
        if (bref.Length == 0) return;

        if (sign == '+')
        {
            string type = m.P(1);
            bool hist = type.Equals("chathistory", OIC) || type.Equals("draft/chathistory", OIC);
            if (!hist)
            {
                string parent = m.Tag("batch");                     // a nested batch inside a chathistory one
                if (parent.Length > 0 && _open.ContainsKey(parent)) hist = true;
            }
            if (hist) _open[bref] = new Accum(m.P(2));               // P(2) = target (#channel), may be ""
        }
        else if (sign == '-')
        {
            if (_open.Remove(bref, out var acc)) Deliver(acc);
        }
    }

    /// <summary>If <paramref name="m"/> belongs to an open chathistory batch, accumulate it (PRIVMSG/NOTICE
    /// become <see cref="RecentMsg"/>s in <paramref name="rec"/>) and return true so the caller suppresses ALL
    /// triggering for it. Returns false for ordinary live messages.</summary>
    public bool Capture(IrcMessage m, out RecentMsg? rec)
    {
        rec = null;
        string bref = m.Tag("batch");
        if (bref.Length == 0 || !_open.TryGetValue(bref, out var acc)) return false;

        if (m.Is("PRIVMSG") || m.Is("NOTICE"))
        {
            string nick = m.Nick ?? "";
            string target = m.P(0);
            bool toChannel = target.StartsWith('#') || target.StartsWith('&');
            string channel = toChannel ? target : nick;
            string msgid = m.Tag("msgid"); if (msgid.Length == 0) msgid = m.Tag("draft/msgid");
            rec = new RecentMsg(nick, channel, m.Trailing, msgid);
            acc.Msgs.Add(rec);
        }
        return true;   // JOIN/TAGMSG/etc inside the batch are still data-only: suppressed, just not surfaced
    }

    // Hand a closed batch's messages to the best-matching waiter: exact target first, then a target-agnostic
    // waiter, then the oldest. Abandoned (timed-out) waiters are dropped so they can't capture a later batch.
    private void Deliver(Accum acc)
    {
        var live = new List<Waiter>();
        while (_waiters.TryDequeue(out var w)) if (!w.Abandoned) live.Add(w);

        int idx = -1;
        if (acc.Target.Length > 0) idx = live.FindIndex(w => w.Target.Equals(acc.Target, OIC));
        if (idx < 0) idx = live.FindIndex(w => w.Target.Length == 0);
        if (idx < 0 && live.Count > 0) idx = 0;

        if (idx >= 0)
        {
            live[idx].Result = acc.Msgs;
            live[idx].Done.Set();
            live.RemoveAt(idx);
        }
        foreach (var w in live) _waiters.Enqueue(w);   // re-queue the rest, order preserved
    }
}
