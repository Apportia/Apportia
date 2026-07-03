using Apportia.ViewModels;

namespace Apportia.Services;

public interface IInstallUi
{
    Task<string?> ShowDialogAsync(AppNode? node, string title, string message, params string[] buttons);
    Task<string?> ShowDiskSpaceDialogAsync(AppNode node, string appName, long required, long available);
    Task<string?> ShowLanguageDialogAsync(AppNode node, IReadOnlyList<string> keys, string? savedLang);
    Task<int[]?> ShowJavaRequiredDialogAsync(AppNode node, string[] pluginNames);
    Task<string?> ShowMirrorDialogAsync(AppNode node, string? failedSlug, IReadOnlyList<(string Slug, string Label)> available);
    Task ShowVirusTotalDialogAsync(AppNode node);

    void ShowDownloadBar(bool visible);
    void SetDownloadStatus(string sizeText, string speedText);
    void SetDownloadProgress(double percent, bool indeterminate);
    void SetInstalling(bool value);
    void SetBusyCursor(bool busy);

    Task<string?> ResolveAppExeAsync(AppNode node, string appsBaseDir);
    Task LaunchAsync(AppNode node);
    IEnumerable<AppNode> GetAllNodes();
}
