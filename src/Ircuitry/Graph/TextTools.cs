using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ircuitry.Graph;

/// <summary>
/// Pure text / number / encoding helpers behind the built-in "toolkit" nodes (Encode, Hash, Change Case,
/// Regex, Math, Convert, Number Theory, Number Format, Date/Time, Random, IRC Color, Stats). These exist so
/// that community nodes can be RECIPES of real built-in nodes instead of code blobs - every capability a
/// community node needs lives here, behind a normal node.
/// </summary>
public static class TextTools
{
    // ============================ encodings ============================
    public static string Encode(string op, string mode, string s)
    {
        bool dec = mode.StartsWith("dec", StringComparison.OrdinalIgnoreCase);
        switch (op)
        {
            case "base64": return dec ? FromB64(s) : Convert.ToBase64String(U8(s));
            case "base32": return dec ? Base32Decode(s) : Base32Encode(U8(s));
            case "hex": return dec ? FromHex(s) : Convert.ToHexString(U8(s)).ToLowerInvariant();
            case "url": return dec ? Uri.UnescapeDataString(s) : Uri.EscapeDataString(s);
            case "html": return dec ? System.Net.WebUtility.HtmlDecode(s) : System.Net.WebUtility.HtmlEncode(s);
            case "binary": return dec ? BinaryDecode(s) : BinaryEncode(s);
            case "morse": return dec ? MorseDecode(s) : MorseEncode(s);
            case "rot13": return Rot13(s);
            case "zwsp": return dec ? ZwspDecode(s) : ZwspEncode(s);
            case "json": return dec ? JsonUnescape(s) : JsonEscape(s);
            default: return s;
        }
    }

    // escapes text so it can be dropped inside a JSON string literal ("...HERE...") - quotes, backslashes, newlines, control chars
    private static string JsonEscape(string s)
    {
        var j = System.Text.Json.JsonSerializer.Serialize(s);   // yields a quoted, fully-escaped JSON string
        return j.Length >= 2 ? j.Substring(1, j.Length - 2) : j; // strip the surrounding quotes
    }
    private static string JsonUnescape(string s)
    {
        try
        {
            var t = s.Trim();
            var quoted = t.StartsWith("\"") && t.EndsWith("\"") && t.Length >= 2 ? t : "\"" + s + "\"";
            return System.Text.Json.JsonSerializer.Deserialize<string>(quoted) ?? "";
        }
        catch { return "(invalid json string)"; }
    }

    // zero-width steganography: each UTF-8 bit becomes a zero-width space (0) / non-joiner (1), terminated by a marker
    private static string ZwspEncode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in U8(s)) for (int i = 7; i >= 0; i--) sb.Append((b >> i & 1) == 0 ? '​' : '‌');
        return sb.ToString();
    }
    private static string ZwspDecode(string s)
    {
        var bits = s.Where(c => c is '​' or '‌').Select(c => c == '‌' ? 1 : 0).ToArray();
        var bytes = new List<byte>();
        for (int i = 0; i + 8 <= bits.Length; i += 8) { int v = 0; for (int j = 0; j < 8; j++) v = (v << 1) | bits[i + j]; bytes.Add((byte)v); }
        try { return Encoding.UTF8.GetString(bytes.ToArray()); } catch { return "(invalid)"; }
    }

    private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);
    private static string FromB64(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim())); } catch { return "(invalid base64)"; } }
    private static string FromHex(string s) { try { return Encoding.UTF8.GetString(Convert.FromHexString(new string(s.Where(Uri.IsHexDigit).ToArray()))); } catch { return "(invalid hex)"; } }

    private static string Rot13(string s) => new string(s.Select(ch =>
        ch is >= 'a' and <= 'z' ? (char)('a' + (ch - 'a' + 13) % 26) :
        ch is >= 'A' and <= 'Z' ? (char)('A' + (ch - 'A' + 13) % 26) : ch).ToArray());

    private static string BinaryEncode(string s) => string.Join(" ", U8(s).Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
    private static string BinaryDecode(string s)
    {
        try { return Encoding.UTF8.GetString(s.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 2)).ToArray()); }
        catch { return "(invalid binary)"; }
    }

    private const string B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder(); int bits = 0, val = 0;
        foreach (var b in data) { val = (val << 8) | b; bits += 8; while (bits >= 5) { sb.Append(B32[(val >> (bits - 5)) & 31]); bits -= 5; } }
        if (bits > 0) sb.Append(B32[(val << (5 - bits)) & 31]);
        while (sb.Length % 8 != 0) sb.Append('=');
        return sb.ToString();
    }
    private static string Base32Decode(string s)
    {
        try
        {
            s = s.TrimEnd('=').ToUpperInvariant(); int bits = 0, val = 0; var bytes = new List<byte>();
            foreach (var c in s) { int i = B32.IndexOf(c); if (i < 0) continue; val = (val << 5) | i; bits += 5; if (bits >= 8) { bytes.Add((byte)((val >> (bits - 8)) & 0xFF)); bits -= 8; } }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        catch { return "(invalid base32)"; }
    }

    private static readonly Dictionary<char, string> Morse = new()
    {
        ['a']=".-",['b']="-...",['c']="-.-.",['d']="-..",['e']=".",['f']="..-.",['g']="--.",['h']="....",['i']="..",
        ['j']=".---",['k']="-.-",['l']=".-..",['m']="--",['n']="-.",['o']="---",['p']=".--.",['q']="--.-",['r']=".-.",
        ['s']="...",['t']="-",['u']="..-",['v']="...-",['w']=".--",['x']="-..-",['y']="-.--",['z']="--..",
        ['0']="-----",['1']=".----",['2']="..---",['3']="...--",['4']="....-",['5']=".....",['6']="-....",['7']="--...",['8']="---..",['9']="----.",
        ['.']=".-.-.-",[',']="--..--",['?']="..--..",['\'']=".----.",['!']="-.-.--",['/']="-..-.",['(']="-.--.",[')']="-.--.-",
        ['&']=".-...",[':']="---...",[';']="-.-.-.",['=']="-...-",['+']=".-.-.",['-']="-....-",['_']="..--.-",['"']=".-..-.",['@']=".--.-.",
    };
    private static string MorseEncode(string s)
        => string.Join(" ", s.ToLowerInvariant().Select(ch => ch == ' ' ? "/" : Morse.TryGetValue(ch, out var m) ? m : "").Where(x => x.Length > 0));
    private static string MorseDecode(string s)
    {
        var rev = Morse.ToDictionary(k => k.Value, k => k.Key);
        var sb = new StringBuilder();
        foreach (var tok in s.Replace("/", " / ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(tok == "/" ? ' ' : rev.TryGetValue(tok, out var c) ? c : '?');
        return sb.ToString();
    }

    // ============================ hashes ============================
    public static string Hash(string op, string s)
    {
        var data = U8(s);
        switch (op)
        {
            case "md5": return Hex(MD5.HashData(data));
            case "sha1": return Hex(SHA1.HashData(data));
            case "sha256": return Hex(SHA256.HashData(data));
            case "sha512": return Hex(SHA512.HashData(data));
            case "crc32": return Crc32(data).ToString("x8");
            default: return "";
        }
    }
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320 & (uint)(-(crc & 1))); }
        return ~crc;
    }

    // ============================ case / reshaping ============================
    public static string Case(string op, string s)
    {
        switch (op)
        {
            case "upper": return s.ToUpperInvariant();
            case "lower": return s.ToLowerInvariant();
            case "title": return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
            case "sentence": return Sentence(s);
            case "capitalize": return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
            case "camel": return Camel(s, false);
            case "pascal": return Camel(s, true);
            case "snake": return Joinwords(s, "_").ToLowerInvariant();
            case "kebab": return Joinwords(s, "-").ToLowerInvariant();
            case "constant": return Joinwords(s, "_").ToUpperInvariant();
            case "mock": return Mock(s);
            case "leet": return Leet(s);
            case "clap": return string.Join(" \U0001F44F ", Words(s));   // intentional unicode (clap)
            case "invert": return new string(s.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
            default: return s;
        }
    }
    private static string Sentence(string s)
    {
        var sb = new StringBuilder(s.ToLowerInvariant()); bool cap = true;
        for (int i = 0; i < sb.Length; i++)
        {
            if (cap && char.IsLetter(sb[i])) { sb[i] = char.ToUpperInvariant(sb[i]); cap = false; }
            else if (sb[i] is '.' or '!' or '?') cap = true;
        }
        return sb.ToString();
    }
    private static string[] Words(string s) => Regex.Split(s.Trim(), @"[\s_\-]+").Where(w => w.Length > 0).ToArray();
    private static string Joinwords(string s, string sep) => string.Join(sep, Regex.Matches(s, @"[A-Z]+(?![a-z])|[A-Z][a-z]*|[a-z]+|[0-9]+").Select(m => m.Value));
    private static string Camel(string s, bool pascal)
    {
        var w = Words(s).Select(x => x.ToLowerInvariant()).ToArray();
        var sb = new StringBuilder();
        for (int i = 0; i < w.Length; i++) sb.Append(i == 0 && !pascal ? w[i] : char.ToUpperInvariant(w[i][0]) + w[i][1..]);
        return sb.ToString();
    }
    private static string Mock(string s)
    { var sb = new StringBuilder(); bool up = false; foreach (var c in s) { if (char.IsLetter(c)) { sb.Append(up ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)); up = !up; } else sb.Append(c); } return sb.ToString(); }
    private static readonly Dictionary<char, char> LeetMap = new() { ['a']='4',['e']='3',['i']='1',['o']='0',['s']='5',['t']='7',['l']='1',['b']='8',['g']='9' };
    private static string Leet(string s) => new string(s.Select(c => LeetMap.TryGetValue(char.ToLowerInvariant(c), out var l) ? l : c).ToArray());

    // ============================ text shaping ============================
    public static string Shape(string op, string s, int n, string fill)
    {
        switch (op)
        {
            case "reverse": return new string(s.Reverse().ToArray());
            case "trim": return s.Trim();
            case "squeeze": return Regex.Replace(s.Trim(), @"\s+", " ");
            case "length": return s.Length.ToString();
            case "repeat": return string.Concat(Enumerable.Repeat(s, Math.Clamp(n, 0, 1000)));
            case "truncate": return s.Length <= n ? s : s[..Math.Max(0, n)].TrimEnd() + "…";
            case "padleft": return s.PadLeft(n, fill.Length > 0 ? fill[0] : ' ');
            case "padright": return s.PadRight(n, fill.Length > 0 ? fill[0] : ' ');
            case "slug": return Slug(s);
            case "acronym": return string.Concat(Words(s).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0])));
            case "disemvowel": return new string(s.Where(c => !"aeiouAEIOU".Contains(c)).ToArray());
            case "numbering": return Numbering(s);
            case "wrap": return Wrap(s, Math.Max(1, n));
            case "zalgo": return Zalgo(s, Math.Clamp(n <= 1 ? 3 : n, 1, 15));
            case "banner": return Banner(s);
            default: return s;
        }
    }
    private static string Slug(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-"), @"(^-+|-+$)", "");
    private static string Numbering(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((l, i) => $"{i + 1}. {l}"));
    }
    private static string Zalgo(string s, int intensity)
    {
        var sb = new StringBuilder();
        int k = 0;
        foreach (var ch in s)
        {
            sb.Append(ch);
            if (char.IsWhiteSpace(ch)) continue;
            for (int i = 0; i < intensity; i++) sb.Append((char)(0x0300 + ((k++ * 7 + i * 13) % 0x70)));   // deterministic combining marks
        }
        return sb.ToString();
    }
    private static string Banner(string s)
    {
        string line = (s.Replace("\r", "").Split('\n')[0]).Trim();
        if (line.Length == 0) line = " ";
        string bar = new string('-', line.Length + 2);
        return $"+{bar}+\n| {line} |\n+{bar}+";
    }
    private static string Wrap(string s, int width)
    {
        var sb = new StringBuilder(); int col = 0;
        foreach (var word in s.Split(' '))
        {
            if (col > 0 && col + 1 + word.Length > width) { sb.Append('\n'); col = 0; }
            else if (col > 0) { sb.Append(' '); col++; }
            sb.Append(word); col += word.Length;
        }
        return sb.ToString();
    }

    // ============================ regex ============================
    public static string Regex_(string op, string pattern, string repl, string flags, string s)
    {
        var opts = RegexOptions.None;
        if (flags.Contains('i')) opts |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) opts |= RegexOptions.Multiline;
        if (flags.Contains('s')) opts |= RegexOptions.Singleline;
        Regex rx;
        try { rx = new Regex(pattern, opts); } catch (Exception e) { return "(bad regex: " + e.Message + ")"; }
        switch (op)
        {
            case "match": return rx.IsMatch(s) ? "true" : "false";
            case "first": { var m = rx.Match(s); return m.Success ? (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value) : ""; }
            case "all": return string.Join("\n", rx.Matches(s).Select(m => m.Groups.Count > 1 ? m.Groups[1].Value : m.Value));
            case "count": return rx.Matches(s).Count.ToString();
            case "replace": return rx.Replace(s, repl);
            default: return s;
        }
    }

    // ============================ math expression ============================
    public static string Math_(string expr)
    {
        try { double v = new ExprEval(expr).Parse(); return Num(v); }
        catch (Exception e) { return "(math error: " + e.Message + ")"; }
    }
    public static string Num(double v)
        => Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15 ? ((long)Math.Round(v)).ToString() : v.ToString("0.######", CultureInfo.InvariantCulture);

    // ============================ unit / value convert ============================
    public static string Convert_(string family, double v, string from, string to)
    {
        from = from.Trim().ToLowerInvariant(); to = to.Trim().ToLowerInvariant();
        switch (family)
        {
            case "temperature":
                double c = from switch { "f" or "fahrenheit" => (v - 32) * 5 / 9, "k" or "kelvin" => v - 273.15, _ => v };
                double outv = to switch { "f" or "fahrenheit" => c * 9 / 5 + 32, "k" or "kelvin" => c + 273.15, _ => c };
                return Num(outv);
            case "length": return Num(v * LenM(from) / LenM(to));
            case "mass": return Num(v * MassKg(from) / MassKg(to));
            case "speed": return Num(v * SpeedMs(from) / SpeedMs(to));
            case "data": return Num(v * Bytes(from) / Bytes(to));
            default: return "(unknown unit family)";
        }
    }
    private static double LenM(string u) => u switch { "mm"=>0.001,"cm"=>0.01,"m"=>1,"km"=>1000,"in"or"inch"=>0.0254,"ft"or"feet"=>0.3048,"yd"=>0.9144,"mi"or"mile"=>1609.344,_=>1 };
    private static double MassKg(string u) => u switch { "mg"=>1e-6,"g"=>0.001,"kg"=>1,"t"=>1000,"oz"=>0.0283495,"lb"or"lbs"=>0.453592,"st"=>6.35029,_=>1 };
    private static double SpeedMs(string u) => u switch { "mps"or"m/s"=>1,"kmh"or"km/h"=>0.277778,"mph"=>0.44704,"kn"or"knot"=>0.514444,_=>1 };
    private static double Bytes(string u) => u switch { "b"=>1,"kb"=>1e3,"mb"=>1e6,"gb"=>1e9,"tb"=>1e12,"kib"=>1024,"mib"=>1048576,"gib"=>1073741824,_=>1 };

    // ============================ number theory ============================
    public static string NumTheory(string op, string a, string b)
    {
        long x = (long)ParseD(a), y = (long)ParseD(b);
        switch (op)
        {
            case "factorial": { if (x < 0) return "(needs n >= 0)"; if (x > 5000) return "(too large)"; BigInteger f = 1; for (long i = 2; i <= x; i++) f *= i; return f.ToString(); }
            case "fibonacci": { if (x < 0 || x > 100000) return "(0..100000)"; BigInteger p = 0, q = 1; for (long i = 0; i < x; i++) { (p, q) = (q, p + q); } return p.ToString(); }
            case "gcd": return Gcd(Math.Abs(x), Math.Abs(y)).ToString();
            case "lcm": { long g = Gcd(Math.Abs(x), Math.Abs(y)); return g == 0 ? "0" : (Math.Abs(x / g * y)).ToString(); }
            case "isprime": return IsPrime(x) ? "true" : "false";
            case "nextprime": { long n = x + 1; while (!IsPrime(n)) n++; return n.ToString(); }
            case "ordinal": return Ordinal(x);
            case "abs": return Num(Math.Abs(ParseD(a)));
            case "sign": return Math.Sign(ParseD(a)).ToString();
            case "round": return Num(Math.Round(ParseD(a)));
            case "floor": return Num(Math.Floor(ParseD(a)));
            case "ceil": return Num(Math.Ceiling(ParseD(a)));
            default: return "";
        }
    }
    private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }
    private static bool IsPrime(long n) { if (n < 2) return false; if (n % 2 == 0) return n == 2; for (long i = 3; i * i <= n; i += 2) if (n % i == 0) return false; return true; }
    public static string Ordinal(long n)
    {
        long m = Math.Abs(n) % 100, d = Math.Abs(n) % 10;
        string suf = (m is >= 11 and <= 13) ? "th" : d switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n + suf;
    }

    // ============================ number format ============================
    public static string NumFormat(string op, string value, int radix)
    {
        switch (op)
        {
            case "base": { try { long n = ParseRadix(value); return radix == 10 ? n.ToString() : ToBase(n, radix); } catch { return "(invalid number)"; } }
            case "roman": { if (!long.TryParse(value.Trim(), out var r) || r < 1 || r > 3999) return "(1..3999)"; return Roman(r); }
            case "unroman": return UnRoman(value.Trim().ToUpperInvariant());
            case "words": { if (!long.TryParse(value.Trim(), out var w)) return "(integer please)"; return NumWords(w); }
            case "commas": { if (!double.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var c)) return value; return c.ToString("#,0.######", CultureInfo.InvariantCulture); }
            default: return value;
        }
    }
    private static long ParseRadix(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt64(s[2..], 16);
        if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt64(s[2..], 2);
        if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt64(s[2..], 8);
        return long.Parse(s);
    }
    private static string ToBase(long n, int radix)
    {
        if (radix is < 2 or > 36) return n.ToString();
        if (n == 0) return "0";
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        bool neg = n < 0; ulong u = (ulong)Math.Abs(n); var sb = new StringBuilder();
        while (u > 0) { sb.Insert(0, digits[(int)(u % (ulong)radix)]); u /= (ulong)radix; }
        return (neg ? "-" : "") + sb;
    }
    private static readonly (int v, string s)[] RomanT = { (1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I") };
    private static string Roman(long n) { var sb = new StringBuilder(); foreach (var (v, s) in RomanT) while (n >= v) { sb.Append(s); n -= v; } return sb.ToString(); }
    private static string UnRoman(string s)
    {
        var map = new Dictionary<char, int> { ['I']=1,['V']=5,['X']=10,['L']=50,['C']=100,['D']=500,['M']=1000 };
        int total = 0, prev = 0;
        for (int i = s.Length - 1; i >= 0; i--) { if (!map.TryGetValue(s[i], out var v)) return "(not roman)"; total += v < prev ? -v : v; prev = v; }
        return total > 0 ? total.ToString() : "(not roman)";
    }
    private static readonly string[] Ones = { "zero","one","two","three","four","five","six","seven","eight","nine","ten","eleven","twelve","thirteen","fourteen","fifteen","sixteen","seventeen","eighteen","nineteen" };
    private static readonly string[] Tens = { "","","twenty","thirty","forty","fifty","sixty","seventy","eighty","ninety" };
    public static string NumWords(long n)
    {
        if (n == 0) return "zero";
        if (n < 0) return "negative " + NumWords(-n);
        var parts = new List<string>();
        void Chunk(long x) { if (x >= 100) { parts.Add(Ones[x / 100]); parts.Add("hundred"); x %= 100; } if (x >= 20) { parts.Add(Tens[x / 10]); x %= 10; } if (x > 0) parts.Add(Ones[x]); }
        foreach (var (scale, name) in new[] { (1_000_000_000L, "billion"), (1_000_000L, "million"), (1_000L, "thousand") })
            if (n >= scale) { Chunk(n / scale); parts.Add(name); n %= scale; }
        if (n > 0) Chunk(n);
        return string.Join(" ", parts);
    }

    // ============================ stats ============================
    public static double[] Numbers(string s) => Regex.Matches(s, @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
    public static string Stat(string op, string list)
    {
        var n = Numbers(list);
        if (n.Length == 0) return "0";
        switch (op)
        {
            case "sum": return Num(n.Sum());
            case "mean": return Num(n.Average());
            case "min": return Num(n.Min());
            case "max": return Num(n.Max());
            case "count": return n.Length.ToString();
            case "range": return Num(n.Max() - n.Min());
            case "median": { var o = n.OrderBy(x => x).ToArray(); int m = o.Length / 2; return Num(o.Length % 2 == 1 ? o[m] : (o[m - 1] + o[m]) / 2); }
            case "stdev": { double mean = n.Average(); return Num(Math.Sqrt(n.Sum(x => (x - mean) * (x - mean)) / n.Length)); }
            default: return "0";
        }
    }

    // ============================ IRC color ============================
    public static string IrcColor(string op, string s)
    {
        switch (op)
        {
            case "strip": return Regex.Replace(s, "\x03(\\d{1,2}(,\\d{1,2})?)?|[\x02\x1D\x1F\x16\x0F\x04]", "");
            case "rainbow": { int[] cols = { 4, 7, 8, 9, 3, 11, 12, 13, 6 }; var sb = new StringBuilder(); int i = 0; foreach (var ch in s) { if (ch == ' ') { sb.Append(' '); continue; } sb.Append('\x03').Append(cols[i++ % cols.Length].ToString("00")).Append(ch); } sb.Append('\x0F'); return sb.ToString(); }
            case "bold": return "\x02" + s + "\x02";
            case "italic": return "\x1D" + s + "\x1D";
            case "underline": return "\x1F" + s + "\x1F";
            default: return s;
        }
    }

    // ============================ pick ============================
    public static string Pick(string op, string list, string sep, int n, double rng)
    {
        var items = Split(list, sep);
        if (items.Length == 0) return "";
        switch (op)
        {
            case "random": return items[(int)(rng * items.Length) % items.Length];
            case "first": return items[0];
            case "last": return items[^1];
            case "nth": return n >= 1 && n <= items.Length ? items[n - 1] : "";
            case "count": return items.Length.ToString();
            default: return items[0];
        }
    }
    private static string[] Split(string s, string sep) => sep switch
    {
        "newline" => s.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
        "space" => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        "pipe" => s.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
        _ => s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
    };

    // ============================ date / time ============================
    public static string DateTime_(string op, string input, string fmt, string zone)
    {
        try
        {
            switch (op)
            {
                case "now": return Now(zone).ToString(fmt.Length > 0 ? fmt : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case "weekday": return ParseDate(input).DayOfWeek.ToString();
                case "format": return ParseDate(input).ToString(fmt.Length > 0 ? fmt : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "unix": return long.TryParse(input.Trim(), out var ts)
                        ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
                        : new DateTimeOffset(ParseDate(input), TimeSpan.Zero).ToUnixTimeSeconds().ToString();
                case "until": { var t = ParseDate(input) - DateTime.UtcNow; return Span(t); }
                case "age": { var b = ParseDate(input); int yrs = DateTime.UtcNow.Year - b.Year; if (DateTime.UtcNow < b.AddYears(yrs)) yrs--; return yrs + " years"; }
                default: return "";
            }
        }
        catch (Exception e) { return "(date error: " + e.Message + ")"; }
    }
    private static DateTime Now(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Equals("utc", StringComparison.OrdinalIgnoreCase)) return DateTime.UtcNow;
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone)); } catch { return DateTime.UtcNow; }
    }
    private static DateTime ParseDate(string s)
        => DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.UtcNow;
    private static string Span(TimeSpan t)
    {
        bool past = t.TotalSeconds < 0; t = t.Duration();
        string s = t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
        return past ? s + " ago" : "in " + s;
    }

    // ============================ random generators ============================
    private static readonly string[] Adjs = { "cosy","fluffy","sleepy","sunny","mellow","peppy","witty","brave","clever","jolly","snug","zippy","breezy","cheery","dapper","groovy" };
    private static readonly string[] Nouns = { "otter","maple","pebble","comet","biscuit","sprout","willow","ember","pixel","cloud","ferret","acorn","puffin","cactus","noodle","waffle" };
    private static readonly string[] First = { "Alex","Sam","Robin","Jordan","Casey","Riley","Quinn","Avery","Skyler","Morgan","Charlie","Frankie","River","Sage" };
    private static readonly string[] Last = { "Rivera","Okafor","Nguyen","Patel","Larsson","Costa","Haddad","Kim","Moreau","Bianchi","Novak","Tanaka","Flores","Adeyemi" };
    private static readonly string[] LoremW = { "lorem","ipsum","dolor","sit","amet","consectetur","adipiscing","elit","sed","do","eiusmod","tempor","incididunt","ut","labore","et","dolore","magna","aliqua","enim","ad","minim","veniam","quis" };

    /// <summary>Random generators (rng returns [0,1)). spec drives each op (dice notation, length, range, …).</summary>
    public static string Gen(string op, string spec, Func<double> rng)
    {
        int R(int n) => n <= 0 ? 0 : (int)(rng() * n) % n;
        switch (op)
        {
            case "coin": return rng() < 0.5 ? "heads" : "tails";
            case "uuid":
            {
                var b = new byte[16]; for (int i = 0; i < 16; i++) b[i] = (byte)R(256);
                b[6] = (byte)((b[6] & 0x0F) | 0x40); b[8] = (byte)((b[8] & 0x3F) | 0x80);
                string h = Convert.ToHexString(b).ToLowerInvariant();
                return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
            }
            case "username": return $"{Adjs[R(Adjs.Length)]}-{Nouns[R(Nouns.Length)]}{R(100)}";
            case "fakename": return $"{First[R(First.Length)]} {Last[R(Last.Length)]}";
            case "color": return $"#{R(256):x2}{R(256):x2}{R(256):x2}";
            case "int": { var (lo, hi) = Range2(spec, 1, 100); return (lo + R(hi - lo + 1)).ToString(); }
            case "lorem": { int n = int.TryParse(spec.Trim(), out var w) && w > 0 ? Math.Min(w, 200) : 24; var sb = new StringBuilder(); for (int i = 0; i < n; i++) sb.Append(i > 0 ? " " : "").Append(LoremW[R(LoremW.Length)]); var s = sb.ToString(); return char.ToUpperInvariant(s[0]) + s[1..] + "."; }
            case "password":
            {
                int len = int.TryParse(spec.Trim(), out var l) && l > 0 ? Math.Clamp(l, 4, 128) : 16;
                const string al = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%^&*-_+=";
                var sb = new StringBuilder(); for (int i = 0; i < len; i++) sb.Append(al[R(al.Length)]); return sb.ToString();
            }
            case "dice": return Dice(spec, R);
            case "gradient": return Gradient(spec);
            default: return "";
        }
    }
    // "#rrggbb #rrggbb [steps]" -> N hex colours blended from start to end
    private static string Gradient(string spec)
    {
        var hexes = Regex.Matches(spec ?? "", @"#?[0-9a-fA-F]{6}").Select(m => m.Value.TrimStart('#')).ToArray();
        if (hexes.Length < 2) return "(need two hex colours, e.g. #ff0000 #0000ff)";
        var sm = Regex.Match(spec ?? "", @"\b(\d{1,2})\b\s*$");
        int steps = sm.Success ? Math.Clamp(int.Parse(sm.Groups[1].Value), 2, 32) : 7;
        int Hx(string h, int o) => Convert.ToInt32(h.Substring(o, 2), 16);
        var (r1, g1, b1) = (Hx(hexes[0], 0), Hx(hexes[0], 2), Hx(hexes[0], 4));
        var (r2, g2, b2) = (Hx(hexes[1], 0), Hx(hexes[1], 2), Hx(hexes[1], 4));
        var outp = new List<string>();
        for (int i = 0; i < steps; i++) { double t = (double)i / (steps - 1); outp.Add($"#{(int)(r1 + (r2 - r1) * t):x2}{(int)(g1 + (g2 - g1) * t):x2}{(int)(b1 + (b2 - b1) * t):x2}"); }
        return string.Join(" ", outp);
    }
    private static (int, int) Range2(string spec, int dlo, int dhi)
    {
        var m = Regex.Match(spec ?? "", @"(-?\d+)\D+(-?\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var a) && int.TryParse(m.Groups[2].Value, out var b)) return (Math.Min(a, b), Math.Max(a, b));
        return (dlo, dhi);
    }
    private static string Dice(string spec, Func<int, int> R)
    {
        var m = Regex.Match((spec ?? "1d6").Trim().ToLowerInvariant().Replace(" ", ""), @"^(\d*)d(\d+)([+-]\d+)?$");
        if (!m.Success) return "format: NdM(+K), e.g. 2d6+3";
        int n = Math.Clamp(m.Groups[1].Value.Length > 0 ? int.Parse(m.Groups[1].Value) : 1, 1, 100);
        int sides = Math.Clamp(int.Parse(m.Groups[2].Value), 2, 1000);
        int mod = m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value) : 0;
        var rolls = new int[n]; int tot = mod; for (int i = 0; i < n; i++) { rolls[i] = R(sides) + 1; tot += rolls[i]; }
        string extra = (n > 1 || mod != 0) ? "  [" + string.Join("+", rolls) + (mod != 0 ? (mod > 0 ? "+" : "") + mod : "") + "]" : "";
        return $"\U0001F3B2 {spec} -> {tot}{extra}";   // intentional unicode (game die)
    }

    private static double ParseD(string s) => double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // tiny safe recursive-descent arithmetic evaluator (+ - * / % ^, parens, unary -, functions)
    private sealed class ExprEval
    {
        private readonly string _s; private int _i;
        public ExprEval(string s) { _s = s; }
        public double Parse() { double v = E(); Ws(); if (_i < _s.Length) throw new Exception("unexpected '" + _s[_i] + "'"); return v; }
        private void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
        private double E() { double v = T(); while (true) { Ws(); if (Eat('+')) v += T(); else if (Eat('-')) v -= T(); else return v; } }
        private double T() { double v = P(); while (true) { Ws(); if (Eat('*')) v *= P(); else if (Eat('/')) v /= P(); else if (Eat('%')) v %= P(); else return v; } }
        private double P() { double v = U(); Ws(); if (Eat('^')) return Math.Pow(v, P()); return v; }
        private double U() { Ws(); if (Eat('-')) return -U(); if (Eat('+')) return U(); return Atom(); }
        private double Atom()
        {
            Ws();
            if (Eat('(')) { double v = E(); Ws(); if (!Eat(')')) throw new Exception("missing )"); return v; }
            if (_i < _s.Length && (char.IsLetter(_s[_i]))) { int st = _i; while (_i < _s.Length && char.IsLetter(_s[_i])) _i++; string fn = _s[st.._i].ToLowerInvariant(); Ws(); if (Eat('(')) { double a = E(); Ws(); Eat(')'); return Fn(fn, a); } return Const(fn); }
            int s0 = _i; while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (_i == s0) throw new Exception("number expected");
            return double.Parse(_s[s0.._i], CultureInfo.InvariantCulture);
        }
        private static double Fn(string f, double a) => f switch { "sqrt"=>Math.Sqrt(a),"abs"=>Math.Abs(a),"round"=>Math.Round(a),"floor"=>Math.Floor(a),"ceil"=>Math.Ceiling(a),"sin"=>Math.Sin(a),"cos"=>Math.Cos(a),"tan"=>Math.Tan(a),"log"=>Math.Log10(a),"ln"=>Math.Log(a),"exp"=>Math.Exp(a), _=>throw new Exception("unknown fn " + f) };
        private static double Const(string c) => c switch { "pi"=>Math.PI,"e"=>Math.E, _=>throw new Exception("unknown name " + c) };
        private bool Eat(char c) { Ws(); if (_i < _s.Length && _s[_i] == c) { _i++; return true; } return false; }
    }
}
