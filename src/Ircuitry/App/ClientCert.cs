using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Ircuitry.App;

/// <summary>
/// A self-signed client certificate the bot presents during the TLS handshake so CertFP / SASL EXTERNAL work
/// with zero setup. One is generated per bot the first time it connects and persisted under the workspace
/// (~/ircuitry/certs); its SHA-256 fingerprint is the stable identity you register with services (e.g.
/// <c>/msg NickServ CERT ADD &lt;fp&gt;</c>). The user can override it any time by setting a Client Cert path in
/// the server settings.
/// </summary>
public static class ClientCert
{
    private static readonly object _lock = new();

    private static string Dir => Path.Combine(AppModel.WorkspaceDir, "certs");

    /// <summary>Path to a stable self-signed client cert for this identity, generating it on first use. Returns
    /// "" if it couldn't be created (e.g. a read-only filesystem) so the caller falls back to an anonymous TLS
    /// handshake.</summary>
    public static string EnsureDefault(string identity)
    {
        string id = Safe(identity);
        string path = Path.Combine(Dir, id + ".pfx");
        lock (_lock)
        {
            if (File.Exists(path)) return path;
            try
            {
                Directory.CreateDirectory(Dir);
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest("CN=" + id, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                // CertFP keys off the fingerprint, not validity, but make it long-lived so it never expires under you
                using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
                File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12));
                return path;
            }
            catch { return ""; }
        }
    }

    /// <summary>The SHA-256 CertFP fingerprint of a cert file (lower-case hex), or "" if it can't be read - this
    /// is what you register with the network's services to use SASL EXTERNAL.</summary>
    public static string Fingerprint(string path, string pass = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            using var cert = ext is ".pfx" or ".p12" ? new X509Certificate2(path, pass) : X509Certificate2.CreateFromPemFile(path);
            return cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        }
        catch { return ""; }
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in (s ?? "").Trim()) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var r = sb.ToString().Trim('-');
        return r.Length > 0 ? r : "ircuitry";
    }
}
