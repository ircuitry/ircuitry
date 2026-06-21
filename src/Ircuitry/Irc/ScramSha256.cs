using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Ircuitry.Irc;

/// <summary>
/// Client side of SASL SCRAM-SHA-256 (RFC 5802 / RFC 7677): the password-based SASL mechanism that proves
/// knowledge of the password without ever putting it (or a reusable hash) on the wire, and that lets the
/// client verify the server too. No channel binding is used (gs2 header "n,," -> c=biws). Drive it step by
/// step: <see cref="ClientFirst"/> -> send; feed the server-first-message to <see cref="ClientFinal"/> ->
/// send; feed the server-final-message to <see cref="VerifyServerFinal"/>. Each method takes/returns the RAW
/// (un-base64'd) SASL payloads; the transport base64-encodes them.
/// </summary>
internal sealed class ScramSha256
{
    private readonly string _user;
    private readonly string _pass;
    private readonly string _cnonce;
    private string _clientFirstBare = "";
    private byte[] _saltedPassword = Array.Empty<byte>();
    private string _authMessage = "";

    public ScramSha256(string user, string pass)
        : this(user, pass, Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))) { }   // base64 has no ',' (SCRAM-legal nonce)

    // Test seam: a fixed client nonce, so the exchange can be checked against the RFC 7677 published vector.
    internal ScramSha256(string user, string pass, string nonce)
    {
        _user = Saslname(user ?? "");
        _pass = pass ?? "";
        _cnonce = nonce;
    }

    /// <summary>RFC 5802 username escaping: '=' -> =3D and ',' -> =2C (escape '=' first so we don't double-escape).</summary>
    private static string Saslname(string s) => s.Replace("=", "=3D").Replace(",", "=2C");

    public string ClientFirst()
    {
        _clientFirstBare = $"n={_user},r={_cnonce}";
        return "n,," + _clientFirstBare;   // gs2 header (no channel binding) + client-first-bare
    }

    /// <summary>Consume the server-first-message and build the client-final-message (carrying the proof).</summary>
    public string ClientFinal(string serverFirst)
    {
        var a = Parse(serverFirst);
        if (a.TryGetValue("e", out var err)) throw new Exception("SCRAM server error: " + err);
        string rnonce = a.GetValueOrDefault("r", "");
        if (!rnonce.StartsWith(_cnonce, StringComparison.Ordinal)) throw new Exception("SCRAM: server nonce did not extend the client nonce");
        if (!int.TryParse(a.GetValueOrDefault("i", ""), out int iters) || iters < 1) throw new Exception("SCRAM: bad iteration count");
        byte[] salt = Convert.FromBase64String(a.GetValueOrDefault("s", ""));

        _saltedPassword = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(_pass), salt, iters, HashAlgorithmName.SHA256, 32);
        byte[] clientKey = Hmac(_saltedPassword, "Client Key");
        byte[] storedKey = SHA256.HashData(clientKey);

        // c= is base64 of the gs2 header "n,," -> "biws"
        string clientFinalNoProof = "c=" + Convert.ToBase64String(Encoding.ASCII.GetBytes("n,,")) + ",r=" + rnonce;
        _authMessage = _clientFirstBare + "," + serverFirst + "," + clientFinalNoProof;

        byte[] clientSig = Hmac(storedKey, _authMessage);
        byte[] proof = Xor(clientKey, clientSig);
        return clientFinalNoProof + ",p=" + Convert.ToBase64String(proof);
    }

    /// <summary>Verify the server-final-message's v= signature; throws if it doesn't match (server impostor / wrong password).</summary>
    public void VerifyServerFinal(string serverFinal)
    {
        var a = Parse(serverFinal);
        if (a.TryGetValue("e", out var err)) throw new Exception("SCRAM server error: " + err);
        byte[] serverKey = Hmac(_saltedPassword, "Server Key");
        byte[] serverSig = Hmac(serverKey, _authMessage);
        if (a.GetValueOrDefault("v", "") != Convert.ToBase64String(serverSig))
            throw new Exception("SCRAM: server signature mismatch");
    }

    private static Dictionary<string, string> Parse(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in s.Split(','))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) d[part[..eq]] = part[(eq + 1)..];
        }
        return d;
    }

    private static byte[] Hmac(byte[] key, string msg)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(Encoding.UTF8.GetBytes(msg));
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }
}
