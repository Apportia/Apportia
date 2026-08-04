using System.IO.Compression;
using Apportia.Text;
using Apportia.ViewModels;

namespace Apportia.Services;

public static class CustomAppUpdater
{
    /// Downloads and applies the latest github release for a custom app.
    /// The hash-mismatch callback matches the signature used elsewhere:
    /// (sectionName, displayName, assetName, expectedHash, downloadedFile) -> proceed?
    public static async Task<bool> UpdateAsync(
        AppNode node,
        Func<string, string, string, string, string, Task<bool>>? onHashMismatch,
        IInstallUi? ui = null,
        CancellationToken ct = default)
    {
        var info = CustomAppService.LoadInfo(node.SectionName);
        if (info == null || string.IsNullOrEmpty(info.DownloadPath))
            return false;
        var repo = CustomAppUpdateChecker.ExtractRepoPath(info.DownloadPath);
        if (repo == null)
            return false;

        var release = await GitHubClient.FetchLatestReleaseAsync(repo, ct, false);
        if (release?.PublishedAt is not { } published)
            return false;

        var sevenZipPath = AppDeployService.FindSevenZip(AppDeployService.AppsDir);
        var asset = PickBestAsset(release.Assets, info.DownloadFile, sevenZipPath != null);
        if (asset == null)
            return false;

        var isPaf = asset.Name.EndsWith(".paf.exe", StringComparison.OrdinalIgnoreCase);
        var isSevenZip = !isPaf && asset.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
        var extension = isPaf ? ".paf.exe" : isSevenZip ? ".7z" : ".zip";
        var tempArchive = Path.Combine(Path.GetTempPath(), $"apportia_ghup_{Guid.NewGuid():N}{extension}");

        node.IsBeingInstalled = true;
        ui?.SetInstalling(true);
        ui?.SetBusyCursor(true);
        ui?.ShowDownloadBar(true);
        ui?.SetDownloadStatus(string.Format(UiText.Dialog.InstallPreparingFormat, node.Name), UiText.Dialog.InstallPleaseWait);
        ui?.SetDownloadProgress(0, true);

        try
        {
            var progress = new Progress<double>(p => ui?.SetDownloadProgress(p * 100, false));
            var ok = await GitHubClient.DownloadAssetAsync(asset.DownloadUrl, tempArchive, progress, ct);
            if (!ok)
                return false;

            var sha256 = asset.Sha256Hex;
            if (sha256.Length > 0 && AppDeployService.VerifyHash(tempArchive, sha256) == HashResult.Invalid)
            {
                var proceed = onHashMismatch != null &&
                              await onHashMismatch(node.SectionName, node.Name, asset.Name, sha256, tempArchive);
                if (!proceed)
                    return false;
            }

            ui?.SetDownloadProgress(0, true);
            ui?.SetDownloadStatus(string.Format(UiText.Dialog.InstallExtractingFormat, node.Name), string.Empty);

            if (isPaf)
            {
                CustomAppService.EnsurePlatformStub();
                await AppDeployService.ExecuteAsync(tempArchive, node.SectionName, CustomAppService.CustomAppsDir, false, ct);
            }
            else
            {
                var stagingDir = Path.Combine(Path.GetTempPath(), $"apportia_ghup_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
                try
                {
                    if (isSevenZip)
                        await AppDeployService.ExtractAsync(sevenZipPath!, tempArchive, stagingDir, ct);
                    else
                        await Task.Run(() => ZipFile.ExtractToDirectory(tempArchive, stagingDir), ct);

                    var appDir = Path.Combine(CustomAppService.CustomAppsDir, node.SectionName);
                    var leftovers = await Task.Run(() => OverlayDirectory(appDir, stagingDir), ct);

                    if (leftovers.Count > 0)
                    {
                        var cmp = StringComparer.OrdinalIgnoreCase;
                        var hasMemory = info.LeftoverKnown.Length > 0;
                        var knownSet = hasMemory ? info.LeftoverKnown.ToHashSet(cmp) : new HashSet<string>(cmp);
                        var newFiles = hasMemory && leftovers.Any(f => !knownSet.Contains(f));
                        var skipDialog = hasMemory && !newFiles;

                        IReadOnlyList<string>? toDelete = null;
                        if (skipDialog)
                        {
                            toDelete = info.LeftoverDelete.Intersect(leftovers, cmp).ToArray();
                        }
                        else if (ui != null)
                        {
                            var preChecked = hasMemory
                                ? info.LeftoverDelete.Intersect(leftovers, cmp).ToArray()
                                : null;
                            var result = await ui.ShowUpdateLeftoverDialogAsync(node, leftovers, preChecked, hasMemory);
                            if (result != null)
                            {
                                toDelete = result.SelectedForDeletion;
                                var known = result.Remember ? leftovers.ToArray() : [];
                                var del = result.Remember ? result.SelectedForDeletion.ToArray() : [];
                                CustomAppService.SaveLeftoverMemory(node.SectionName, known, del);
                            }
                        }

                        if (toDelete is { Count: > 0 })
                        {
                            var touchedDirs = new HashSet<string>(cmp);
                            foreach (var rel in toDelete)
                            {
                                var full = Path.Combine(appDir, rel);
                                try
                                {
                                    if (File.Exists(full))
                                    {
                                        File.Delete(full);
                                        var parent = Path.GetDirectoryName(full);
                                        if (parent != null)
                                            touchedDirs.Add(parent);
                                    }
                                }
                                catch
                                {
                                    // best-effort — the user asked to delete, but a locked file
                                    // shouldn't fail the whole update
                                }
                            }

                            foreach (var dir in touchedDirs)
                                PruneEmptyDirsUpward(dir, appDir);
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(stagingDir))
                            Directory.Delete(stagingDir, true);
                    }
                    catch
                    {
                        // temp cleanup best-effort
                    }
                }
            }

            // Anchor the stored date to atom's <updated> so subsequent CustomAppUpdateChecker
            // runs compare like-for-like; the JSON published_at wouldn't move with edits.
            var atom = await GitHubClient.FetchLatestReleaseFromAtomAsync(repo, ct);
            var storedLocal = atom?.PublishedAt?.LocalDateTime ?? published.LocalDateTime;
            var newVersion = GitHubVersion.Derive(release.TagName, release.PublishedAt);
            CustomAppService.ApplyGithubUpdate(node.SectionName, asset.Name, storedLocal, newVersion, release.TagName);
            node.SetVersion(release.TagName, newVersion);
            node.LocalDisplayVersion = release.TagName;
            node.LocalPackageVersion = newVersion;
            node.SetUpstreamUpdateDate(storedLocal.ToString("yyyy-MM-dd"));
            node.CurrentDate = storedLocal.ToString("yyyy-MM-dd");
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(tempArchive))
                    File.Delete(tempArchive);
            }
            catch
            {
                // temp cleanup best-effort
            }

            node.IsBeingInstalled = false;
            ui?.SetDownloadProgress(0, false);
            ui?.ShowDownloadBar(false);
            ui?.SetInstalling(false);
            ui?.SetBusyCursor(false);
        }
    }

    /// Picks the release asset that best matches the previously downloaded file.
    /// Prefers an exact name match, then same extension with the highest name similarity,
    /// finally falls back to the first supported archive.
    public static GhAsset? PickBestAsset(IReadOnlyList<GhAsset> assets, string previous, bool sevenZipAvailable)
    {
        var supported = assets.Where(a => IsSupportedExt(a.Name, sevenZipAvailable)).ToList();
        if (supported.Count == 0)
            return null;
        if (string.IsNullOrEmpty(previous))
            return supported[0];

        var exact = supported.FirstOrDefault(a => string.Equals(a.Name, previous, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        var previousExt = Path.GetExtension(previous);
        var previousStem = StripDigits(Path.GetFileNameWithoutExtension(previous));

        return supported
               .Select(a => new
               {
                   Asset = a,
                   Score = SimilarityScore(StripDigits(Path.GetFileNameWithoutExtension(a.Name)), previousStem),
                   SameExt = Path.GetExtension(a.Name).Equals(previousExt, StringComparison.OrdinalIgnoreCase)
               })
               .OrderByDescending(x => x.SameExt)
               .ThenByDescending(x => x.Score)
               .First()
               .Asset;
    }

    private static bool IsSupportedExt(string name, bool sevenZipAvailable)
    {
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.EndsWith(".paf.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        return sevenZipAvailable && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDigits(string s)
    {
        return new string(s.Where(c => !char.IsDigit(c)).ToArray()).ToLowerInvariant();
    }

    // Longest-common-substring length, capped by shorter input; higher = more similar.
    private static int SimilarityScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var dp = new int[a.Length + 1, b.Length + 1];
        var best = 0;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                if (a[i - 1] == b[j - 1])
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                    if (dp[i, j] > best) best = dp[i, j];
                }

        return best;
    }

    // Walks up from dir toward stopDir (exclusive), removing directories that are empty.
    // Stops as soon as it hits a non-empty directory or reaches stopDir.
    private static void PruneEmptyDirsUpward(string dir, string stopDir)
    {
        var current = Path.TrimEndingDirectorySeparator(dir);
        var stop = Path.TrimEndingDirectorySeparator(stopDir);
        while (!string.IsNullOrEmpty(current) &&
               !string.Equals(current, stop, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(current))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                    return;
                Directory.Delete(current);
            }
            catch
            {
                return;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }

    // Overlay copy: files from the new archive overwrite existing ones, but pre-existing
    // files that aren't in the archive (user configs, saves, portable data) are kept.
    // Returns the relative paths of files that existed before and were NOT part of the
    // new archive, so the caller can offer the user selective cleanup.
    private static List<string> OverlayDirectory(string destDir, string newSourceDir)
    {
        var preExisting = Directory.Exists(destDir)
            ? Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories)
                       .Select(f => Path.GetRelativePath(destDir, f))
                       .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(destDir);

        foreach (var dir in Directory.EnumerateDirectories(newSourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(newSourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(newSourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(newSourceDir, file);
            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            preExisting.Remove(rel);
        }

        return preExisting.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }
}