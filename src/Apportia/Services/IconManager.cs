using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Apportia.Services;

public sealed class IconManager : IDisposable
{
    private const string RawBase = "https://raw.githubusercontent.com/Apportia/Apportia/main/data/AppImages/";
    private const int MaxConcurrentDownloads = 4;
    private static readonly Uri FallbackUri = new("avares://Apportia/Assets/AppImage.png");

    private readonly ConcurrentDictionary<string, Bitmap> _cache = new();
    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private bool _disposed;

    public IconManager(string cacheDir)
    {
        _cacheDir = cacheDir;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Wget", "1.25"));
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
        foreach (var bitmap in _cache.Values)
            bitmap.Dispose();
        _cache.Clear();
    }

    /// Returns the cached or placeholder icon synchronously – used during initial load.
    public Bitmap GetIcon(string sectionName, int size)
    {
        var localPath = LocalPath(sectionName, size);
        return File.Exists(localPath) ? _cache.GetOrAdd($"{size}:{sectionName}", _ => new Bitmap(localPath)) : Placeholder();
    }

    public Bitmap GetCustomIcon(string folderName)
    {
        var iconPath = Path.Combine(CustomAppService.CustomAppImagesDir, folderName + ".png");
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(CustomAppService.CustomAppsDir, folderName, folderName + ".png");
        return File.Exists(iconPath) ? _cache.GetOrAdd("custom:" + folderName, _ => new Bitmap(iconPath)) : Placeholder();
    }

    public Bitmap ReloadCustomIcon(string folderName)
    {
        var key = "custom:" + folderName;
        if (_cache.TryRemove(key, out var old))
            old.Dispose();
        return GetCustomIcon(folderName);
    }

    public async Task EnsureIconAsync(string sectionName, int size, CancellationToken ct = default)
    {
        if (!File.Exists(LocalPath(sectionName, size)))
            await DownloadAsync(sectionName, size, ct);
    }

    /// Downloads missing icons in the background, calling onUpdated for each new icon.
    public async Task DownloadAllAsync(
        IEnumerable<string> sectionNames,
        int size,
        Action<string, Bitmap> onUpdated,
        CancellationToken ct = default)
    {
        var missing = sectionNames
                      .Where(s => !File.Exists(LocalPath(s, size)))
                      .ToList();

        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDownloads, CancellationToken = ct },
            async (section, token) =>
            {
                var bitmap = await DownloadAsync(section, size, token);
                if (bitmap is not null)
                    onUpdated(section, bitmap);
            });
    }

    private async Task<Bitmap?> DownloadAsync(string section, int size, CancellationToken ct)
    {
        var url = $"{RawBase}{size}/{NormalizeSection(section)}.png";
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            var localPath = LocalPath(section, size);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, bytes, ct);
            var bitmap = new Bitmap(new MemoryStream(bytes));
            _cache[$"{size}:{section}"] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            Log($"Icon not found: {section} ({url}) – {ex.Message}");
            return null;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Apportia");
            var logPath = Path.Combine("/tmp", exeName + ".log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            /* logging must never crash the app */
        }
    }

    private Bitmap Placeholder()
    {
        return _cache.GetOrAdd(string.Empty, _ =>
        {
            using var stream = AssetLoader.Open(FallbackUri);
            return new Bitmap(stream);
        });
    }

    public string LocalPath(string section, int size)
    {
        return Path.Combine(_cacheDir, ResolveSize(size).ToString(), $"{NormalizeSection(section)}.png");
    }

    private static int ResolveSize(int size)
    {
        return size < 16 ? 16 : size;
    }

    private static string NormalizeSection(string section)
    {
        return section.Replace("+", "Plus");
    }
}