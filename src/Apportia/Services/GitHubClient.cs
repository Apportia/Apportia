using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Apportia.Services;

/// Unified access point for every GitHub interaction in the app. Primary path uses the
/// REST API; when that is rate-limited or unreachable we fall back to the Atom release
/// feed or raw.githubusercontent.com. Optional GITHUB_TOKEN env var lifts the API limit
/// from 60/h to 5000/h.
public static class GitHubClient
{
    private static readonly HttpClient Http;

    static GitHubClient()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Wget", "1.25"));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// Fetches a UTF-8 text file from a repo. Prefers the API contents endpoint (verifies the
    /// blob SHA); falls back to raw.githubusercontent.com without SHA verification when the
    /// API fails or is rate-limited.
    public static async Task<string?> FetchFileContentAsync(
        string repo,
        string path,
        string branch = "main",
        CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{repo}/contents/{path}?ref={branch}", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                if (JsonSerializer.Deserialize(json, GitHubJsonContext.Default.GhContentResponse)
                    is { Encoding: "base64" } content)
                {
                    var bytes = Convert.FromBase64String(content.Content.Replace("\n", "").Replace("\r", ""));
                    if (VerifyBlobSha(bytes, content.Sha))
                        return Encoding.UTF8.GetString(bytes);
                }
            }
            else if (!IsRateLimited(response))
            {
                return null;
            }
        }
        catch
        {
            /* fall through to raw */
        }

        try
        {
            return await Http.GetStringAsync($"https://raw.githubusercontent.com/{repo}/{branch}/{path}", ct);
        }
        catch
        {
            return null;
        }
    }

    /// Latest release for the repo. Tries the API first, then the Atom feed as fallback
    /// (Atom entries carry no asset list; callers must reconstruct download URLs).
    public static async Task<GhRelease?> FetchLatestReleaseAsync(string repo, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize(json, GitHubJsonContext.Default.GhRelease);
            }
        }
        catch
        {
            /* fall through to atom */
        }

        var atom = await FetchAtomReleasesAsync(repo, ct);
        return atom.Count > 0 ? atom[0] : null;
    }

    /// Releases for the repo, newest first. Tries the API first, then the Atom feed.
    /// Atom entries carry no asset list; callers must reconstruct download URLs when
    /// releases are returned from the fallback path.
    public static async Task<IReadOnlyList<GhRelease>> FetchReleasesAsync(
        string repo, int perPage = 30, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{repo}/releases?per_page={perPage}", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                if (JsonSerializer.Deserialize(json, GitHubJsonContext.Default.GhReleaseArray) is { } releases)
                    return releases;
            }
        }
        catch
        {
            /* fall through to atom */
        }

        return await FetchAtomReleasesAsync(repo, ct);
    }

    /// Streams a binary asset with progress. Uses the shared client so Wget UA and any
    /// token are applied uniformly. Returns null on any error.
    public static async Task<bool> DownloadAssetAsync(
        string url,
        string destPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                    progress?.Report(downloaded / (double)total);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// Fetches a binary asset and returns its bytes. Returns null on any error.
    public static async Task<byte[]?> GetAssetBytesAsync(string url, CancellationToken ct = default)
    {
        try
        {
            return await Http.GetByteArrayAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<GhRelease>> FetchAtomReleasesAsync(string repo, CancellationToken ct)
    {
        try
        {
            var xml = await Http.GetStringAsync($"https://github.com/{repo}/releases.atom", ct);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://www.w3.org/2005/Atom";
            return doc.Descendants(ns + "entry")
                      .Select(entry => new GhRelease
                      {
                          TagName = ExtractTag(entry.Element(ns + "id")?.Value ?? string.Empty),
                          Body = entry.Element(ns + "content")?.Value ?? string.Empty,
                          Assets = []
                      })
                      .Where(r => r.TagName.Length > 0)
                      .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string ExtractTag(string atomId)
    {
        var idx = atomId.LastIndexOf('/');
        return idx >= 0 && idx < atomId.Length - 1 ? atomId[(idx + 1)..] : string.Empty;
    }

    private static bool IsRateLimited(HttpResponseMessage r)
    {
        if (r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return true;
        return r.Headers.TryGetValues("X-RateLimit-Remaining", out var v) && v.FirstOrDefault() == "0";
    }

    private static bool VerifyBlobSha(byte[] content, string expectedSha)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var data = new byte[header.Length + content.Length];
        header.CopyTo(data, 0);
        content.CopyTo(data, header.Length);
        var actual = Convert.ToHexString(SHA1.HashData(data));
        return string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GhAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed class GhRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
    [JsonPropertyName("assets")] public List<GhAsset> Assets { get; set; } = [];
}

internal sealed class GhContentResponse
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = string.Empty;
}

[JsonSerializable(typeof(GhContentResponse))]
[JsonSerializable(typeof(GhRelease))]
[JsonSerializable(typeof(GhRelease[]))]
[JsonSerializable(typeof(GhAsset))]
[JsonSerializable(typeof(GhAsset[]))]
internal partial class GitHubJsonContext : JsonSerializerContext;