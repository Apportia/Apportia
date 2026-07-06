using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace Apportia.Services;

public sealed class AppImageManager : IDisposable
{
    private const string RawBase = "https://raw.githubusercontent.com/Apportia/Apportia/main/data/AppImages/";
    private const int MaxConcurrentDownloads = 4;
    private static readonly Uri FallbackUri = new("avares://Apportia/Assets/Emoji/1f5bc.svg");

    private readonly ConcurrentDictionary<string, Bitmap> _cache = new();
    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private bool _disposed;

    public AppImageManager(string cacheDir)
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
        if (!File.Exists(localPath))
            return Placeholder(size);
        try
        {
            return _cache.GetOrAdd($"{size}:{sectionName}", _ => new Bitmap(localPath));
        }
        catch (Exception ex)
        {
            Log.Write($"Icon corrupt, discarding: {sectionName} @ {size} – {ex.Message}");
            try
            {
                File.Delete(localPath);
            }
            catch
            {
                /* file may be locked – will retry on next launch after Bitmap ref drops */
            }

            return Placeholder(size);
        }
    }

    public Bitmap GetCustomIcon(string folderName, int size = 24)
    {
        var iconPath = Path.Combine(CustomAppService.CustomAppImagesDir, folderName + ".png");
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(CustomAppService.CustomAppsDir, folderName, folderName + ".png");
        return File.Exists(iconPath) ? _cache.GetOrAdd("custom:" + folderName, _ => new Bitmap(iconPath)) : Placeholder(size);
    }

    public Bitmap ReloadCustomIcon(string folderName)
    {
        var key = "custom:" + folderName;
        if (_cache.TryRemove(key, out var old))
            old.Dispose();
        return GetCustomIcon(folderName);
    }

    public async Task<Bitmap?> GetPreviewAsync(string sectionName, CancellationToken ct = default)
    {
        var localPath = Path.Combine(_cacheDir, "Previews", $"{NormalizeSection(sectionName)}.png");
        if (File.Exists(localPath))
            return new Bitmap(localPath);

        var url = $"{RawBase}Previews/{NormalizeSection(sectionName)}.png";
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await AtomicFile.WriteAllBytesAsync(localPath, bytes, ct);
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            Log.Write($"Preview not found: {sectionName} ({url}) – {ex.Message}");
            return null;
        }
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
            await AtomicFile.WriteAllBytesAsync(localPath, bytes, ct);
            var bitmap = new Bitmap(new MemoryStream(bytes));
            _cache[$"{size}:{section}"] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Write($"Icon not found: {section} ({url}) – {ex.Message}");
            return null;
        }
    }

    private Bitmap Placeholder(int size)
    {
        return _cache.GetOrAdd($"placeholder:{size}", _ => RasterizeSvg(FallbackUri, size));
    }

    private static Bitmap RasterizeSvg(Uri uri, int size)
    {
        using var stream = AssetLoader.Open(uri);
        var svg = new SKSvg();
        var picture = svg.Load(stream) ?? throw new InvalidOperationException($"Failed to load SVG: {uri}");
        var bounds = picture.CullRect;
        var scale = size / Math.Max(bounds.Width, bounds.Height);
        var info = new SKImageInfo(size, size);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate((size - bounds.Width * scale) / 2f, (size - bounds.Height * scale) / 2f);
        canvas.Scale(scale);
        canvas.DrawPicture(picture);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new Bitmap(new MemoryStream(data.ToArray()));
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