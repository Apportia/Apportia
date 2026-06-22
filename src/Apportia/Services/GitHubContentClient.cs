using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apportia.Services;

public static class GitHubContentClient
{
    private const string ApiBase = "https://api.github.com/repos/Apportia/Apportia/contents/";

    private static readonly HttpClient Http;

    static GitHubContentClient()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Apportia", "1.0"));
        Http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static async Task<string?> FetchTextAsync(string repoPath, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(ApiBase + repoPath, ct);
            if (JsonSerializer.Deserialize(json, GitHubApiJsonContext.Default.GitHubContentResponse)
                is not { Encoding: "base64" } response)
                return null;

            var bytes = Convert.FromBase64String(response.Content.Replace("\n", "").Replace("\r", ""));

            return VerifyBlobSha(bytes, response.Sha) ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch
        {
            return null;
        }
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

internal sealed class GitHubContentResponse
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = string.Empty;
}

[JsonSerializable(typeof(GitHubContentResponse))]
internal partial class GitHubApiJsonContext : JsonSerializerContext;