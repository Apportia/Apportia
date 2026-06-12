using System.Net.Http.Headers;
using System.Text;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace Apportia.Services;

public sealed class AppDatabaseUpdater : IDisposable
{
    private const string PrimaryUrl = "https://raw.githubusercontent.com/Apportia/Apportia/main/data/app_database.ini";
    private const string FallbackUrl = "https://portableapps.com/updater/update.php";

    // Minimum ratio of new entries vs existing before replacing (avoids truncated downloads)
    private const double MinEntryRatio = 0.8;

    // Absolute minimum number of app sections in a valid file
    private const int MinEntryCount = 100;

    // Keys every valid app section must have at least one of
    private static readonly string[] RequiredKeys =
    [
        "Name",
        "Category",
        "SubCategory",
        "DisplayVersion",
        "PackageVersion",
        "DownloadSize",
        "InstallSize",
        "DownloadFile",
        "DownloadPath",
        "Hash",
        "ReleaseDate",
        "UpdateDate"
    ];

    public static readonly string CachePath = Path.Combine(ResolveDataDir(), "app_database.ini");

    private readonly HttpClient _http;

    public AppDatabaseUpdater()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Wget", "1.21.4"));
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async Task TryUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var iniText = await TryFetchPrimaryAsync(ct);

            if (iniText is null || !IsValid(iniText))
                iniText = await TryFetchFallbackAsync(ct);

            if (iniText is null || !IsValid(iniText))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await File.WriteAllTextAsync(CachePath, iniText, Encoding.UTF8, ct);
        }
        catch
        {
            /* keep existing cache intact on any failure */
        }
    }

    private async Task<string?> TryFetchPrimaryAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(PrimaryUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            /* network or parse failure – caller falls back to secondary source */
            return null;
        }
    }

    private async Task<string?> TryFetchFallbackAsync(CancellationToken ct)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(FallbackUrl, ct);

            using var stream = new MemoryStream(bytes);
            using var archive = SevenZipArchive.OpenArchive(stream, new ReaderOptions());

            var iniEntry = archive.Entries
                                  .FirstOrDefault(e =>
                                                      !e.IsDirectory &&
                                                      e.Key?.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) == true);

            if (iniEntry is null)
                return null;

            await using var entryStream = await iniEntry.OpenEntryStreamAsync(ct);
            using var buffer = new MemoryStream();
            await entryStream.CopyToAsync(buffer, ct);

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch
        {
            /* corrupt or unexpected archive format – caller falls back to cached data */
            return null;
        }
    }

    private static bool IsValid(string iniText)
    {
        var newCount = CountValidSections(iniText);

        if (newCount < MinEntryCount)
            return false;

        if (!File.Exists(CachePath))
            return true;
        var existingCount = CountValidSections(File.ReadAllText(CachePath));
        return existingCount <= 0 || !(newCount < existingCount * MinEntryRatio);
    }

    private static int CountValidSections(string iniText)
    {
        var count = 0;
        var inSection = false;
        var foundKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in iniText.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';')
                continue;

            if (trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                if (inSection && RequiredKeys.All(foundKeys.Contains))
                    count++;
                inSection = true;
                foundKeys.Clear();
                continue;
            }

            if (!inSection)
                continue;

            var sep = trimmed.IndexOf('=');
            if (sep <= 0)
                continue;

            var key = trimmed[..sep].Trim();
            var value = trimmed[(sep + 1)..].Trim();

            if (value.Length <= 0)
                continue;
            foreach (var required in RequiredKeys)
            {
                if (!key.Equals(required, StringComparison.OrdinalIgnoreCase))
                    continue;
                foundKeys.Add(required);
                break;
            }
        }

        if (inSection && RequiredKeys.All(foundKeys.Contains))
            count++;
        return count;
    }

    private static string ResolveDataDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "Data");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "Data");
    }
}