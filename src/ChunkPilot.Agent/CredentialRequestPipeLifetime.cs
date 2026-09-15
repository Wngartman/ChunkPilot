using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChunkPilot.Agent;

/// <summary>
/// Native credential mutations belong to the open setup request. Other Agent operations retain
/// their existing ownership and are deliberately not bound to transient pipe connections.
/// </summary>
internal sealed class CredentialRequestPipeLifetime : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource request;
    private readonly CancellationTokenSource monitorStop = new();
    private readonly Task monitor;

    public CredentialRequestPipeLifetime(NamedPipeServerStream pipe, CancellationToken hostToken)
    {
        this.pipe = pipe;
        request = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        monitor = WatchDisconnectAsync();
    }

    public CancellationToken Token => request.Token;

    public void DemandConnected()
    {
        Token.ThrowIfCancellationRequested();
        // IsConnected can remain true until the pending read observes EOF. Peek the same handle
        // immediately before committing instead of trusting that cached managed property alone.
        if (!pipe.IsConnected || !PeekNamedPipe(pipe.SafePipeHandle, IntPtr.Zero, 0,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
        {
            request.Cancel();
            Token.ThrowIfCancellationRequested();
        }
    }

    private async Task WatchDisconnectAsync()
    {
        try
        {
            // One request per duplex connection: EOF or unexpected extra input ends this request.
            _ = await pipe.ReadAsync(new byte[1], monitorStop.Token).ConfigureAwait(false);
            request.Cancel();
        }
        catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
        catch (IOException) { request.Cancel(); }
        catch (ObjectDisposedException) { request.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Cancel the outstanding read without closing the pipe; successful callers still need to
        // send their response. Await it before disposing either token source.
        await monitorStop.CancelAsync().ConfigureAwait(false);
        await monitor.ConfigureAwait(false);
        monitorStop.Dispose();
        request.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle namedPipe, IntPtr buffer, uint bufferSize,
        IntPtr bytesRead, IntPtr totalBytesAvailable, IntPtr bytesLeftThisMessage);
}
