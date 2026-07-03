using Apportia.ViewModels;

namespace Apportia.Services;

public sealed class InstallQueue : IDisposable
{
    private readonly Queue<(AppNode Node, bool Launch)> _queue = new();

    public bool IsRunning { get; set; }
    public bool InSetupPhase { get; set; }
    public AppNode? ActiveNode { get; set; }
    public string? ActiveDownloadFile { get; set; }
    public CancellationTokenSource? Cts { get; set; }

    public int Count => _queue.Count;

    public IEnumerable<(AppNode Node, bool Launch)> Items => _queue;

    public void Dispose()
    {
        Cts?.Dispose();
        Cts = null;
    }

    public void Enqueue(AppNode node, bool launch)
    {
        node.IsQueued = true;
        _queue.Enqueue((node, launch));
    }

    public bool TryDequeue(out AppNode node, out bool launch)
    {
        if (_queue.TryDequeue(out var item))
        {
            item.Node.IsQueued = false;
            node = item.Node;
            launch = item.Launch;
            return true;
        }

        node = null!;
        launch = false;
        return false;
    }

    public void Remove(AppNode node)
    {
        node.IsQueued = false;
        var remaining = _queue.Where(i => i.Node != node).ToArray();
        _queue.Clear();
        foreach (var item in remaining)
            _queue.Enqueue(item);
    }

    public void ClearQueue()
    {
        foreach (var item in _queue)
            item.Node.IsQueued = false;
        _queue.Clear();
    }
}