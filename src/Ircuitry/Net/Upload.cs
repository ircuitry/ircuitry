using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Ircuitry.Net;

/// <summary>
/// The binary HTTP the string-only <see cref="Http"/> helper can't do: download a URL to a file, and POST a
/// file as <c>multipart/form-data</c> to a filehost. This is the standard way to push an image - or a zipped
/// codebase - to a host that returns a link. Used by the media nodes and the Programmer AI's send_codebase.
/// </summary>
public static class Upload
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(120),
    };

    static Upload()
    {
        try { Client.DefaultRequestHeaders.UserAgent.ParseAdd("ircuitry/" + Ircuitry.App.AppInfo.Version); }
        catch { /* a malformed UA never blocks an upload */ }
    }

    /// <summary>Download a URL to a local file. Returns (ok, bytes-as-string or error message).</summary>
    public static (bool ok, string info) Download(string url, string destPath, long maxBytes = 64_000_000)
    {
        try
        {
            using var resp = Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return (false, "http " + (int)resp.StatusCode);
            if (resp.Content.Headers.ContentLength is long len && len > maxBytes) return (false, "file too big (" + len + " bytes)");

            string? dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var s = resp.Content.ReadAsStream();
            using var outFs = File.Create(destPath);
            var buf = new byte[81920];
            long total = 0; int r;
            while ((r = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += r;
                if (total > maxBytes) { outFs.Dispose(); try { File.Delete(destPath); } catch { } return (false, "exceeded size cap"); }
                outFs.Write(buf, 0, r);
            }
            return (true, total.ToString());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// POST a file to an endpoint as multipart/form-data - the way filehosts accept uploads.
    /// <paramref name="fieldName"/> is the form field carrying the file (e.g. "file", "fileToUpload");
    /// <paramref name="extraFields"/> become text fields; <paramref name="headers"/> add request headers
    /// (e.g. an auth token). Returns (status, response body or error); status 0 means the request never
    /// completed (network/IO error).
    /// </summary>
    public static (int status, string body) PostFile(
        string url, string filePath, string fieldName,
        IEnumerable<(string key, string val)>? extraFields = null,
        IEnumerable<(string key, string val)>? headers = null,
        string? contentType = null)
    {
        try
        {
            if (!File.Exists(filePath)) return (0, "no such file: " + filePath);

            using var form = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(File.ReadAllBytes(filePath));
            string ct = string.IsNullOrWhiteSpace(contentType) || !contentType.Contains('/') ? GuessContentType(filePath) : contentType;
            try { bytes.Headers.ContentType = new MediaTypeHeaderValue(ct); } catch { bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); }
            form.Add(bytes, string.IsNullOrEmpty(fieldName) ? "file" : fieldName, Path.GetFileName(filePath));
            if (extraFields != null)
                foreach (var (k, v) in extraFields)
                    if (!string.IsNullOrEmpty(k)) form.Add(new StringContent(v), k);

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            if (headers != null)
                foreach (var (k, v) in headers)
                    if (!string.IsNullOrEmpty(k)) req.Headers.TryAddWithoutValidation(k, v);

            using var resp = Client.Send(req, HttpCompletionOption.ResponseContentRead);
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
            return ((int)resp.StatusCode, body.Length > 0 ? body : "http " + (int)resp.StatusCode);
        }
        catch (Exception ex) { return (0, ex.Message); }
    }

    /// <summary>True for a 2xx status from <see cref="PostFile"/>.</summary>
    public static bool Ok(int status) => status is >= 200 and < 300;

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".zip" => "application/zip",
        ".json" => "application/json",
        ".txt" or ".md" => "text/plain",
        _ => "application/octet-stream",
    };
}
