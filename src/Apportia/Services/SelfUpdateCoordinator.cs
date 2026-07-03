namespace Apportia.Services;

public sealed class SelfUpdateCoordinator(CancellationToken shutdownToken)
{
    public SelfUpdateInfo? Pending { get; private set; }

    public async Task<SelfUpdateInfo?> CheckAsync()
    {
        Pending = await SelfUpdater.CheckAsync(shutdownToken);
        return Pending;
    }

    public Task ApplyAsync(IProgress<int>? progress)
    {
        return Pending == null ? Task.CompletedTask : SelfUpdater.ApplyAsync(Pending, progress, shutdownToken);
    }
}
