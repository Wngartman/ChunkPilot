using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

internal interface ICertificationAgentTransport
{
    Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task<TResponse> SendAsync<TResponse>(
        string operation,
        object? payload = null,
        CancellationToken cancellationToken = default);
}

internal sealed class NamedPipeCertificationAgentTransport(string pipeName) : ICertificationAgentTransport
{
    private const int ConnectTimeoutMilliseconds = 5_000;
    internal const int MaximumResponseFrameBytes = 4 * 1024 * 1024;

    public async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await SendAsync<OperationResult>("Ping", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or
                                               InvalidOperationException)
            {
                lastFailure = exception;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            "The isolated packaged Agent did not open its named pipe before the startup deadline.",
            lastFailure);
    }

    public async Task<TResponse> SendAsync<TResponse>(
        string operation,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        await using var writer = new StreamWriter(
            pipe, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true
        };

        var request = new AgentRequest
        {
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload ?? new { }, ProtocolJson.Options)
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, ProtocolJson.Options))
            .ConfigureAwait(false);
        var line = await ReadBoundedResponseFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<AgentResponse>(line, ProtocolJson.Options)
                       ?? throw new IOException("The Agent returned an invalid response frame.");
        if (!response.RequestId.Equals(request.RequestId, StringComparison.Ordinal))
            throw new IOException("The Agent response identity did not match the request.");
        if (!response.Success)
            throw new InvalidOperationException(response.Error);
        return DeserializeResponsePayload<TResponse>(operation, response);
    }

    internal static TResponse DeserializeResponsePayload<TResponse>(
        string operation,
        AgentResponse response)
    {
        if (response.Payload is not { } responsePayload)
        {
            // These exact lookup operations deliberately use JSON null for an unavailable item.
            // JsonElement? cannot distinguish that value from an omitted payload after
            // deserialization, so preserve only their explicit nullable operation/type contracts.
            if ((operation.Equals("ResolveCatalogProject", StringComparison.Ordinal) &&
                 typeof(TResponse) == typeof(CatalogItem)) ||
                (operation.Equals("PluginRelease", StringComparison.Ordinal) &&
                 typeof(TResponse) == typeof(PluginRelease)))
                return default!;
            throw new IOException("The Agent returned no response payload.");
        }
        return responsePayload.Deserialize<TResponse>(ProtocolJson.Options)
               ?? throw new IOException("The Agent returned an unexpected payload.");
    }

    internal static async Task<string> ReadBoundedResponseFrameAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream(capacity: 64 * 1024);
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("The Agent disconnected before returning a complete response frame.");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline >= 0 ? newline : read;
            if (frame.Length + count > MaximumResponseFrameBytes)
                throw new InvalidDataException(
                    $"The Agent response exceeded the {MaximumResponseFrameBytes}-byte certification pipe limit.");
            frame.Write(buffer, 0, count);
            if (newline < 0)
                continue;

            var bytes = frame.GetBuffer().AsSpan(0, checked((int)frame.Length));
            if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                bytes = bytes[..^1];
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The Agent response was not valid UTF-8.", exception);
            }
        }
    }
}

internal sealed record HeadlessAgentLaunchOptions(
    string AgentExecutablePath,
    string DataRoot,
    string ManagedServersRoot,
    string TemporaryRoot,
    string CredentialSourcePath,
    string InstanceId,
    string? CertificationUpdateFaultToken = null,
    string? CertificationRuntimeRoot = null);

internal static class HeadlessAgentLaunch
{
    internal static ProcessStartInfo CreateStartInfo(HeadlessAgentLaunchOptions options)
    {
        var executable = Path.GetFullPath(options.AgentExecutablePath);
        if (!Path.GetFileName(executable).Equals("ChunkPilot.Agent.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The headless controller only starts ChunkPilot.Agent.exe.");
        var temporary = Path.GetFullPath(options.TemporaryRoot);
        if (!Directory.Exists(temporary) || File.Exists(temporary) ||
            (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException(
                "The headless Agent requires an existing regular task-owned temporary directory.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ChildProcessEnvironmentPolicy.Apply(startInfo);
        startInfo.Environment["CHUNKPILOT_DATA_ROOT"] = Path.GetFullPath(options.DataRoot);
        startInfo.Environment["CHUNKPILOT_MANAGED_SERVERS_ROOT"] =
            Path.GetFullPath(options.ManagedServersRoot);
        startInfo.Environment["CHUNKPILOT_INSTANCE_ID"] = options.InstanceId;
        startInfo.Environment["TEMP"] = temporary;
        startInfo.Environment["TMP"] = temporary;
        startInfo.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = temporary;
        startInfo.Environment[CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable] =
            Path.GetFullPath(options.CredentialSourcePath);
        startInfo.Environment.Remove(ExternalReachabilityProbeOptions.EnvironmentVariable);
        startInfo.Environment.Remove(CertificationUpdateFaultInjector.TokenEnvironmentVariable);
        startInfo.Environment.Remove(CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable);
        if (options.CertificationUpdateFaultToken is not null ||
            options.CertificationRuntimeRoot is not null)
        {
            if (options.CertificationUpdateFaultToken is not { Length: 64 } token ||
                token.Any(character => !Uri.IsHexDigit(character)) ||
                string.IsNullOrWhiteSpace(options.CertificationRuntimeRoot) ||
                !Path.IsPathFullyQualified(options.CertificationRuntimeRoot))
                throw new ArgumentException(
                    "The certification update-failure authority requires an exact capability and runtime root.");
            startInfo.Environment[CertificationUpdateFaultInjector.TokenEnvironmentVariable] = token;
            startInfo.Environment[CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable] =
                Path.GetFullPath(options.CertificationRuntimeRoot);
        }
        return startInfo;
    }
}

internal sealed record ExactOwnedTerminationResult(bool Attempted, bool Succeeded, string Detail);

internal sealed class HeadlessAgentProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly long processCreationTicks;
    private readonly WindowsOwnedProcessJob ownedJob;

    private HeadlessAgentProcess(
        Process process,
        long processCreationTicks,
        WindowsOwnedProcessJob ownedJob,
        bool belowNormalPriorityApplied,
        string priorityWarning)
    {
        this.process = process;
        this.processCreationTicks = processCreationTicks;
        this.ownedJob = ownedJob;
        BelowNormalPriorityApplied = belowNormalPriorityApplied;
        PriorityWarning = priorityWarning;
    }

    public int ProcessId => process.Id;
    public bool HasExited
    {
        get
        {
            process.Refresh();
            return process.HasExited;
        }
    }
    public int? ExitCode => HasExited ? process.ExitCode : null;
    public bool ExactRootProcessIdentityMatches => ProcessCreationIdentity.Matches(
        ProcessCreationIdentity.Of(process.SafeHandle), processCreationTicks);
    public bool BelowNormalPriorityApplied { get; }
    public string PriorityWarning { get; }

    public static HeadlessAgentProcess Start(HeadlessAgentLaunchOptions options)
    {
        var ownedJob = WindowsOwnedProcessJob.Create();
        Process? process = null;
        try
        {
            process = ownedJob.StartSuspendedAssignedAndResume(
                HeadlessAgentLaunch.CreateStartInfo(options));
            var creationTicks = ProcessCreationIdentity.Of(process.SafeHandle);
            if (creationTicks == ProcessCreationIdentity.Unknown)
                throw new InvalidOperationException(
                    "Windows did not expose the exact packaged Agent process-creation identity.");

            var priorityApplied = ownedJob.BelowNormalPriorityApplied;
            var priorityWarning = ownedJob.PriorityWarning;
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                priorityApplied = priorityApplied &&
                                  process.PriorityClass == ProcessPriorityClass.BelowNormal;
                if (!priorityApplied)
                    priorityWarning = AppendWarning(
                        priorityWarning,
                        "Windows did not confirm BelowNormal priority for the isolated Agent Job.");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                               NotSupportedException)
            {
                priorityApplied = false;
                priorityWarning = AppendWarning(
                    priorityWarning,
                    "Windows could not confirm the isolated Agent priority; no system setting was changed. " +
                    SecretRedactor.Redact(exception.Message));
            }
            return new HeadlessAgentProcess(
                process, creationTicks, ownedJob, priorityApplied, priorityWarning);
        }
        catch
        {
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            catch (Exception cleanupException) when (cleanupException is Win32Exception or InvalidOperationException or
                                                      NotSupportedException)
            {
                // The original startup failure remains authoritative; the controller has not yet
                // received a usable process wrapper and must not mask it with cleanup diagnostics.
            }
            finally
            {
                process?.Dispose();
                ownedJob.Dispose();
            }
            throw;
        }
    }

    public IReadOnlyDictionary<int, long> CaptureExactOwnedProcessIdentities() =>
        ownedJob.CaptureActiveProcessIdentities();

    public WindowsOwnedProcessJobSnapshot CaptureExactOwnedProcessSnapshot() =>
        ownedJob.CaptureStableProcessSnapshot();

    public bool ExactOwnedProcessIdentityStillMatches(int processId, long expectedCreationTicks) =>
        ownedJob.IdentityStillMatches(processId, expectedCreationTicks);

    public int ActiveExactOwnedProcessCount => ownedJob.ActiveProcessCount;

    public Task<bool> WaitForExactOwnedTreeExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ownedJob.WaitForEmptyAsync(timeout, cancellationToken);

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (HasExited)
            return true;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<ExactOwnedTerminationResult> TryTerminateExactOwnedTreeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            if (ownedJob.ActiveProcessCount == 0)
                return new ExactOwnedTerminationResult(
                    false, true, "The exact Agent Job had already become empty.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The Job handle itself remains the exact ownership capability. Failure to query its
            // accounting must not prevent the bounded terminate attempt below.
        }
        try
        {
            ownedJob.Terminate();
            var exited = await ownedJob.WaitForEmptyAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return new ExactOwnedTerminationResult(
                true, exited,
                exited
                    ? "The exact-owned Agent process tree was terminated after graceful cleanup failed."
                    : "The exact-owned Agent process tree did not exit after bounded termination.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           NotSupportedException)
        {
            return new ExactOwnedTerminationResult(
                true, false,
                "Exact-owned Agent process-tree termination failed: " +
                SecretRedactor.Redact(exception.Message));
        }
    }

    public ValueTask DisposeAsync()
    {
        process.Dispose();
        ownedJob.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string AppendWarning(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first + " " + second;
}
