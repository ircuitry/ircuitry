using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Ircuitry.Graph;

/// <summary>A small per-bot AI response cache stored in bot state as JSON. Matches on normalised prompt text
/// by default; an optional embedding vector per entry enables semantic (cosine-similarity) matching when the
/// node is configured with an embeddings endpoint. Bounded FIFO so the workspace can't grow without limit.</summary>
public static class AiCache
{
    private sealed class Entry
    {
        public string n { get; set; } = "";   // normalised prompt
        public string r { get; set; } = "";   // cached reply
        public long t { get; set; }            // unix seconds
        public float[]? e { get; set; }        // optional embedding
    }

    private sealed class Bucket { public int v { get; set; } = 1; public List<Entry> items { get; set; } = new(); }

    private static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Lowercase, drop punctuation, collapse whitespace - so "Hello, there!" matches "hello there".</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        bool gap = false;
        foreach (char ch in (s ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { if (gap && sb.Length > 0) sb.Append(' '); sb.Append(ch); gap = false; }
            else gap = true;
        }
        return sb.ToString();
    }

    /// <summary>Find a cached reply: exact normalised match first (free), else the most similar embedding at or
    /// above <paramref name="threshold"/> when a query vector is supplied. Null = miss.</summary>
    public static (string reply, double score)? Lookup(string json, string norm, float[]? query, double threshold)
    {
        var b = Parse(json);
        foreach (var it in b.items) if (it.n == norm) return (it.r, 1.0);
        if (query != null)
        {
            double best = -1; Entry? bestE = null;
            foreach (var it in b.items)
                if (it.e != null) { double s = Cosine(query, it.e); if (s > best) { best = s; bestE = it; } }
            if (bestE != null && best >= threshold) return (bestE.r, best);
        }
        return null;
    }

    /// <summary>Store (or refresh) an entry, dropping the oldest past <paramref name="max"/>. Returns new JSON.</summary>
    public static string Put(string json, string norm, string reply, float[]? embedding, int max)
    {
        var b = Parse(json);
        b.items.RemoveAll(x => x.n == norm);
        b.items.Add(new Entry { n = norm, r = reply, t = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), e = embedding });
        int cap = Math.Max(1, max);
        while (b.items.Count > cap) b.items.RemoveAt(0);
        return JsonSerializer.Serialize(b, Opts);
    }

    public static int Count(string json) => Parse(json).items.Count;

    private static Bucket Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Bucket();
        try { return JsonSerializer.Deserialize<Bucket>(json) ?? new Bucket(); }
        catch { return new Bucket(); }
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return -1;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (na == 0 || nb == 0) ? -1 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
