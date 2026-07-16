using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Apportia.Services;

/// REST API with atom-feed / raw.githubusercontent.com fallback. GITHUB_TOKEN env var
/// lifts the anonymous rate limit (60/h) to 5000/h.
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

    /// API path verifies the blob SHA; raw.githubusercontent.com fallback does not.
    public static async Task<string?> FetchFileContentAsync(
        string repo,
        string path,
        string branch = "main",
        CancellationToken ct = default)
    {
        var apiUrl = $"https://api.github.com/repos/{repo}/contents/{path}?ref={branch}";
        try
        {
            using var response = await Http.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
        }
        catch
        {
            // fall through to raw
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

    /// Atom fallback never carries assets. Pass allowAtomFallback: false when the caller
    /// needs assets, otherwise a transient API failure gets masked with an assetless release.
    public static async Task<GhRelease?> FetchLatestReleaseAsync(
        string repo,
        CancellationToken ct = default,
        bool allowAtomFallback = true)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://api.github.com/repos/{repo}/releases/latest", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize(json, GitHubJsonContext.Default.GhRelease);
            }
        }
        catch
        {
            // fall through to atom (or return null if the caller opted out)
        }

        if (!allowAtomFallback)
            return null;

        var atom = await FetchAtomReleasesAsync(repo, ct);
        return atom.Count > 0 ? atom[0] : null;
    }

    /// Newest first. Atom-fallback entries have no assets.
    public static async Task<IReadOnlyList<GhRelease>> FetchReleasesAsync(
        string repo,
        int perPage = 30,
        CancellationToken ct = default)
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

    /// Atom-only path — no API quota consumed, but the release has no assets.
    public static async Task<GhRelease?> FetchLatestReleaseFromAtomAsync(string repo, CancellationToken ct = default)
    {
        var atom = await FetchAtomReleasesAsync(repo, ct);
        return atom.Count > 0 ? atom[0] : null;
    }

    /// Streams to disk with progress. Returns false on any error.
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

    /// Returns null on any error.
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
                          Name = entry.Element(ns + "title")?.Value ?? string.Empty,
                          PublishedAt = DateTimeOffset.TryParse(entry.Element(ns + "updated")?.Value, out var dt) ? dt : null,
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

    [JsonPropertyName("digest")] public string Digest { get; set; } = string.Empty;

    public string Sha256Hex =>
        Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? Digest["sha256:".Length..]
            : string.Empty;
}

public sealed class GhRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
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
