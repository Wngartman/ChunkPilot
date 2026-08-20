using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.App;

public interface IAgentClient
{
    Task EnsureConnectedAsync(CancellationToken cancellationToken = default);
    Task<TResponse> SendAsync<TResponse>(string operation, object? payload = null, CancellationToken cancellationToken = default);
    bool TrySendOneWay(string operation, object? payload = null, int connectTimeoutMilliseconds = 250) => false;
}

public sealed class AgentClient : IAgentClient
{
    internal const int InitialProbeTimeoutMilliseconds = 200;
    private readonly SemaphoreSlim startupGate = new(1, 1);

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (await CanPingAsync(cancellationToken).ConfigureAwait(false))
            return;
        await startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CanPingAsync(cancellationToken).ConfigureAwait(false))
                return;
            var executable = ResolveAgentExecutable();
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
                throw new InvalidOperationException("Windows did not start ChunkPilot.Agent.");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                if (await CanPingAsync(cancellationToken).ConfigureAwait(false))
                    return;
                if (process.HasExited)
                    throw new InvalidOperationException($"ChunkPilot.Agent exited with code {process.ExitCode}.");
            }
            throw new TimeoutException("ChunkPilot.Agent did not open its named pipe within 15 seconds.");
        }
        finally
        {
            startupGate.Release();
        }
    }

    public async Task<TResponse> SendAsync<TResponse>(
        string operation,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", ChunkPilotConstants.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
        var request = new AgentRequest
        {
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload ?? new { }, ProtocolJson.Options)
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ProtocolJson.Options)).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                   ?? throw new IOException("Agent disconnected before returning a response.");
        var response = JsonSerializer.Deserialize<AgentResponse>(line, ProtocolJson.Options)
                       ?? throw new IOException("Agent returned an invalid response.");
        if (!response.Success)
            throw new InvalidOperationException(response.Error);
        if (response.Payload is not { } responsePayload)
            throw new IOException("Agent returned no response payload.");
        return responsePayload.Deserialize<TResponse>(ProtocolJson.Options)
               ?? throw new IOException("Agent returned an unexpected payload.");
    }

    public bool TrySendOneWay(string operation, object? payload = null, int connectTimeoutMilliseconds = 250)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", ChunkPilotConstants.PipeName,
                PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(Math.Clamp(connectTimeoutMilliseconds, 25, 1_000));
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 16 * 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            var request = new AgentRequest
            {
                Operation = operation,
                Payload = JsonSerializer.SerializeToElement(payload ?? new { }, ProtocolJson.Options)
            };
            writer.WriteLine(JsonSerializer.Serialize(request, ProtocolJson.Options));
            writer.Flush();
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<bool> CanPingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", ChunkPilotConstants.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(InitialProbeTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
                bufferSize: 16 * 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 16 * 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            var request = new AgentRequest
            {
                Operation = "Ping",
                Payload = JsonSerializer.SerializeToElement(new { }, ProtocolJson.Options)
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ProtocolJson.Options)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var response = line is null ? null : JsonSerializer.Deserialize<AgentResponse>(line, ProtocolJson.Options);
            if (response?.Success != true)
                return false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException) { return false; }
    }

    private static string ResolveAgentExecutable()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "Agent", "ChunkPilot.Agent.exe");
        if (File.Exists(direct))
            return direct;
        var siblingBuild = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "ChunkPilot.Agent", "bin",
            AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug",
            "net10.0", "ChunkPilot.Agent.exe"));
        if (File.Exists(siblingBuild))
            return siblingBuild;
        throw new FileNotFoundException("ChunkPilot.Agent.exe was not found beside the app or in the local build output.", direct);
    }
}
