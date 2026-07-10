using System.IO.Pipes;

namespace Apportia.Services;

public sealed class IpcServer(string pipeName, Action<string[]> onArgs) : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(token), token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(pipeName,
                                                     PipeDirection.In,
                                                     NamedPipeServerStream.MaxAllowedServerInstances,
                                                     PipeTransmissionMode.Byte,
                                                     PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(token);
                _ = Task.Run(() => HandleConnectionAsync(pipe, token), token);
            }
            catch (OperationCanceledException)
            {
                /* shutdown requested */
                break;
            }
            catch (Exception)
            {
                /* pipe error — restart server on next iteration */
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            await using (pipe)
            {
                using var reader = new StreamReader(pipe);
                var line = await reader.ReadLineAsync(token);
                if (string.IsNullOrEmpty(line))
                    return;
                onArgs(line.Split('\0'));
            }
        }
        catch (Exception)
        {
            /* connection dropped or cancelled */
        }
    }
}