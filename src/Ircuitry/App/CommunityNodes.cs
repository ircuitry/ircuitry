using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Ircuitry.Editor;
using Ircuitry.Graph;

namespace Ircuitry.App;

/// <summary>
/// Builds the community <c>.ircnode</c> library as composites of built-in nodes - RECIPES, never code blobs -
/// so every one can be dropped in and right-click -> Edit to see how it works. Emitted with
/// <c>--emit-community DIR</c>; each recipe overwrites <c>DIR/&lt;typeId&gt;.ircnode</c>.
/// </summary>
public static class CommunityNodes
{
    // ---- fluent builder --------------------------------------------------
    private sealed class Ctx
    {
        public readonly NodeGraph G = new();
        public readonly Node Fin;
        private float _ay = -40, _ny = -180;
        private readonly List<Node> _exec = new();
        private readonly List<(string name, Node node, int pin)> _outs = new();
        public readonly Dictionary<string, string> Exposed = new();

        public Ctx() { Fin = Add("flow.in", -460, -160); }
        public Node Add(string type, float x, float y) => G.Add(NodeCatalog.Get(type), new Vector2(x, y));
        public Node Arg(string name) { var n = Add("flow.arg", -460, _ay); _ay += 80; n.SetParam("name", name); return n; }
        public Node N(string type, params (string k, string v)[] ps) { var n = Add(type, -120, _ny); _ny += 110; foreach (var (k, v) in ps) n.SetParam(k, v); return n; }
        public void Wire(Node a, int ap, Node b, int bp) => G.Connect(a.Id, ap, b.Id, bp);
        public void Exec(Node n) => _exec.Add(n);                 // an exec node that must run before outputs
        public void Out(string name, Node n, int pin) => _outs.Add((name, n, pin));
        public Node Expose(Node n, string key, string def) { n.SetParam(key, "{" + key + "}"); Exposed[key] = def; return n; }
        public void Param(string key, string def) => Exposed[key] = def;   // a composite param used only via {token} in templates

        public void Finish()
        {
            Node prev = Fin;
            foreach (var n in _exec) { Wire(prev, 0, n, 0); prev = n; }   // exec chain through action/code/http nodes
            float ry = -160;
            foreach (var (name, node, pin) in _outs)
            {
                var ret = Add("flow.return", 320, ry); ry += 90; ret.SetParam("name", name);
                Wire(prev, 0, ret, 0);          // exec
                Wire(node, pin, ret, 1);        // value
            }
        }
    }

    private static string Make(string id, string title, string icon, string cat, string desc, Action<Ctx> build)
    {
        var c = new Ctx();
        build(c);
        c.Finish();
        return new GraphEditor(c.G).SerializeAsComposite(id, title, icon, cat, desc, c.Exposed) ?? "";
    }

    // common shape: one pure-data builtin, single text input -> single result
    private static string One(string id, string title, string icon, string cat, string desc, string arg, string builtin,
        (string, string)[] ps, (string key, string def)[] expose = null!, string outName = "result")
        => Make(id, title, icon, cat, desc, c =>
        {
            var a = arg != null ? c.Arg(arg) : null;
            var n = c.N(builtin, ps ?? Array.Empty<(string, string)>());
            if (expose != null) foreach (var (k, d) in expose) c.Expose(n, k, d);
            if (a != null) c.Wire(a, 0, n, 0);
            c.Out(outName, n, 0);
        });

    // a web call: optional input -> URL -> HTTP GET -> optional JSON pick -> optional format -> result
    private static string Web(string id, string title, string icon, string desc, string arg, string url, string jsonPath,
        string outTemplate = null!, bool encodeArg = false, (string key, string def)[] expose = null!, string headers = "", string outName = "result")
        => Make(id, title, icon, "Action", desc, c =>
        {
            var http = c.N("net.http", ("method", "GET"));
            if (headers.Length > 0) http.SetParam("headers", headers);
            if (arg != null)
            {
                var a = c.Arg(arg);
                Node urlSrc = a;
                if (encodeArg) { var enc = c.N("data.encode", ("op", "url"), ("mode", "encode")); c.Wire(a, 0, enc, 0); urlSrc = enc; }
                var fmt = c.N("data.format", ("template", url));   // {a} = the (encoded) argument
                c.Wire(urlSrc, 0, fmt, 0);
                c.Wire(fmt, 0, http, 1);                           // -> url input
            }
            else http.SetParam("url", url);
            if (expose != null) foreach (var (k, d) in expose) c.Expose(http, k, d);
            c.Exec(http);
            Node src = http; int pin = 1;                          // response
            if (jsonPath != null) { var dj = c.N("data.json", ("path", jsonPath)); c.Wire(http, 1, dj, 0); src = dj; pin = 0; }
            if (outTemplate != null) { var fo = c.N("data.format", ("template", outTemplate)); c.Wire(src, pin, fo, 0); src = fo; pin = 0; }
            c.Out(outName, src, pin);
        });

    // a confined code tool: arg -> code.* node (data input at index 1+) -> result. root is exposed.
    private static string CodeTool(string id, string title, string icon, string desc, string builtin,
        (string arg, int pin)[] argPins, int resultPin, string outName, (string, string)[] ps = null!)
        => Make(id, title, icon, "Code", desc, c =>
        {
            var n = c.N(builtin, ps ?? Array.Empty<(string, string)>());
            c.Expose(n, "root", "");
            foreach (var (arg, pin) in argPins) { var a = c.Arg(arg); c.Wire(a, 0, n, pin); }
            c.Exec(n);
            c.Out(outName, n, resultPin);
        });

    // ---- the library -----------------------------------------------------
    public static IEnumerable<(string file, string manifest)> All()
    {
        var L = new List<(string, string)>();
        void R(string id, string manifest) => L.Add((id + ".ircnode", manifest));

        // ===== encodings (Encode/Decode node) =====
        R("text.b64encode", One("text.b64encode", "Base64 Encode", "text-t", "Data", "Encodes text to Base64.", "text", "data.encode", new[] { ("op", "base64"), ("mode", "encode") }));
        R("text.b64decode", One("text.b64decode", "Base64 Decode", "text-t", "Data", "Decodes Base64 back to text.", "text", "data.encode", new[] { ("op", "base64"), ("mode", "decode") }));
        R("enc.base32", One("enc.base32", "Base32 Encode/Decode", "text-t", "Data", "Encodes or decodes Base32.", "text", "data.encode", new[] { ("op", "base32") }, new[] { ("mode", "encode") }));
        R("enc.hex", One("enc.hex", "Hex Encode/Decode", "math-operations", "Data", "Encodes text to hex or decodes it back.", "text", "data.encode", new[] { ("op", "hex") }, new[] { ("mode", "encode") }));
        R("text.urlencode", One("text.urlencode", "URL Encode", "link", "Data", "Percent-encodes text for use in a URL.", "text", "data.encode", new[] { ("op", "url"), ("mode", "encode") }));
        R("text.urldecode", One("text.urldecode", "URL Decode", "link", "Data", "Decodes a percent-encoded URL string.", "text", "data.encode", new[] { ("op", "url"), ("mode", "decode") }));
        R("enc.binary", One("enc.binary", "Binary Encode/Decode", "list-numbers", "Data", "Converts text to 0/1 binary and back.", "text", "data.encode", new[] { ("op", "binary") }, new[] { ("mode", "encode") }));
        R("enc.htmlescape", One("enc.htmlescape", "HTML Escape/Unescape", "file", "Data", "Escapes or unescapes HTML entities.", "text", "data.encode", new[] { ("op", "html") }, new[] { ("mode", "encode") }));
        R("text.rot13", One("text.rot13", "ROT13", "arrows-clockwise", "Data", "Applies the ROT13 letter cipher (its own inverse).", "text", "data.encode", new[] { ("op", "rot13"), ("mode", "encode") }));
        R("morse.encode", One("morse.encode", "Text to Morse", "radio", "Data", "Converts text to Morse code.", "text", "data.encode", new[] { ("op", "morse"), ("mode", "encode") }));
        R("morse.decode", One("morse.decode", "Morse to Text", "radio", "Data", "Converts Morse code back to text.", "text", "data.encode", new[] { ("op", "morse"), ("mode", "decode") }));
        R("enc.morsebeep", One("enc.morsebeep", "Morse Visualizer", "broadcast", "Data", "Shows text as Morse dots and dashes.", "text", "data.encode", new[] { ("op", "morse"), ("mode", "encode") }));
        R("text.zwsp", One("text.zwsp", "Invisible Ink (ZWSP)", "ghost", "Data", "Hides text as zero-width characters (encode), or reveals it (decode).", "text", "data.encode", new[] { ("op", "zwsp") }, new[] { ("mode", "encode") }));

        // ===== hashes =====
        R("hash.md5", One("hash.md5", "MD5 Hash", "lock-key", "Data", "MD5 hash of the input.", "text", "data.hash", new[] { ("op", "md5") }));
        R("hash.sha1", One("hash.sha1", "SHA1 Hash", "lock-key", "Data", "SHA-1 hash of the input.", "text", "data.hash", new[] { ("op", "sha1") }));
        R("hash.sha256", One("hash.sha256", "SHA-256 Hash", "lock-key", "Data", "SHA-256 hash of the input.", "text", "data.hash", new[] { ("op", "sha256") }));
        R("enc.crc32", One("enc.crc32", "CRC32 Checksum", "lock-key", "Data", "CRC32 checksum of the input.", "text", "data.hash", new[] { ("op", "crc32") }));

        // ===== case / reshaping =====
        R("text.case", One("text.case", "Change Case", "text-aa", "Data", "Recases text (set the style).", "text", "data.case", null!, new[] { ("op", "upper") }));
        R("text.titlecase", One("text.titlecase", "Smart Title Case", "text-aa", "Data", "Title-cases text.", "text", "data.case", new[] { ("op", "title") }));
        R("text.mock", One("text.mock", "Mock Case", "text-aa", "Data", "mOcKiNg SpOnGeBoB case.", "text", "data.case", new[] { ("op", "mock") }));
        R("text.spongebob", One("text.spongebob", "Caps Alternator", "eraser", "Data", "AlTeRnAtInG caps.", "text", "data.case", new[] { ("op", "mock") }));
        R("text.leet", One("text.leet", "Leetspeak", "floppy-disk", "Data", "Converts letters to l33t numerals.", "text", "data.case", new[] { ("op", "leet") }));
        // intentional unicode (clap) in the node description text
        R("fun.clap", One("fun.clap", "Clap Emojify", "hands-clapping", "Data", "Inserts \U0001F44F between \U0001F44F words.", "text", "data.case", new[] { ("op", "clap") }));

        // ===== shape =====
        R("text.reverse", One("text.reverse", "Reverse Text", "arrow-bend-up-left", "Data", "Reverses the characters.", "text", "data.shape", new[] { ("op", "reverse") }));
        R("text.trim", One("text.trim", "Trim Text", "scissors", "Data", "Trims surrounding whitespace.", "text", "data.shape", new[] { ("op", "trim") }));
        R("text.repeat", One("text.repeat", "Repeat Text", "repeat", "Data", "Repeats text N times.", "text", "data.shape", new[] { ("op", "repeat") }, new[] { ("n", "3") }));
        R("text.pad", One("text.pad", "Pad Text", "ruler", "Data", "Pads text to a width.", "text", "data.shape", new[] { ("op", "padleft") }, new[] { ("n", "10"), ("fill", " ") }));
        R("text.truncate", One("text.truncate", "Truncate Text", "scissors", "Data", "Truncates to N characters with an ellipsis.", "text", "data.shape", new[] { ("op", "truncate") }, new[] { ("n", "80") }));
        R("text.slug", One("text.slug", "Slugify", "link", "Data", "Makes a url-safe slug.", "text", "data.shape", new[] { ("op", "slug") }));
        R("text.acronym", One("text.acronym", "Acronym Maker", "translate", "Data", "First letters of each word.", "text", "data.shape", new[] { ("op", "acronym") }));
        R("text.vowels", One("text.vowels", "Disemvowel", "prohibit", "Data", "Removes the vowels.", "text", "data.shape", new[] { ("op", "disemvowel") }));
        R("text.numbering", One("text.numbering", "Number a List", "hash", "Data", "Numbers each line.", "text", "data.shape", new[] { ("op", "numbering") }));
        R("text.wrap", One("text.wrap", "Word Wrap", "file-text", "Data", "Wraps text to a column width.", "text", "data.shape", new[] { ("op", "wrap") }, new[] { ("n", "40") }));
        R("text.zalgo", One("text.zalgo", "Zalgo Text", "fish", "Data", "Adds c̷r̷e̷e̷p̷y̷ combining marks.", "text", "data.shape", new[] { ("op", "zalgo") }, new[] { ("n", "3") }));
        R("gen.ascii", One("gen.ascii", "ASCII Banner", "text-aa", "Data", "Wraps text in a simple ASCII box.", "text", "data.shape", new[] { ("op", "banner") }));

        // ===== regex / parse =====
        R("text.replace", One("text.replace", "Find & Replace", "repeat", "Data", "Regex find & replace.", "text", "data.regex", new[] { ("op", "replace") }, new[] { ("pattern", ""), ("replace", ""), ("flags", "") }));
        R("text.mdstrip", One("text.mdstrip", "Strip Markdown", "note-pencil", "Data", "Removes common Markdown markers.", "text", "data.regex", new[] { ("op", "replace"), ("pattern", "[*_`~#>]+"), ("replace", ""), ("flags", "m") }));
        R("irc.stripcolor", One("irc.stripcolor", "Strip IRC Colors", "palette", "Ircv3", "Removes IRC colour/format codes.", "text", "irc.color", new[] { ("op", "strip") }));
        R("parse.csvfield", One("parse.csvfield", "CSV Field", "knife", "Data", "Picks the Nth comma-separated field.", "row", "data.pick", new[] { ("op", "nth"), ("sep", "comma") }, new[] { ("n", "1") }));
        R("parse.lines", One("parse.lines", "Pick Line", "file-text", "Data", "Picks the Nth line.", "text", "data.pick", new[] { ("op", "nth"), ("sep", "newline") }, new[] { ("n", "1") }));
        R("parse.jsonpath", One("parse.jsonpath", "JSON Field Picker", "puzzle-piece", "Data", "Reads a field from JSON by dotted path.", "json", "data.json", null!, new[] { ("path", "") }, "result"));

        // multi-output parsers (regex)
        R("parse.urls", Make("parse.urls", "Extract URLs", "link", "Filter", "Pulls all URLs out of text.", c =>
        { var a = c.Arg("text"); var all = c.N("data.regex", ("op", "all"), ("pattern", @"https?://[^\s]+")); var cnt = c.N("data.regex", ("op", "count"), ("pattern", @"https?://[^\s]+")); c.Wire(a, 0, all, 0); c.Wire(a, 0, cnt, 0); c.Out("result", all, 0); c.Out("count", cnt, 0); }));
        R("parse.emails", Make("parse.emails", "Extract Emails", "envelope", "Filter", "Pulls all email addresses out of text.", c =>
        { var a = c.Arg("text"); var all = c.N("data.regex", ("op", "all"), ("pattern", @"[\w.+-]+@[\w-]+\.[\w.-]+")); var cnt = c.N("data.regex", ("op", "count"), ("pattern", @"[\w.+-]+@[\w-]+\.[\w.-]+")); c.Wire(a, 0, all, 0); c.Wire(a, 0, cnt, 0); c.Out("result", all, 0); c.Out("count", cnt, 0); }));
        R("parse.numbers", Make("parse.numbers", "Extract Numbers", "hash", "Filter", "Pulls all numbers out of text and sums them.", c =>
        { var a = c.Arg("text"); var all = c.N("data.regex", ("op", "all"), ("pattern", @"-?\d+(\.\d+)?")); var sum = c.N("data.stats", ("op", "sum")); c.Wire(a, 0, all, 0); c.Wire(a, 0, sum, 0); c.Out("result", all, 0); c.Out("sum", sum, 0); }));
        R("parse.hashtags", Make("parse.hashtags", "Extract Hashtags & Mentions", "hash", "Filter", "Pulls #hashtags and @mentions out of text.", c =>
        { var a = c.Arg("text"); var h = c.N("data.regex", ("op", "all"), ("pattern", @"#\w+")); var m = c.N("data.regex", ("op", "all"), ("pattern", @"@\w+")); c.Wire(a, 0, h, 0); c.Wire(a, 0, m, 0); c.Out("hashtags", h, 0); c.Out("mentions", m, 0); }));

        // ===== math / convert =====
        R("calc.eval", One("calc.eval", "Calculator", "calculator", "Data", "Evaluates an arithmetic expression.", "expr", "data.mathx", Array.Empty<(string, string)>()));
        R("calc.percent", One("calc.percent", "Percentage", "percent", "Data", "Percentage math - e.g. 80*15/100.", "expr", "data.mathx", Array.Empty<(string, string)>()));
        R("calc.tip", One("calc.tip", "Tip Split", "receipt", "Data", "Tip math - e.g. 80*1.18/4 for 18% split 4 ways.", "expr", "data.mathx", Array.Empty<(string, string)>()));
        R("calc.temp", One("calc.temp", "Temperature Convert", "thermometer", "Data", "Converts a temperature between c / f / k.", "value", "data.convert", new[] { ("family", "temperature") }, new[] { ("from", "c"), ("to", "f") }));
        R("calc.units", One("calc.units", "Unit Convert", "ruler", "Data", "Converts a value between units.", "value", "data.convert", null!, new[] { ("family", "length"), ("from", "km"), ("to", "mi") }));
        R("calc.base", One("calc.base", "Base Converter", "hash", "Data", "Converts a number to another base.", "number", "num.format", new[] { ("op", "base") }, new[] { ("radix", "16") }));
        R("calc.roman", One("calc.roman", "Roman Numerals", "bank", "Data", "Converts a number to Roman numerals.", "value", "num.format", new[] { ("op", "roman") }));

        // ===== numbers =====
        R("num.factorial", One("num.factorial", "Factorial", "hash", "Data", "n! factorial.", "n", "num.theory", new[] { ("op", "factorial") }));
        R("num.fibonacci", One("num.fibonacci", "Fibonacci Number", "hash", "Data", "The nth Fibonacci number.", "n", "num.theory", new[] { ("op", "fibonacci") }));
        R("num.ordinal", One("num.ordinal", "Ordinal Suffix", "hash", "Data", "1 -> 1st, 22 -> 22nd, etc.", "n", "num.theory", new[] { ("op", "ordinal") }));
        R("num.primes", One("num.primes", "Prime Check", "hash", "Data", "Whether n is prime.", "n", "num.theory", new[] { ("op", "isprime") }));
        R("num.round", One("num.round", "Round Number", "hash", "Data", "Rounds to the nearest integer.", "value", "num.theory", new[] { ("op", "round") }));
        R("num.basen", One("num.basen", "Number Base Converter", "hash", "Data", "Converts a number to another base.", "value", "num.format", new[] { ("op", "base") }, new[] { ("radix", "2") }));
        R("num.words", One("num.words", "Number to Words", "hash", "Data", "123 -> one hundred twenty three.", "number", "num.format", new[] { ("op", "words") }));
        R("num.gcd", Make("num.gcd", "GCD & LCM", "hash", "Data", "Greatest common divisor and least common multiple of two numbers.", c =>
        { var a = c.Arg("a"); var b = c.Arg("b"); var g = c.N("num.theory", ("op", "gcd")); var lc = c.N("num.theory", ("op", "lcm")); c.Wire(a, 0, g, 0); c.Wire(b, 0, g, 1); c.Wire(a, 0, lc, 0); c.Wire(b, 0, lc, 1); c.Out("gcd", g, 0); c.Out("lcm", lc, 0); }));
        R("num.clamp", Make("num.clamp", "Clamp Number", "hash", "Data", "Clamps a number between a low and high bound.", c =>
        { var v = c.Arg("value"); var lo = c.N("data.math", ("op", "max")); var hi = c.N("data.math", ("op", "min")); c.Expose(lo, "lo", "0"); c.Expose(hi, "hi", "100"); c.Wire(v, 0, lo, 0); c.Wire(lo, 0, hi, 0); c.Out("result", hi, 0); }));
        R("num.percentchange", Make("num.percentchange", "Percent Change", "chart-line-up", "Data", "Percent change from old to new.", c =>
        { var o = c.Arg("old"); var n = c.Arg("new"); var diff = c.N("data.math", ("op", "-")); var div = c.N("data.math", ("op", "÷")); var mul = c.N("data.math", ("op", "×"), ("b", "100")); c.Wire(n, 0, diff, 0); c.Wire(o, 0, diff, 1); c.Wire(diff, 0, div, 0); c.Wire(o, 0, div, 1); c.Wire(div, 0, mul, 0); c.Out("result", mul, 0); }));
        R("num.stats", Make("num.stats", "Number Stats", "chart-bar", "Data", "Mean, median, min, max and sum of a list of numbers.", c =>
        { var a = c.Arg("numbers"); foreach (var op in new[] { "mean", "median", "min", "max", "sum" }) { var s = c.N("data.stats", ("op", op)); c.Wire(a, 0, s, 0); c.Out(op, s, 0); } }));

        // ===== date / time =====
        R("time.now", One("time.now", "Current Time", "clock", "Data", "The current date and time.", null!, "data.datetime", new[] { ("op", "now") }, new[] { ("format", "yyyy-MM-dd HH:mm:ss"), ("zone", "UTC") }));
        R("time.tz", One("time.tz", "Time in Timezone", "globe-hemisphere-west", "Data", "The current time in a timezone.", null!, "data.datetime", new[] { ("op", "now") }, new[] { ("zone", "Europe/London") }));
        R("date.weekday", One("date.weekday", "Day of Week", "calendar", "Data", "The weekday of a date.", "date", "data.datetime", new[] { ("op", "weekday") }));
        R("time.unix", One("time.unix", "Unix Timestamp", "timer", "Data", "Converts to/from a unix timestamp.", "ts", "data.datetime", new[] { ("op", "unix") }));
        R("time.until", One("time.until", "Countdown", "hourglass", "Data", "How long until a date/time.", "when", "data.datetime", new[] { ("op", "until") }));
        R("time.age", One("time.age", "Age Calculator", "cake", "Data", "Age in years from a birthdate.", "birthdate", "data.datetime", new[] { ("op", "age") }));
        R("date.countdown", Make("date.countdown", "Countdown", "hourglass", "Data", "Time and days until a target date.", c =>
        { var a = c.Arg("target"); var dt = c.N("data.datetime", ("op", "until")); var days = c.N("data.regex", ("op", "first"), ("pattern", @"\d+")); c.Wire(a, 0, dt, 0); c.Wire(dt, 0, days, 0); c.Out("result", dt, 0); c.Out("days", days, 0); }));

        // ===== random / fun =====
        R("coinflip", One("coinflip", "Coin Flip", "coin", "Data", "Heads or tails.", null!, "gen.random", new[] { ("op", "coin") }));
        R("fun.dice", One("fun.dice", "Dice Roller", "dice-five", "Data", "Rolls dice in NdM+K notation.", "dice", "gen.random", new[] { ("op", "dice") }));
        R("gen.dice", One("gen.dice", "Dice Notation Roller", "dice-five", "Data", "Rolls dice in NdM+K notation.", "notation", "gen.random", new[] { ("op", "dice") }, null!, "total"));
        R("gen.password", One("gen.password", "Password Generator", "key", "Data", "A random password of a given length.", null!, "gen.random", new[] { ("op", "password") }, new[] { ("spec", "16") }));
        R("gen.uuid", One("gen.uuid", "UUID", "identification-badge", "Data", "A random UUID v4.", null!, "gen.random", new[] { ("op", "uuid") }));
        R("gen.username", One("gen.username", "Username Generator", "hand-palm", "Data", "A cute random username.", null!, "gen.random", new[] { ("op", "username") }));
        R("gen.fakename", One("gen.fakename", "Fake Name", "identification-card", "Data", "A random person name.", null!, "gen.random", new[] { ("op", "fakename") }));
        R("gen.color", One("gen.color", "Random Color", "palette", "Data", "A random hex colour.", null!, "gen.random", new[] { ("op", "color") }, null!, "hex"));
        R("gen.lorem", One("gen.lorem", "Lorem Ipsum", "scroll", "Data", "Lorem ipsum placeholder text.", null!, "gen.random", new[] { ("op", "lorem") }, new[] { ("spec", "24") }));
        R("gen.gradient", Make("gen.gradient", "Color Gradient", "rainbow", "Data", "Blends a range of hex colours between two endpoints.", c =>
        { var s = c.Arg("start"); var e = c.Arg("end"); var fmt = c.N("data.format", ("template", "{a} {b} 7")); var grad = c.N("gen.random", ("op", "gradient")); c.Wire(s, 0, fmt, 0); c.Wire(e, 0, fmt, 1); c.Wire(fmt, 0, grad, 0); c.Out("result", grad, 0); }));
        R("gen.qrurl", Make("gen.qrurl", "QR Code URL", "square", "Data", "Builds a QR-code image URL for the text.", c =>
        { var a = c.Arg("text"); var enc = c.N("data.encode", ("op", "url"), ("mode", "encode")); var fmt = c.N("data.format", ("template", "https://api.qrserver.com/v1/create-qr-code/?size=240x240&data={a}")); c.Wire(a, 0, enc, 0); c.Wire(enc, 0, fmt, 0); c.Out("url", fmt, 0); }));
        R("gen.placeholder", One("gen.placeholder", "Placeholder Image URL", "image", "Data", "A placeholder image URL of a given size.", null!, "data.format", new[] { ("template", "https://placehold.co/{size}") }, new[] { ("size", "600x400") }, "url"));

        R("fun.choose", One("fun.choose", "Choose One", "target", "Data", "Picks one of the comma-separated options.", "options", "data.pick", new[] { ("op", "random"), ("sep", "comma") }));
        R("fun.decide", One("fun.decide", "Decide", "smiley-meh", "Data", "Yes, no, or maybe.", null!, "data.random", new[] { ("options", "yes\nno\nmaybe\ndefinitely\nno way\nask again later") }));
        R("fun.8ball", One("fun.8ball", "Magic 8-Ball", "magic-wand", "Data", "Classic Magic 8-Ball answers.", null!, "data.random", new[] { ("options", "It is certain\nWithout a doubt\nYes definitely\nMost likely\nOutlook good\nReply hazy, try again\nAsk again later\nDon't count on it\nMy reply is no\nVery doubtful") }));
        R("fun.fortune", One("fun.fortune", "Fortune Cookie", "cookie", "Data", "A random fortune.", null!, "data.random", new[] { ("options", "A pleasant surprise is waiting for you.\nFortune favours the bold.\nYou will find what you seek.\nGood news will come to you by mail.\nA friend asks only for your time, not your money.\nNow is the time to try something new.") }));
        R("fun.compliment", One("fun.compliment", "Compliment", "heart", "Data", "A random pick-me-up.", null!, "data.random", new[] { ("options", "You're doing great!\nYour code compiles on the first try.\nYou light up the channel.\nYou're sharper than a tack.\nEveryone's glad you're here.") }));
        R("fun.insult", One("fun.insult", "Playful Insult", "smiley-wink", "Data", "A gentle, playful jab.", null!, "data.random", new[] { ("options", "You absolute muffin.\nYou couldn't ping your way out of a paper bag.\nYour bashrc is showing.\nNice try, butterfingers.\nYou call that a regex?") }));
        R("fun.rps", One("fun.rps", "Rock Paper Scissors", "hand-fist", "Data", "The bot throws rock, paper, or scissors.", null!, "data.random", new[] { ("options", "rock\npaper\nscissors") }));

        // ===== irc =====
        R("irc.rainbow", One("irc.rainbow", "Rainbow Text", "rainbow", "Ircv3", "Colours text in rainbow IRC colours.", "text", "irc.color", new[] { ("op", "rainbow") }));

        // ===== counts =====
        R("text.count", Make("text.count", "Count Characters & Words", "hash", "Data", "Counts characters, words and lines.", c =>
        { var a = c.Arg("text"); var ch = c.N("data.shape", ("op", "length")); var wd = c.N("data.regex", ("op", "count"), ("pattern", @"\S+")); var ln = c.N("data.regex", ("op", "count"), ("pattern", "^"), ("flags", "m")); c.Wire(a, 0, ch, 0); c.Wire(a, 0, wd, 0); c.Wire(a, 0, ln, 0); c.Out("chars", ch, 0); c.Out("words", wd, 0); c.Out("lines", ln, 0); }));
        R("wordcount", Make("wordcount", "Word Count", "hash", "Data", "Counts words and characters.", c =>
        { var a = c.Arg("text"); var wd = c.N("data.regex", ("op", "count"), ("pattern", @"\S+")); var ch = c.N("data.shape", ("op", "length")); c.Wire(a, 0, wd, 0); c.Wire(a, 0, ch, 0); c.Out("words", wd, 0); c.Out("chars", ch, 0); }));

        // ===== web (HTTP + JSON) =====
        R("web.dadjoke", Web("web.dadjoke", "Dad Joke", "smiley", "A random dad joke.", null!, "https://icanhazdadjoke.com/", "joke", headers: "Accept: application/json"));
        R("web.advice", Web("web.advice", "Advice", "lightbulb", "A random piece of advice.", null!, "https://api.adviceslip.com/advice", "slip.advice"));
        R("web.fact", Web("web.fact", "Random Fact", "brain", "A random useless fact.", null!, "https://uselessfacts.jsph.pl/api/v2/facts/random", "text"));
        R("web.wiki", Web("web.wiki", "Wikipedia", "books", "The summary of a Wikipedia topic.", "topic", "https://en.wikipedia.org/api/rest_v1/page/summary/{a}", "extract", encodeArg: true));
        R("web.define", Web("web.define", "Dictionary", "book-open", "The definition of a word.", "word", "https://api.dictionaryapi.dev/api/v2/entries/en/{a}", "0.meanings.0.definitions.0.definition", encodeArg: true));
        R("web.urban", Web("web.urban", "Urban Dictionary", "chat-circle", "An Urban Dictionary definition.", "term", "https://api.urbandictionary.com/v0/define?term={a}", "list.0.definition", encodeArg: true));
        R("web.crypto", Web("web.crypto", "Crypto Price", "coin", "A coin's price in USD.", "coin", "https://api.coingecko.com/api/v3/simple/price?ids={a}&vs_currencies=usd", null!, encodeArg: true));
        R("web.iplookup", Web("web.iplookup", "IP / Host Lookup", "globe", "Geo/ISP info for an IP or host.", "host", "http://ip-api.com/json/{a}", null!, encodeArg: true));
        R("web.shorten", Web("web.shorten", "Shorten URL", "link", "Shortens a URL via is.gd.", "url", "https://is.gd/create.php?format=simple&url={a}", null!, encodeArg: true));
        R("web.weather", Web("web.weather", "Weather", "cloud-sun", "Current weather for a place (wttr.in).", "place", "https://wttr.in/{a}?format=3", null!, encodeArg: true));
        R("web.currency", Web("web.currency", "Currency Convert", "currency-dollar", "Exchange rate lookup (set the URL for your provider).", "query", "https://api.frankfurter.app/latest?{a}", null!, encodeArg: false));
        R("web.search", Web("web.search", "Web Search", "magnifying-glass", "Searches DuckDuckGo's instant-answer API.", "query", "https://api.duckduckgo.com/?q={a}&format=json&no_html=1", "AbstractText", encodeArg: true, outName: "results"));

        // ===== github =====
        R("gh.api", Web("gh.api", "GitHub: API", "github-logo", "Calls the GitHub REST API. Set a token header for private data.", "endpoint", "https://api.github.com{a}", null!, encodeArg: false, expose: new[] { ("headers", "Accept: application/vnd.github+json") }, outName: "json"));
        R("gh.run", Make("gh.run", "GitHub: Run gh", "github-logo", "Code", "Runs the gh CLI in your repo (e.g. 'pr list'). Set the codebase folder.", c =>
        { var a = c.Arg("ghargs"); var fmt = c.N("data.format", ("template", "gh {a}")); var sh = c.N("code.shell"); c.Expose(sh, "root", ""); c.Wire(a, 0, fmt, 0); c.Wire(fmt, 0, sh, 1); c.Exec(sh); c.Out("output", sh, 1); }));

        R("gh.pr.list", Make("gh.pr.list", "GitHub: List PRs", "github-logo", "Code", "Lists open pull requests via the gh CLI. Set the codebase folder.", c =>
        { var sh = c.N("code.shell", ("command", "gh pr list")); c.Expose(sh, "root", ""); c.Exec(sh); c.Out("prs", sh, 1); }));
        R("gh.issue.create", Make("gh.issue.create", "GitHub: Create Issue", "github-logo", "Code", "Creates a GitHub issue via the gh CLI. Set the codebase folder.", c =>
        { var t = c.Arg("title"); var b = c.Arg("body"); var fmt = c.N("data.format", ("template", "gh issue create --title \"{a}\" --body \"{b}\"")); var sh = c.N("code.shell"); c.Expose(sh, "root", ""); c.Wire(t, 0, fmt, 0); c.Wire(b, 0, fmt, 1); c.Wire(fmt, 0, sh, 1); c.Exec(sh); c.Out("url", sh, 1); }));
        R("gh.comment", Make("gh.comment", "GitHub: Comment", "github-logo", "Code", "Comments on an issue/PR via the gh CLI. Set the codebase folder and the issue/PR number.", c =>
        { var b = c.Arg("body"); c.Param("target", "1"); var fmt = c.N("data.format", ("template", "gh issue comment {target} --body \"{a}\"")); var sh = c.N("code.shell"); c.Expose(sh, "root", ""); c.Wire(b, 0, fmt, 0); c.Wire(fmt, 0, sh, 1); c.Exec(sh); c.Out("result", sh, 1); }));

        // ===== tools (confined code.*) =====
        R("tool.readfile", CodeTool("tool.readfile", "Read File", "folder-open", "Reads a file from the codebase folder.", "code.read", new[] { ("file", 1) }, 2, "content"));
        R("tool.editfile", CodeTool("tool.editfile", "Edit File", "pencil", "Find/replace in a file in the codebase folder.", "code.edit", new[] { ("file", 1), ("find", 2), ("replace", 3) }, 1, "result"));
        R("tool.listfiles", CodeTool("tool.listfiles", "List Files", "folder", "Lists files in the codebase folder by glob.", "code.glob", new[] { ("glob", 1) }, 1, "files"));
        R("tool.search", CodeTool("tool.search", "Search Files (grep)", "magnifying-glass", "Greps the codebase folder for a pattern.", "code.grep", new[] { ("pattern", 1) }, 1, "matches"));
        R("tool.run", CodeTool("tool.run", "Run Command", "keyboard", "Runs a command in the codebase folder.", "code.shell", new[] { ("cmd", 1) }, 1, "stdout"));

        // the image -> filehost recipe (HTTP multipart) - kept under its own filename
        L.Add(("filehost-image.ircnode", FilehostImage()));

        return L;
    }

    /// <summary>image -> HTTP Request (multipart) -> the returned link. A deconstructible recipe of real nodes.</summary>
    public static string FilehostImage() => Make("filehost.image", "Push Image to Filehost", "image", "Storage",
        "Uploads an image (a path under ~/ircuitry/files - e.g. from a Download File node) to a filehost via standard " +
        "multipart/form-data and outputs the returned link. It is just an HTTP Request node in multipart mode, so " +
        "right-click -> Edit to see how it works or point it at your own host. Defaults to 0x0.st; for catbox set " +
        "endpoint https://catbox.moe/user/api.php, field fileToUpload, and add a form field 'reqtype=fileupload'.",
        c =>
        {
            var img = c.Arg("image");
            var http = c.N("net.http", ("method", "POST"), ("send", "file (multipart)"));
            c.Expose(http, "endpoint", "https://0x0.st"); http.SetParam("url", "{endpoint}");
            c.Expose(http, "field", "file");
            c.Expose(http, "headers", "");
            c.Expose(http, "fields", ""); http.SetParam("body", "{fields}");
            c.Wire(img, 0, http, 2);     // image path -> file input
            c.Exec(http);
            c.Out("url", http, 1);       // response (the link)
        });
}
