using System.Linq;

namespace Ircuitry.App;

/// <summary>How an eval case's expectation is checked against what the graph would send.</summary>
public enum EvalMatch { Contains, Exact, Regex, NoReply }

/// <summary>One golden-suite case: a fake inbound message and what the graph is expected to reply.
/// Run as a dry run (no IRC, throwaway state) so a suite can be checked any time without a live server.</summary>
public sealed class EvalCase
{
    public string Name = "case";
    public string Message = "!ping";
    public string Nick = "alice";
    public string Channel = "#test";
    public string Expect = "";
    public EvalMatch Mode = EvalMatch.Contains;

    public EvalCase Clone() => new() { Name = Name, Message = Message, Nick = Nick, Channel = Channel, Expect = Expect, Mode = Mode };

    /// <summary>Check the lines the graph would send against this case. Returns (pass, detail).</summary>
    public (bool pass, string detail) Evaluate(System.Collections.Generic.IReadOnlyList<string> sent)
    {
        switch (Mode)
        {
            case EvalMatch.NoReply:
                return sent.Count == 0 ? (true, "stayed silent") : (false, "expected silence, sent " + sent.Count);
            case EvalMatch.Exact:
                return sent.Any(s => s == Expect) ? (true, "exact match") : (false, "no line equalled the expected text");
            case EvalMatch.Regex:
                try
                {
                    var rx = new System.Text.RegularExpressions.Regex(Expect, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    return sent.Any(s => rx.IsMatch(s)) ? (true, "regex matched") : (false, "no line matched the pattern");
                }
                catch (System.Exception e) { return (false, "bad regex: " + e.Message); }
            default: // Contains
                return sent.Any(s => s.Contains(Expect, System.StringComparison.OrdinalIgnoreCase)) ? (true, "contains match") : (false, "no line contained the expected text");
        }
    }
}
