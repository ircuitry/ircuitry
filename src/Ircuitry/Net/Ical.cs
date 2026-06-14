using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ircuitry.Net;

/// <summary>One parsed VEVENT from an iCalendar (RFC 5545) feed.</summary>
public sealed class IcalEvent
{
    public string Summary = "";
    public string Location = "";
    public string Description = "";
    public DateTime Start;
    public DateTime End;
    public bool HasStart;
    public bool AllDay;

    /// <summary>Human-friendly start (date for all-day, date+time otherwise).</summary>
    public string When => !HasStart ? "" : AllDay ? Start.ToString("yyyy-MM-dd") : Start.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// A small, dependency-free iCalendar parser: line-unfolding, VEVENT extraction,
/// and the common DATE / DATE-TIME forms (local, UTC "Z", and date-only all-day).
/// Recurrence (RRULE) is not expanded.
/// </summary>
public static class Ical
{
    public static List<IcalEvent> Parse(string text)
    {
        var events = new List<IcalEvent>();
        if (string.IsNullOrEmpty(text)) return events;

        IcalEvent? cur = null;
        foreach (var line in Unfold(text))
        {
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new IcalEvent(); continue; }
            if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { if (cur is { HasStart: true }) events.Add(cur); cur = null; continue; }
            if (cur == null) continue;

            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string namePart = line[..colon];
            string value = line[(colon + 1)..];
            int semi = namePart.IndexOf(';');
            string name = (semi < 0 ? namePart : namePart[..semi]).ToUpperInvariant();
            string paramStr = semi < 0 ? "" : namePart[(semi + 1)..];

            switch (name)
            {
                case "SUMMARY": cur.Summary = Unescape(value); break;
                case "LOCATION": cur.Location = Unescape(value); break;
                case "DESCRIPTION": cur.Description = Unescape(value); break;
                case "DTSTART": cur.Start = ParseDate(value, paramStr, out var allDay); cur.HasStart = true; cur.AllDay = allDay; break;
                case "DTEND": cur.End = ParseDate(value, paramStr, out _); break;
            }
        }
        return events;
    }

    /// <summary>The soonest event starting at or after <paramref name="now"/> (null if none).</summary>
    public static IcalEvent? Next(IEnumerable<IcalEvent> events, DateTime now)
    {
        IcalEvent? best = null;
        foreach (var e in events)
            if (e.HasStart && e.Start >= now && (best == null || e.Start < best.Start)) best = e;
        return best;
    }

    public static List<IcalEvent> OnDay(IEnumerable<IcalEvent> events, DateTime day)
    {
        var res = new List<IcalEvent>();
        foreach (var e in events) if (e.HasStart && e.Start.Date == day.Date) res.Add(e);
        res.Sort((a, b) => a.Start.CompareTo(b.Start));
        return res;
    }

    // RFC 5545 line folding: a continuation line begins with a space or tab.
    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && lines.Count > 0)
                lines[^1] += raw[1..];
            else
                lines.Add(raw);
        }
        return lines;
    }

    private static DateTime ParseDate(string v, string param, out bool allDay)
    {
        allDay = param.IndexOf("VALUE=DATE", StringComparison.OrdinalIgnoreCase) >= 0;
        v = v.Trim();

        if (v.EndsWith("Z", StringComparison.Ordinal) &&
            DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

        if (DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;

        if (v.Length == 8 && DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            allDay = true;
            return d;
        }

        return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var g) ? g : default;
    }

    // ---- writing ----

    /// <summary>Build a single VEVENT block (CRLF-terminated lines) for appending to an .ics file.</summary>
    public static string FormatEvent(string summary, DateTime start, DateTime end, string location, string description, string uid)
    {
        string Fmt(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss");
        var sb = new StringBuilder();
        sb.Append("BEGIN:VEVENT\r\n");
        sb.Append("UID:").Append(uid).Append("\r\n");
        sb.Append("DTSTAMP:").Append(DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'")).Append("\r\n");
        sb.Append("DTSTART:").Append(Fmt(start)).Append("\r\n");
        sb.Append("DTEND:").Append(Fmt(end)).Append("\r\n");
        sb.Append("SUMMARY:").Append(Escape(summary)).Append("\r\n");
        if (location.Length > 0) sb.Append("LOCATION:").Append(Escape(location)).Append("\r\n");
        if (description.Length > 0) sb.Append("DESCRIPTION:").Append(Escape(description)).Append("\r\n");
        sb.Append("END:VEVENT\r\n");
        return sb.ToString();
    }

    /// <summary>Append a VEVENT into existing .ics text (before END:VCALENDAR), or wrap a fresh calendar.</summary>
    public static string AppendEvent(string existing, string vevent)
    {
        const string close = "END:VCALENDAR";
        int idx = existing.LastIndexOf(close, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return existing[..idx] + vevent + existing[idx..];
        return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//ircuitry//Bot Bakery//EN\r\n" + vevent + close + "\r\n";
    }

    private static string Escape(string v) =>
        v.Replace("\\", "\\\\").Replace("\n", "\\n").Replace(",", "\\,").Replace(";", "\\;");

    private static string Unescape(string v)
    {
        if (v.IndexOf('\\') < 0) return v;
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '\\' || i + 1 >= v.Length) { sb.Append(v[i]); continue; }
            char c = v[++i];
            sb.Append(c switch { 'n' => '\n', 'N' => '\n', ',' => ',', ';' => ';', '\\' => '\\', _ => c });
        }
        return sb.ToString();
    }
}
