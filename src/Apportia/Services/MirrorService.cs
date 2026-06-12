namespace Apportia.Services;

internal static class MirrorService
{
    private static readonly string DataPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "mirrors.json");

    private static readonly IReadOnlyDictionary<string, string> SourceForgeMirrors =
        new Dictionary<string, string>
        {
            ["https://downloads.sourceforge.net"] = "United States, San Diego",
            ["https://kumisystems.dl.sourceforge.net"] = "Austria",
            ["https://netix.dl.sourceforge.net"] = "Bulgaria, Sofia",
            ["https://freefr.dl.sourceforge.net"] = "France, Paris",
            ["https://netcologne.dl.sourceforge.net"] = "Germany, Cologne",
            ["https://deac-fra.dl.sourceforge.net"] = "Germany, Frankfurt",
            ["https://deac-riga.dl.sourceforge.net"] = "Latvia, Riga",
            ["https://deac-ams.dl.sourceforge.net"] = "Netherlands, Amsterdam",
            ["https://unlimited.dl.sourceforge.net"] = "Serbia, Belgrade",
            ["https://altushost-swe.dl.sourceforge.net"] = "Sweden, Stockholm",
            ["https://ufpr.dl.sourceforge.net"] = "Brazil, Parana",
            ["https://razaoinfo.dl.sourceforge.net"] = "Brazil, Rio Grande do Sul",
            ["https://sinalbr.dl.sourceforge.net"] = "Brazil, Sao Paulo",
            ["https://gigenet.dl.sourceforge.net"] = "United States, Chicago",
            ["https://newcontinuum.dl.sourceforge.net"] = "United States, Chicago",
            ["https://cytranet.dl.sourceforge.net"] = "United States, Las Vegas",
            ["https://versaweb.dl.sourceforge.net"] = "United States, Las Vegas",
            ["https://cfhcable.dl.sourceforge.net"] = "United States, New York",
            ["https://phoenixnap.dl.sourceforge.net"] = "United States, Phoenix",
            ["https://ixpeering.dl.sourceforge.net"] = "Australia, Perth",
            ["https://sitsa.dl.sourceforge.net"] = "Argentina, Cordoba",
            ["https://yer.dl.sourceforge.net"] = "Azerbaijan, Baku",
            ["https://udomain.dl.sourceforge.net"] = "Hong Kong",
            ["https://zenlayer.dl.sourceforge.net"] = "Hong Kong",
            ["https://excellmedia.dl.sourceforge.net"] = "India, Kolkata",
            ["https://webwerks.dl.sourceforge.net"] = "India, Kolkata",
            ["https://jaist.dl.sourceforge.net"] = "Japan, Tokyo",
            ["https://onboardcloud.dl.sourceforge.net"] = "Singapore",
            ["https://nchc.dl.sourceforge.net"] = "Taiwan, Taipei",
            ["https://liquidtelecom.dl.sourceforge.net"] = "Kenya, Nairobi",
            ["https://tenet.dl.sourceforge.net"] = "South Africa, Johannesburg"
        };

    private static readonly IReadOnlyList<string> PortableAppsMirrors =
    [
        "http://downloads.portableapps.com",
        "http://downloads2.portableapps.com"
    ];

    private static bool IsSourceForgeUrl(string url)
    {
        return url.Contains("sourceforge.net", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortableAppsUrl(string url)
    {
        return url.Contains("portableapps.com/portableapps/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? GetCurrentMirrorBase(string url)
    {
        return !Uri.TryCreate(url, UriKind.Absolute, out var uri) ? null : $"{uri.Scheme}://{uri.Host}";
    }

    internal static string ReplaceMirror(string url, string newBase)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var currentBase = $"{uri.Scheme}://{uri.Host}";
        return newBase + url[currentBase.Length..];
    }

    internal static IReadOnlyList<(string Base, string Label)> GetAvailableMirrors(string url)
    {
        if (IsSourceForgeUrl(url))
            return SourceForgeMirrors
                   .Select(kv => (kv.Key, kv.Value))
                   .ToList();
        if (IsPortableAppsUrl(url))
            return PortableAppsMirrors
                   .Select(m => (m, m.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)))
                   .ToList();
        return [];
    }

    internal static string? LoadPreferredMirror()
    {
        try
        {
            if (File.Exists(DataPath))
            {
                var val = File.ReadAllText(DataPath).Trim();
                return val.Length > 0 ? val : null;
            }
        }
        catch
        {
            /* corrupt or missing – no preferred mirror */
        }

        return null;
    }

    internal static void SavePreferredMirror(string mirror)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
            File.WriteAllText(DataPath, mirror);
        }
        catch
        {
            /* non-critical – preference resets on next run */
        }
    }
}
