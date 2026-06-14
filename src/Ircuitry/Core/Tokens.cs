namespace Ircuitry.Core;

/// <summary>
/// Shared rule for what counts as a <c>{token}</c> in templating. A token name is non-empty and made
/// only of letters/digits/<c>_</c>/<c>.</c> - so real tokens like <c>{nick}</c> or <c>{args}</c> resolve,
/// while literal braces in JSON bodies, code (<c>{}</c>, <c>{"k":"v"}</c>, dicts, f-strings) are left
/// untouched instead of being silently eaten.
/// </summary>
public static class Tokens
{
    public static bool IsName(string s, int start, int end)
    {
        if (end <= start) return false;
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.')) return false;
        }
        return true;
    }
}
