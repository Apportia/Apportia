using Apportia.ViewModels;
using Avalonia.Threading;

namespace Apportia.Services;

public sealed class InstallOrchestrator(
    InstallQueue queue,
    AppDeployService deployService,
    IInstallUi ui)
{
    public async Task InstallAsync(AppNode node, string appsBaseDir, bool launch, bool fromQueue = false)
    {
        if (queue.IsRunning && !fromQueue)
            return;
        if (string.IsNullOrEmpty(node.DownloadFile) || string.IsNullOrEmpty(node.DownloadPath))
            return;

        if (OperatingSystem.IsLinux() && !AppDeployService.IsWineAvailable())
        {
            await ui.ShowDialogAsync(
                node, "Wine Not Found",
                "Running Windows applications requires Wine.\n\n" +
                "Please install Wine using your package manager.",
                "OK");
            return;
        }

        var downloadFile = node.DownloadFile;
        var downloadHash = node.Hash;
        string? chosenLanguage = null;

        if (node.HasLanguageVariants)
        {
            var savedLang = AppLanguageService.Load(node.SectionName);

            var autoSelected = savedLang == "English" ||
                               savedLang != null && node.HasLanguageVariantKey(savedLang);

            if (!autoSelected)
            {
                var selected = await ui.ShowLanguageDialogAsync(node, node.GetLanguageKeys()!, savedLang);
                if (selected is null)
                    return;
                chosenLanguage = selected;
            }
            else
            {
                chosenLanguage = savedLang;
            }

            if (chosenLanguage != "English" && node.TryGetLanguageVariant(chosenLanguage!, out var variantFile, out var variantHash))
            {
                downloadFile = variantFile;
                downloadHash = variantHash;
            }
        }

        if (node.RequiresJava)
        {
            var allNodes = ui.GetAllNodes().ToList();
            var javaInstalled = allNodes
                .Any(n => PluginService.IsJavaPlugin(n.SectionName) && n.IsInstalled);

            if (!javaInstalled)
            {
                var available = allNodes
                                .Where(n => PluginService.IsJavaPlugin(n.SectionName) && n is { IsInstalled: false, IsLegacy: false })
                                .ToList();

                if (available.Count == 0)
                {
                    await ui.ShowDialogAsync(
                        node, "Java Required",
                        $"{node.Name} requires a Java runtime, but no Java plugins are available to install.",
                        "OK");
                    return;
                }

                var indices = await ui.ShowJavaRequiredDialogAsync(node, available.Select(n => n.Name).ToArray());
                if (indices == null)
                    return;

                foreach (var idx in indices)
                    queue.Enqueue(available[idx], false);
            }
        }

        var requiredBytes = (node.DownloadSizeMb + node.InstallSizeMb) * 1_048_576;
        if (requiredBytes > 0)
        {
            while (true)
            {
                var needed = (long)(requiredBytes * 1.1);
                var free = AppDiskUsageService.GetAvailableFreeSpace(appsBaseDir);
                if (free < needed)
                {
                    var choice = await ui.ShowDiskSpaceDialogAsync(node, node.Name, needed, free);
                    if (choice == "Retry")
                        continue;
                    queue.ClearQueue();
                    return;
                }

                break;
            }
        }

        var wasInstalled = node.IsInstalled;
        queue.IsRunning = true;
        queue.InSetupPhase = false;
        queue.Cts = new CancellationTokenSource();
        queue.ActiveNode = node;
        queue.ActiveDownloadFile = downloadFile;
        node.IsBeingInstalled = true;
        ui.SetInstalling(true);
        ui.SetBusyCursor(true);
        ui.ShowDownloadBar(true);
        ui.SetDownloadStatus($"Preparing {node.Name}...", "Please wait");

        try
        {
            var url = node.DownloadPath.TrimEnd('/') + "/" + downloadFile;
            var progressCts = new CancellationTokenSource();
            var progressToken = progressCts.Token;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (progressToken.IsCancellationRequested)
                    return;
                if (p.Percent > 0)
                {
                    ui.SetDownloadProgress(p.Percent, false);
                    ui.SetDownloadStatus(p.FormatReceived(), p.BytesPerSecond > 0 ? p.FormatSpeed() : string.Empty);
                }
                else
                {
                    ui.SetDownloadProgress(0, true);
                }
            });

            string localPath;
            var preferred = MirrorService.LoadPreferredMirror(url);
            var downloadUrl = preferred != null
                ? MirrorService.ApplyMirror(url, preferred)
                : url;
            while (true)
            {
                try
                {
                    localPath = await deployService.DownloadAsync(downloadUrl, downloadFile, progress, node.UserAgent, queue.Cts.Token);
                    await progressCts.CancelAsync();
                    progressCts.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    if (queue.Cts.IsCancellationRequested)
                        return;
                    try
                    {
                        File.Delete(Path.Combine(appsBaseDir, downloadFile));
                    }
                    catch
                    {
                        /* partial download file may not exist yet if the connection failed early */
                    }

                    var failed = MirrorService.GetCurrentMirrorSlug(downloadUrl);
                    var available = MirrorService.GetAvailableMirrors(downloadUrl);
                    if (available.Count > 0)
                    {
                        ui.ShowDownloadBar(false);
                        var selected = await ui.ShowMirrorDialogAsync(node, failed, available);
                        if (selected != null)
                        {
                            MirrorService.SavePreferredMirror(downloadUrl, selected);
                            downloadUrl = MirrorService.ApplyMirror(downloadUrl, selected);
                            ui.ShowDownloadBar(true);
                            ui.SetDownloadStatus($"Preparing {node.Name}...", string.Empty);
                            continue;
                        }
                    }

                    await ui.ShowDialogAsync(node, "Download Failed", ex.Message, "OK");
                    return;
                }
            }

            var hash = AppDeployService.VerifyHash(localPath, downloadHash);
            if (hash == HashResult.Invalid)
            {
                ui.ShowDownloadBar(false);
                var choice = await ui.ShowDialogAsync(
                    node, "Hash Mismatch",
                    "The downloaded file's hash does not match the expected value.\n\n" +
                    "The file may be corrupted or tampered with.",
                    "Scan with VirusTotal", "Proceed Anyway", "Cancel");
                if (choice == "Scan with VirusTotal")
                {
                    await ui.ShowVirusTotalDialogAsync(node);
                    choice = await ui.ShowDialogAsync(
                        node, "Hash Mismatch",
                        "Do you want to proceed with the installation?",
                        "Proceed", "Cancel");
                    if (choice != "Proceed")
                    {
                        File.Delete(localPath);
                        return;
                    }
                }
                else if (choice != "Proceed Anyway")
                {
                    File.Delete(localPath);
                    return;
                }

                ui.ShowDownloadBar(true);
            }

            var sevenZipPath = AppDeployService.FindSevenZip(appsBaseDir);
            var isLegacyArchive =
                sevenZipPath != null &&
                DateTime.TryParse(node.UpdateDate, out var updateDtCheck) &&
                updateDtCheck.Date < new DateTime(2016, 1, 1);

            ui.SetDownloadProgress(0, true);
            ui.SetDownloadStatus(
                isLegacyArchive ? $"Extracting {node.Name}..." : $"Installing {node.Name}...",
                "Please wait");
            queue.InSetupPhase = true;

            try
            {
                if (isLegacyArchive)
                {
                    var extractDest = Path.Combine(appsBaseDir, node.SectionName);
                    await AppDeployService.ExtractAsync(sevenZipPath!, localPath, extractDest, queue.Cts.Token);
                    try
                    {
                        File.Delete(localPath);
                    }
                    catch
                    {
                        /* file may be locked briefly after extraction completes */
                    }

                    try
                    {
                        var pluginsDir = Path.Combine(extractDest, "$PLUGINSDIR");
                        if (Directory.Exists(pluginsDir))
                            Directory.Delete(pluginsDir, true);
                    }
                    catch
                    {
                        /* $PLUGINSDIR cleanup is best-effort; leftover files are harmless */
                    }

                    try
                    {
                        AppDeployService.SetIniSectionValue(
                            Path.Combine(extractDest, "App", "AppInfo", "appinfo.ini"),
                            "PortableApps.comInstaller",
                            "InstallIntegrityCheck",
                            "true");
                    }
                    catch
                    {
                        /* appinfo.ini write is best-effort; platform will re-create it on next run */
                    }
                }
                else
                {
                    try
                    {
                        var installDir = node.IsPlugin
                            ? PluginService.GetInstallDir(node.SectionName)
                            : Path.Combine(appsBaseDir, node.SectionName);
                        var licenseDir = Path.Combine(installDir, "Data", "PortableApps.comInstaller");
                        Directory.CreateDirectory(licenseDir);
                        await File.WriteAllTextAsync(
                            Path.Combine(licenseDir, "license.ini"),
                            "[PortableApps.comInstaller]\nEULAVersion=1\n",
                            queue.Cts.Token);
                    }
                    catch
                    {
                        /* non-critical – installer may prompt for EULA if this fails */
                    }

                    await AppDeployService.ExecuteAsync(localPath, node.SectionName, appsBaseDir, false, queue.Cts.Token);

                    try
                    {
                        var baseDir = node.IsPlugin
                            ? PluginService.GetInstallDir(node.SectionName)
                            : Path.Combine(appsBaseDir, node.SectionName);
                        var appInfoPath = Path.Combine(baseDir, "App", "AppInfo", "appinfo.ini");
                        var eulaInstallerDir = Path.Combine(baseDir, "Data", "PortableApps.comInstaller");
                        if (!AppDeployService.ReadEulaVersion(appInfoPath) && Directory.Exists(eulaInstallerDir))
                            Directory.Delete(eulaInstallerDir, true);
                    }
                    catch
                    {
                        /* eulaInstallerDir removal is best-effort; stale folder is harmless */
                    }
                }

                string? appExeAfter;
                if (node.IsPlugin)
                {
                    var m = PluginService.GetMarkerFile(node.SectionName);
                    appExeAfter = File.Exists(m) ? m : null;
                }
                else
                {
                    appExeAfter = await ui.ResolveAppExeAsync(node, appsBaseDir);
                }

                if (appExeAfter != null)
                {
                    if (chosenLanguage != null)
                        AppLanguageService.Save(node.SectionName, chosenLanguage);
                    node.IsInstalled = true;
                    if (!node.IsPlugin)
                    {
                        LocalVersionService.Save(node.SectionName, node.DisplayVersion, node.PackageVersion);
                        node.LocalDisplayVersion = node.DisplayVersion;
                        node.LocalPackageVersion = node.PackageVersion;
                    }

                    var installDir = node.IsPlugin
                        ? PluginService.GetInstallDir(node.SectionName)
                        : Path.Combine(appsBaseDir, node.SectionName);
                    _ = ScanAndCacheNodeSizeAsync(node, installDir);
                    if (!node.IsPlugin && AppBackupService.HasBackup(node.SectionName))
                        try
                        {
                            AppBackupService.RestoreBackup(node.SectionName, installDir);
                        }
                        catch (Exception ex)
                        {
                            Log.Write($"Failed to restore backup for '{node.SectionName}': {ex.Message}");
                        }

                    if (DateTime.TryParse(node.UpdateDate, out var updateDate))
                    {
                        File.SetLastWriteTime(appExeAfter, updateDate);
                        node.CurrentDate = updateDate.ToString("yyyy-MM-dd");
                    }
                    else
                    {
                        node.CurrentDate = File.GetLastWriteTime(appExeAfter).ToString("yyyy-MM-dd");
                    }

                    if (launch && !node.IsPlugin)
                        await ui.LaunchAsync(node);
                }
            }
            catch (Exception ex)
            {
                if (!queue.Cts.IsCancellationRequested)
                    await ui.ShowDialogAsync(node, "Launch Failed", ex.Message, "OK");
            }
        }
        finally
        {
            queue.InSetupPhase = false;
            node.IsBeingInstalled = false;

            if (queue.Cts?.IsCancellationRequested == true)
            {
                var downloadedFile = Path.Combine(appsBaseDir, queue.ActiveDownloadFile ?? string.Empty);
                try
                {
                    File.Delete(downloadedFile);
                }
                catch
                {
                    /* already gone or never created */
                }

                if (!wasInstalled)
                {
                    var appDir = node.IsPlugin
                        ? PluginService.GetInstallDir(node.SectionName)
                        : Path.Combine(appsBaseDir, node.SectionName);
                    try
                    {
                        if (Directory.Exists(appDir))
                            Directory.Delete(appDir, true);
                    }
                    catch
                    {
                        /* partial dir may be locked */
                    }
                }
            }

            queue.ActiveNode = null;
            queue.ActiveDownloadFile = null;
            queue.Cts?.Dispose();
            queue.Cts = null;

            ui.ShowDownloadBar(false);

            if (queue.TryDequeue(out var nextNode, out var nextLaunch))
            {
                _ = InstallAsync(nextNode, appsBaseDir, nextLaunch, true);
            }
            else
            {
                queue.IsRunning = false;
                ui.SetBusyCursor(false);
                ui.SetInstalling(false);
            }
        }
    }

    private static async Task ScanAndCacheNodeSizeAsync(AppNode node, string appDir)
    {
        var bytes = await Task.Run(() => AppDiskUsageService.GetDirectorySize(appDir));
        var cache = AppDiskUsageService.LoadCache();
        cache.Sizes[node.SectionName] = bytes;
        AppDiskUsageService.SaveCache(cache);
        await Dispatcher.UIThread.InvokeAsync(() => node.SetUsedBytes(bytes));
    }
}