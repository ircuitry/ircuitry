using System;
using System.Collections.Generic;
using Ircuitry.Core;

namespace Ircuitry.Runtime;

public readonly struct LogEntry
{
    public readonly DateTime Time;
    public readonly LogLevel Level;
    public readonly string Text;
    public readonly string Server;   // origin server label (for multi-server bots); "" otherwise
    public LogEntry(DateTime time, LogLevel level, string text, string server = "")
    { Time = time; Level = level; Text = text; Server = server; }
}

/// <summary>Thread-safe bounded log shared between the IRC read thread and the UI.</summary>
public sealed class ConsoleLog
{
    private const int Cap = 1000;
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly object _lock = new();
    public long Revision { get; private set; }

    public void Add(LogLevel level, string text, string server = "")
    {
        // split multi-line text so the console stays one-line-per-row
        foreach (var raw in text.Split('\n'))
        {
            lock (_lock)
            {
                _entries.AddLast(new LogEntry(DateTime.Now, level, raw.TrimEnd('\r'), server));
                while (_entries.Count > Cap) _entries.RemoveFirst();
                Revision++;
            }
        }
    }

    public void Clear() { lock (_lock) { _entries.Clear(); Revision++; } }

    /// <summary>Copies the most recent <paramref name="max"/> entries (oldest first).</summary>
    public List<LogEntry> Tail(int max)
    {
        lock (_lock)
        {
            var result = new List<LogEntry>(Math.Min(max, _entries.Count));
            int skip = Math.Max(0, _entries.Count - max);
            int i = 0;
            foreach (var e in _entries)
            {
                if (i++ < skip) continue;
                result.Add(e);
            }
            return result;
        }
    }

    public int Count { get { lock (_lock) return _entries.Count; } }
}
