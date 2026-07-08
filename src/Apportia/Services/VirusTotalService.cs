using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apportia.Models;

namespace Apportia.Services;

public enum VtFileStatus
{
    Unknown,
    Fresh,
    Stale
}

public static class VirusTotalService
{
    public static readonly string IndexPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "virus_total.json");

    private static readonly string ResultsDir =
        Path.Combine(AppContext.BaseDirectory, "Data", "VirusTotal");

    private static readonly HttpClient Http = new();

    public static VtStore LoadStore()
    {
        if (!File.Exists(IndexPath)) return new VtStore();
        try
        {
            var text = File.ReadAllText(IndexPath);
            if (JsonSerializer.Deserialize(text, VirusTotalJsonContext.Default.VtStore) is { } store
                && (store.ApiKey != null || store.Files.Count > 0))
            {
                store.Files = new Dictionary<string, Dictionary<string, string>>(store.Files, StringComparer.OrdinalIgnoreCase);
                return store;
            }

            // Migrate: old format was a plain {app: {file: sha256}} dict without the VtStore wrapper
            if (JsonSerializer.Deserialize(
                    text, typeof(Dictionary<string, Dictionary<string, string>>), VirusTotalJsonContext.Default)
                is Dictionary<string, Dictionary<string, string>> { Count: > 0 } oldFiles)
                return new VtStore { Files = new Dictionary<string, Dictionary<string, string>>(oldFiles, StringComparer.OrdinalIgnoreCase) };
        }
        catch
        {
            /* corrupt */
        }

        return new VtStore();
    }

    public static void SaveStore(VtStore store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(store, VirusTotalJsonContext.Default.VtStore));
        }
        catch
        {
            /* non-fatal */
        }
    }

    public static VtResponse? LoadCachedResult(string sha256)
    {
        var path = CachePath(sha256);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(path),
                VirusTotalJsonContext.Default.VtResponse);
        }
        catch
        {
            return null;
        }
    }

    public static VtFileStatus GetCacheStatus(string sha256, string appUpdateDate)
    {
        var path = CachePath(sha256);
        if (!File.Exists(path)) return VtFileStatus.Unknown;
        if (!DateTime.TryParse(appUpdateDate, out var updateDt)) return VtFileStatus.Fresh;
        return File.GetLastWriteTime(path).Date < updateDt.Date ? VtFileStatus.Stale : VtFileStatus.Fresh;
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static IReadOnlyList<string> GetTopLevelBinaries(string appDir)
    {
        if (!Directory.Exists(appDir)) return [];
        return Directory.GetFiles(appDir, "*", SearchOption.TopDirectoryOnly)
                        .Where(IsPeFile)
                        .Select(Path.GetFileName)
                        .Where(f => !string.IsNullOrEmpty(f))
                        .OrderBy(f => !f!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList()!;
    }

    public static IReadOnlyList<string> GetSubdirBinaries(string appDir)
    {
        if (!Directory.Exists(appDir)) return [];
        return Directory.GetDirectories(appDir, "*", SearchOption.AllDirectories)
                        .SelectMany(dir => Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
                        .Where(IsPeFile)
                        .Select(f => Path.GetRelativePath(appDir, f))
                        .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
                        .ThenBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(f => !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                        .ToList();
    }

    private static bool IsPeFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 2);
            return fs.ReadByte() == 0x4D && fs.ReadByte() == 0x5A;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(VtResponse? Response, string? Error, bool NotFound)> QueryAsync(string sha256, string apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://www.virustotal.com/api/v3/files/{sha256}");
            request.Headers.Add("x-apikey", apiKey);
            var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, null, true);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}", false);
            var result = JsonSerializer.Deserialize(body, VirusTotalJsonContext.Default.VtResponse);
            SaveResult(sha256, body);
            return (result, null, false);
        }
        catch (Exception ex)
        {
            return (null, ex.Message, false);
        }
    }

    public static async Task<(string? AnalysisId, string? Error)> UploadAsync(string filePath, string apiKey)
    {
        const long maxUploadBytes = 32 * 1024 * 1024;
        try
        {
            if (new FileInfo(filePath).Length > maxUploadBytes)
                return (null, "File exceeds the 32 MB upload limit for VirusTotal.");

            using var content = new MultipartFormDataContent();
            await using var fs = File.OpenRead(filePath);
            content.Add(new StreamContent(fs), "file", Path.GetFileName(filePath));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.virustotal.com/api/v3/files");
            request.Headers.Add("x-apikey", apiKey);
            request.Content = content;
            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            var result = JsonSerializer.Deserialize(body, VirusTotalJsonContext.Default.VtUploadResponse);
            return (result?.Data?.Id, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static async Task<(string? Sha256, string? Error)> PollAnalysisAsync(
        string analysisId, string apiKey, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            progress?.Report(attempt);
            try
            {
                await Task.Delay(10_000, ct);
            }
            catch (OperationCanceledException)
            {
                return (null, "Cancelled.");
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"https://www.virustotal.com/api/v3/analyses/{analysisId}");
                request.Headers.Add("x-apikey", apiKey);
                var response = await Http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    return (null, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                var result = JsonSerializer.Deserialize(body, VirusTotalJsonContext.Default.VtAnalysisResponse);
                if (result?.Data?.Attributes?.Status == "completed")
                    return (result.Meta?.FileInfo?.Sha256, null);
            }
            catch (OperationCanceledException)
            {
                return (null, "Cancelled.");
            }
            catch
            {
                /* retry */
            }
        }

        return (null, "Analysis timed out after 5 minutes.");
    }

    private static void SaveResult(string sha256, string json)
    {
        try
        {
            Directory.CreateDirectory(ResultsDir);
            File.WriteAllText(CachePath(sha256), json);
        }
        catch
        {
            /* non-fatal */
        }
    }

    private static string CachePath(string sha256)
    {
        return Path.Combine(ResultsDir, sha256 + ".json");
    }
}

[JsonSerializable(typeof(VtResponse))]
[JsonSerializable(typeof(VtStore))]
[JsonSerializable(typeof(VtUploadResponse))]
[JsonSerializable(typeof(VtAnalysisResponse))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class VirusTotalJsonContext : JsonSerializerContext;
