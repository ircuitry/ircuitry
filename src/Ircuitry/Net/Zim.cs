using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ircuitry.Net;

/// <summary>
/// A minimal, fully-managed reader for the ZIM file format (openzim / Kiwix offline content - e.g. an offline
/// Wikipedia). It opens an archive, looks an entry up by title or path, searches titles (binary-search over the
/// on-disk title index), and reads an entry's content by decompressing only the one cluster it lives in.
///
/// No native libzim and no Xapian: this does TITLE / prefix search, NOT fulltext ranking. Cluster compression
/// supported: raw (1), xz/LZMA2 (4), zstd (5). Spec: https://wiki.openzim.org/wiki/ZIM_file_format
///
/// All access is seek-based and lazy (the pointer lists are never loaded whole), so it stays cheap even on a
/// multi-gigabyte archive; one recently-read cluster is cached.
/// </summary>
public sealed class ZimArchive : IDisposable
{
    private const uint Magic = 0x044d495a;          // 'Z','I','M',0x04 little-endian
    private const ushort RedirectMime = 0xFFFF;

    private readonly FileStream _fs;
    private readonly BinaryReader _br;
    private readonly object _lock = new();
    private readonly long _fileLen;

    public int ArticleCount { get; }
    public int ClusterCount { get; }
    public uint MainPageIndex { get; }
    public char ContentNamespace { get; private set; } = 'A';

    private readonly ulong _urlPtrPos, _titlePtrPos, _clusterPtrPos, _mimeListPos, _checksumPos;
    private readonly string[] _mimeTypes;

    // one-cluster cache (decompressed payload incl. the offset table)
    private int _cacheCluster = -1;
    private byte[]? _cacheData;
    private bool _cacheExtended;

    public readonly struct Entry
    {
        public readonly char Namespace;
        public readonly string Url, Title;
        public readonly bool IsRedirect;
        public readonly uint RedirectIndex;        // url-list index of the redirect target
        public readonly uint Cluster, Blob;
        public readonly ushort Mime;
        public Entry(char ns, string url, string title, bool redir, uint redirIdx, uint cluster, uint blob, ushort mime)
        { Namespace = ns; Url = url; Title = title.Length > 0 ? title : url; IsRedirect = redir; RedirectIndex = redirIdx; Cluster = cluster; Blob = blob; Mime = mime; }
        public string Path => Namespace + "/" + Url;
        public bool IsContent => !IsRedirect && Mime < 0xFFFD;
    }

    private ZimArchive(FileStream fs)
    {
        _fs = fs; _br = new BinaryReader(fs); _fileLen = fs.Length;
        if (_br.ReadUInt32() != Magic) throw new InvalidDataException("not a ZIM file (bad magic)");
        _br.ReadUInt16(); _br.ReadUInt16();          // major, minor version
        _fs.Position += 16;                          // uuid
        ArticleCount = _br.ReadInt32();
        ClusterCount = _br.ReadInt32();
        _urlPtrPos = _br.ReadUInt64();
        _titlePtrPos = _br.ReadUInt64();
        _clusterPtrPos = _br.ReadUInt64();
        _mimeListPos = _br.ReadUInt64();
        MainPageIndex = _br.ReadUInt32();
        _br.ReadUInt32();                            // layoutPage (deprecated)
        _checksumPos = _br.ReadUInt64();
        _mimeTypes = ReadMimeList();
    }

    public static ZimArchive Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZimArchive z;
        try { z = new ZimArchive(fs); }
        catch { fs.Dispose(); throw; }
        z.ContentNamespace = z.DetectContentNamespace();
        return z;
    }

    public void Dispose() { _br.Dispose(); _fs.Dispose(); }

    // ---------------- public lookups ----------------

    /// <summary>The MIME type string for a content entry (or "" if unknown).</summary>
    public string MimeOf(in Entry e) => e.Mime < _mimeTypes.Length ? _mimeTypes[e.Mime] : "";

    /// <summary>Resolve a redirect chain to the underlying content entry (bounded to avoid cycles).</summary>
    public Entry Resolve(Entry e)
    {
        for (int hop = 0; hop < 16 && e.IsRedirect; hop++)
        {
            var t = EntryAtUrlIndex(e.RedirectIndex);
            if (t == null) break;
            e = t.Value;
        }
        return e;
    }

    /// <summary>Find an entry by an explicit namespaced path like "A/Android" or "C/Android".</summary>
    public Entry? FindPath(string nsPath)
    {
        if (string.IsNullOrEmpty(nsPath)) return null;
        char ns; string url;
        if (nsPath.Length >= 2 && nsPath[1] == '/') { ns = nsPath[0]; url = nsPath[2..]; }
        else { ns = ContentNamespace; url = nsPath; }
        int idx = UrlLowerBound(ns, url);
        if (idx >= ArticleCount) return null;
        var e = EntryAtUrlIndex((uint)idx);
        return e is { } ev && ev.Namespace == ns && ev.Url == url ? ev : null;
    }

    /// <summary>Find a content entry whose title equals (case-insensitively) the given title; else the first
    /// prefix match. Returns the resolved (non-redirect) entry.</summary>
    public Entry? FindTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (title.Length >= 2 && title[1] == '/')   // an explicit path was passed
        {
            var p = FindPath(title);
            return p is { } pv ? Resolve(pv) : null;
        }
        Entry? firstPrefix = null;
        foreach (var e in SearchEntries(title, 8))
        {
            firstPrefix ??= e;
            if (string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase)) return Resolve(e);
        }
        return firstPrefix is { } f ? Resolve(f) : null;
    }

    public readonly struct Hit { public readonly string Title, Path; public Hit(string t, string p) { Title = t; Path = p; } }

    /// <summary>Title/prefix search over the content namespace; returns up to <paramref name="limit"/> hits.</summary>
    public List<Hit> SearchTitles(string query, int limit)
    {
        var hits = new List<Hit>();
        foreach (var e in SearchEntries(query, limit)) hits.Add(new Hit(e.Title, e.Path));
        return hits;
    }

    /// <summary>Decompress and return an entry's raw content bytes.</summary>
    public byte[] ReadBytes(Entry e)
    {
        e = Resolve(e);
        if (!e.IsContent) return Array.Empty<byte>();
        lock (_lock)
        {
            EnsureCluster((int)e.Cluster);
            var data = _cacheData!;
            int off = _cacheExtended ? 8 : 4;
            long count = (off == 8 ? (long)BitConverter.ToUInt64(data, 0) : BitConverter.ToUInt32(data, 0)) / off - 1;
            if (e.Blob >= count) return Array.Empty<byte>();
            long start = ReadClusterOffset(data, (int)e.Blob, off);
            long end = ReadClusterOffset(data, (int)e.Blob + 1, off);
            if (start < 0 || end > data.Length || end < start) return Array.Empty<byte>();
            var blob = new byte[end - start];
            Array.Copy(data, start, blob, 0, blob.Length);
            return blob;
        }
    }

    public string ReadText(Entry e) => Encoding.UTF8.GetString(ReadBytes(e));

    /// <summary>A metadata value (the M namespace), e.g. "Title", "Description", "Language".</summary>
    public string Metadata(string name)
    {
        var e = FindPath("M/" + name);
        return e is { } ev ? ReadText(ev) : "";
    }

    public Entry? MainEntry => MainPageIndex != 0xFFFFFFFF && MainPageIndex < ArticleCount
        ? (EntryAtUrlIndex(MainPageIndex) is { } m ? Resolve(m) : null) : null;

    // ---------------- internals ----------------

    private IEnumerable<Entry> SearchEntries(string query, int limit)
    {
        char ns = ContentNamespace;
        // capitalise the first letter for the binary-search KEY (article titles are nearly always capitalised);
        // the actual match test is case-insensitive, so the typed casing doesn't matter.
        string key = query.Length > 0 ? char.ToUpperInvariant(query[0]) + query[1..] : query;
        int lo = TitleLowerBound(ns, key);
        int found = 0;
        for (int i = lo; i < ArticleCount && found < limit; i++)
        {
            var e = EntryFor(i);
            if (e.Namespace != ns) break;            // left the content namespace (list is ns-then-title sorted)
            if (e.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) { yield return e; found++; }
            else if (string.CompareOrdinal(e.Title, key) > 0 && !e.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    // title-list index -> entry (title list stores url-list indices)
    private Entry EntryFor(int titleListIndex)
    {
        lock (_lock)
        {
            _fs.Position = (long)_titlePtrPos + titleListIndex * 4L;
            uint urlIdx = _br.ReadUInt32();
            return ReadDirentAtUrlIndex(urlIdx);
        }
    }

    public Entry? EntryAtUrlIndex(uint urlIndex)
    {
        if (urlIndex >= ArticleCount) return null;
        lock (_lock) return ReadDirentAtUrlIndex(urlIndex);
    }

    private Entry ReadDirentAtUrlIndex(uint urlIndex)
    {
        _fs.Position = (long)_urlPtrPos + urlIndex * 8L;
        ulong direntPos = _br.ReadUInt64();
        return ReadDirent((long)direntPos);
    }

    private Entry ReadDirent(long pos)
    {
        _fs.Position = pos;
        ushort mime = _br.ReadUInt16();
        _br.ReadByte();                              // parameterLen (we don't read the trailing params)
        char ns = (char)_br.ReadByte();
        _br.ReadUInt32();                            // revision
        if (mime == RedirectMime)
        {
            uint redirect = _br.ReadUInt32();
            string url = ReadZString(), title = ReadZString();
            return new Entry(ns, url, title, true, redirect, 0, 0, mime);
        }
        uint cluster = _br.ReadUInt32(), blob = _br.ReadUInt32();
        string u = ReadZString(), t = ReadZString();
        return new Entry(ns, u, t, false, 0, cluster, blob, mime);
    }

    // lower bound in the URL pointer list (sorted by namespace, then url)
    private int UrlLowerBound(char ns, string url)
    {
        int lo = 0, hi = ArticleCount;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            var e = EntryAtUrlIndex((uint)mid)!.Value;
            if (CompareKey(e.Namespace, e.Url, ns, url) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // lower bound in the title pointer list (sorted by namespace, then title)
    private int TitleLowerBound(char ns, string title)
    {
        int lo = 0, hi = ArticleCount;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            var e = EntryFor(mid);
            if (CompareKey(e.Namespace, e.Title, ns, title) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int CompareKey(char ns1, string s1, char ns2, string s2)
    {
        if (ns1 != ns2) return ns1 < ns2 ? -1 : 1;
        return string.CompareOrdinal(s1, s2);
    }

    private char DetectContentNamespace()
    {
        if (MainPageIndex != 0xFFFFFFFF && MainPageIndex < ArticleCount && EntryAtUrlIndex(MainPageIndex) is { } m)
        {
            var r = Resolve(m);
            if (r.Namespace is 'A' or 'C') return r.Namespace;
        }
        if (HasNamespace('C')) return 'C';           // new (namespaceless) scheme
        if (HasNamespace('A')) return 'A';           // classic scheme
        return 'A';
    }

    private bool HasNamespace(char ns)
    {
        int lo = UrlLowerBound(ns, "");
        return lo < ArticleCount && EntryAtUrlIndex((uint)lo)!.Value.Namespace == ns;
    }

    private string[] ReadMimeList()
    {
        lock (_lock)
        {
            _fs.Position = (long)_mimeListPos;
            var list = new List<string>();
            while (true)
            {
                string s = ReadZString();
                if (s.Length == 0) break;            // empty string terminates the list
                list.Add(s);
                if (list.Count > 4096) break;        // sanity bound
            }
            return list.ToArray();
        }
    }

    private string ReadZString()
    {
        var bytes = new List<byte>(32);
        int b;
        while ((b = _fs.ReadByte()) > 0) bytes.Add((byte)b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    // ---------------- clusters ----------------

    private static long ReadClusterOffset(byte[] data, int i, int off)
        => off == 8 ? (long)BitConverter.ToUInt64(data, i * 8) : BitConverter.ToUInt32(data, i * 4);

    private void EnsureCluster(int cluster)
    {
        if (_cacheCluster == cluster && _cacheData != null) return;
        if (cluster < 0 || cluster >= ClusterCount) throw new InvalidDataException("cluster out of range");

        _fs.Position = (long)_clusterPtrPos + cluster * 8L;
        long cOff = (long)_br.ReadUInt64();
        long cEnd = cluster + 1 < ClusterCount ? (long)_br.ReadUInt64()
                  : (_checksumPos > (ulong)cOff ? (long)_checksumPos : _fileLen);
        if (cEnd <= cOff) cEnd = _fileLen;

        _fs.Position = cOff;
        byte info = _br.ReadByte();
        int comp = info & 0x0F;
        _cacheExtended = (info & 0x10) != 0;
        int compLen = (int)Math.Min(cEnd - cOff - 1, int.MaxValue);
        byte[] raw = _br.ReadBytes(compLen);

        _cacheData = comp switch
        {
            0 or 1 => raw,                           // uncompressed
            4 => Inflate(raw, xz: true),             // xz / LZMA2
            5 => Inflate(raw, xz: false),            // zstd
            _ => throw new NotSupportedException("unsupported ZIM cluster compression " + comp),
        };
        _cacheCluster = cluster;
    }

    private static byte[] Inflate(byte[] data, bool xz)
    {
        using var src = new MemoryStream(data);
        using Stream dec = xz
            ? new SharpCompress.Compressors.Xz.XZStream(src)
            : new ZstdSharp.DecompressionStream(src);
        using var outp = new MemoryStream(data.Length * 4);
        dec.CopyTo(outp);
        return outp.ToArray();
    }
}
