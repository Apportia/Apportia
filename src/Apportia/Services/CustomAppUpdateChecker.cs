using Apportia.ViewModels;

namespace Apportia.Services;

public static class CustomAppUpdateChecker
{
    // Compares the atom feed's <updated> (moves on any release edit, including
    // binary re-uploads) against the locally stored date.
    public static async Task CheckAsync(IEnumerable<AppNode> customNodes, CancellationToken ct = default)
    {
        foreach (var node in customNodes)
        {
            if (ct.IsCancellationRequested)
                return;
            if (!node.IsCustom)
                continue;
            var info = CustomAppService.LoadInfo(node.SectionName);
            if (info == null || string.IsNullOrEmpty(info.DownloadPath) || !info.UpdateEnabled)
                continue;
            var repo = ExtractRepoPath(info.DownloadPath);
            if (repo == null)
                continue;

            var atom = await GitHubClient.FetchLatestReleaseFromAtomAsync(repo, ct);
            if (atom?.PublishedAt is not { } published)
                continue;

            var upstreamDate = published.LocalDateTime.Date;
            if (!DateTime.TryParse(node.UpdateDate, out var localDate) || upstreamDate > localDate.Date)
                node.SetUpstreamUpdateDate(upstreamDate.ToString("yyyy-MM-dd"));
        }
    }

    // Extracts "owner/repo" from a URL like https://github.com/owner/repo(/...).
    // Returns null when the URL isn't a github repo path.
    public static string? ExtractRepoPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length < 2 ? null : $"{segments[0]}/{segments[1]}";
    }
}
