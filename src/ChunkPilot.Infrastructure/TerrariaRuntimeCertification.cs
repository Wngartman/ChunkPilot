using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Runs the official Windows Terraria server only in a unique disposable loopback root.</summary>
public sealed class TerrariaRuntimeCertifier
{
    private readonly OfficialTerrariaProvider provider;

    public TerrariaRuntimeCertifier(OfficialTerrariaProvider? provider = null) =>
        this.provider = provider ?? new OfficialTerrariaProvider();

    public async Task<TerrariaCertificationEvidence> CertifyAsync(
        string cacheRoot,
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var canonicalCache = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(canonicalCache);
        var operationId = Guid.NewGuid();
        var workRoot = Path.Combine(canonicalCache, "work", operationId.ToString("N"));
        var serverRoot = Path.Combine(workRoot, "server");
        var failureRoot = Path.Combine(canonicalCache, "failures");
        var release = OfficialTerrariaProvider.CurrentRelease();
        var timer = Stopwatch.StartNew();
        Process? process = null;
        var output = new List<string>();
        string localHash = "";
        var artifactValidated = false;
        var readiness = false;
        var localConnection = false;
        var command = false;
        var save = false;
        var cleanStop = false;
        var world = false;
        var portReleased = false;
        var noGui = false;
        int? exitCode = null;
        string readinessLine = "";
        string saveLine = "";
        string stopLine = "";
        string failure = "";
        var failureKind = TerrariaCertificationFailureKind.None;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            Directory.CreateDirectory(serverRoot);
            progress?.Report("Downloading and validating the official Terraria package");
            var materialized = await provider.DownloadAndMaterializeAsync(canonicalCache, serverRoot,
                cancellationToken: token).ConfigureAwait(false);
            localHash = materialized.LocalSha256;
            artifactValidated = true;
            var port = FreeLoopbackPort();
            var worldPath = Path.Combine(serverRoot, "Worlds", "certification.wld");
            var configurationPath = await TerrariaServerConfigurationWriter.WriteAsync(
                serverRoot, serverRoot, new TerrariaServerConfiguration
                {
                    WorldPath = worldPath,
                    WorldName = "ChunkPilot Certification",
                    AutoCreateSize = 1,
                    Difficulty = 0,
                    MaximumPlayers = 2,
                    Port = port,
                    BindAddress = "127.0.0.1",
                    EnableUpnp = false,
                    Motd = "ChunkPilot isolated certification"
                }, token).ConfigureAwait(false);
            if (File.Exists(Path.Combine(serverRoot, "eula.txt")))
                throw new InvalidDataException("Terraria certification must not create a Minecraft EULA file.");

            var start = new ProcessStartInfo(materialized.ExecutablePath)
            {
                WorkingDirectory = serverRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-config");
            start.ArgumentList.Add(configurationPath);
            start.ArgumentList.Add("-noupnp");
            start.ArgumentList.Add("-ip");
            start.ArgumentList.Add("127.0.0.1");
            progress?.Report($"Launching Terraria {release.Version} on loopback port {port}");
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("TerrariaServer.exe did not start.");
            _ = process.StartTime.ToUniversalTime(); // Forces Windows to resolve this exact process instance now.
            var stdout = PumpAsync(process.StandardOutput, "STDOUT", output, token);
            var stderr = PumpAsync(process.StandardError, "STDERR", output, token);

            var readinessDeadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < readinessDeadline)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException(
                        $"Terraria exited before readiness with code {process.ExitCode}. {OutputTail(output, 10)}");
                if (IsLoopbackListening(port))
                {
                    readiness = true;
                    lock (output)
                        readinessLine = output.LastOrDefault(line =>
                            line.Contains("port", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("server", StringComparison.OrdinalIgnoreCase)) ??
                            $"Owned listener observed on 127.0.0.1:{port}.";
                    break;
                }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
            if (!readiness) throw new TimeoutException("Terraria did not open its loopback listener before the certification deadline.");
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            process.Refresh();
            noGui = process.MainWindowHandle == IntPtr.Zero;

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                localConnection = client.Connected;
            }

            var beforePlaying = OutputCount(output);
            await SendAsync(process, "playing", token).ConfigureAwait(false);
            command = await WaitForNewOutputAsync(output, beforePlaying,
                line => line.Contains("player", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), token)
                .ConfigureAwait(false);
            var beforeSave = OutputCount(output);
            await SendAsync(process, "save", token).ConfigureAwait(false);
            saveLine = await WaitForNewOutputLineAsync(output, beforeSave,
                line => line.Contains("sav", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("backup", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30), token).ConfigureAwait(false) ?? "";
            save = !string.IsNullOrEmpty(saveLine) || File.Exists(worldPath);

            progress?.Report("Saving and stopping the owned Terraria process");
            var beforeStop = OutputCount(output);
            await SendAsync(process, "exit", token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            exitCode = process.ExitCode;
            cleanStop = exitCode == 0;
            lock (output)
                stopLine = output.Skip(beforeStop).LastOrDefault() ?? $"Process exited with code {exitCode}.";
            world = File.Exists(worldPath) && new FileInfo(worldPath).Length > 0;
            portReleased = await WaitForPortReleaseAsync(port, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            if (!cleanStop || !world || !portReleased || !save || !command || !localConnection || !noGui)
            {
                failure = "One or more lifecycle evidence gates did not pass.";
                failureKind = !cleanStop ? TerrariaCertificationFailureKind.Stop :
                    !save || !world ? TerrariaCertificationFailureKind.Save :
                    TerrariaCertificationFailureKind.Unknown;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                           InvalidOperationException or HttpRequestException or OperationCanceledException or
                                           TimeoutException or SocketException)
        {
            failure = SecretRedactor.Redact(exception.Message);
            failureKind = ClassifyFailure(exception, failure, artifactValidated, readiness);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    exitCode ??= process.HasExited ? process.ExitCode : null;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    failureKind = TerrariaCertificationFailureKind.Cleanup;
                    failure = string.IsNullOrEmpty(failure)
                        ? SecretRedactor.Redact(exception.Message)
                        : failure + " Cleanup: " + SecretRedactor.Redact(exception.Message);
                }
                process.Dispose();
            }
            if (!string.IsNullOrEmpty(failure) && Directory.Exists(workRoot))
            {
                Directory.CreateDirectory(failureRoot);
                var diagnostic = Path.Combine(failureRoot, $"terraria-{operationId:N}.log");
                try
                {
                    string[] lines;
                    lock (output) lines = output.TakeLast(1_000).ToArray();
                    await File.WriteAllLinesAsync(diagnostic, lines, new UTF8Encoding(false), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (IOException) { }
            }
        }

        var cleanup = false;
        try
        {
            if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
            cleanup = !Directory.Exists(workRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failureKind = TerrariaCertificationFailureKind.Cleanup;
            failure = string.IsNullOrEmpty(failure)
                ? "Disposable-root cleanup failed: " + SecretRedactor.Redact(exception.Message)
                : failure + " Disposable-root cleanup failed: " + SecretRedactor.Redact(exception.Message);
        }
        timer.Stop();
        return new TerrariaCertificationEvidence
        {
            Version = release.Version,
            LocalArtifactSha256 = localHash,
            TestedAt = DateTimeOffset.UtcNow,
            ArtifactValidated = artifactValidated,
            ReadinessConfirmed = readiness,
            LocalConnectionConfirmed = localConnection,
            ConsoleCommandConfirmed = command,
            SaveConfirmed = save,
            CleanStopConfirmed = cleanStop,
            WorldCreated = world,
            PortReleased = portReleased,
            CleanupConfirmed = cleanup,
            NoUnexpectedGuiConfirmed = noGui,
            ExitCode = exitCode,
            Elapsed = timer.Elapsed,
            ReadinessEvidence = readinessLine,
            SaveEvidence = saveLine,
            StopEvidence = stopLine,
            Limitation = "The official package has no first-party cryptographic checksum; SHA-256 is locally calculated. Public networking was not exercised.",
            FailureKind = failureKind,
            Failure = failure
        };
    }

    public static bool Passed(TerrariaCertificationEvidence evidence) =>
        evidence.ArtifactValidated && evidence.ReadinessConfirmed && evidence.LocalConnectionConfirmed &&
        evidence.ConsoleCommandConfirmed && evidence.SaveConfirmed && evidence.CleanStopConfirmed &&
        evidence.WorldCreated && evidence.PortReleased && evidence.CleanupConfirmed &&
        evidence.NoUnexpectedGuiConfirmed && string.IsNullOrEmpty(evidence.Failure);

    public static async Task WriteEvidenceAsync(
        string path,
        TerrariaCertificationEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static async Task PumpAsync(
        StreamReader reader,
        string stream,
        List<string> output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lock (output)
            {
                output.Add($"{DateTimeOffset.UtcNow:O} {stream} {SecretRedactor.Redact(line)}");
                if (output.Count > 2_000) output.RemoveRange(0, output.Count - 2_000);
            }
        }
    }

    private static async Task SendAsync(Process process, string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (process.HasExited) throw new InvalidOperationException("The owned Terraria process already exited.");
        await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int OutputCount(List<string> output)
    {
        lock (output) return output.Count;
    }

    private static string OutputTail(List<string> output, int maximumLines)
    {
        lock (output)
            return string.Join(" | ", output.TakeLast(maximumLines));
    }

    private static TerrariaCertificationFailureKind ClassifyFailure(
        Exception exception,
        string failure,
        bool artifactValidated,
        bool readiness)
    {
        if (exception is OperationCanceledException) return TerrariaCertificationFailureKind.Cancelled;
        if (failure.Contains("Microsoft.Xna.Framework", StringComparison.OrdinalIgnoreCase))
            return TerrariaCertificationFailureKind.MissingRuntimePrerequisite;
        if (!artifactValidated || exception is InvalidDataException or HttpRequestException)
            return TerrariaCertificationFailureKind.ArtifactValidation;
        if (!readiness)
            return exception is TimeoutException
                ? TerrariaCertificationFailureKind.Readiness
                : TerrariaCertificationFailureKind.Startup;
        return exception is TimeoutException
            ? TerrariaCertificationFailureKind.Stop
            : TerrariaCertificationFailureKind.Unknown;
    }

    private static async Task<bool> WaitForNewOutputAsync(
        List<string> output,
        int start,
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await WaitForNewOutputLineAsync(output, start, predicate, timeout, cancellationToken).ConfigureAwait(false) is not null;

    private static async Task<string?> WaitForNewOutputLineAsync(
        List<string> output,
        int start,
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (output)
            {
                var line = output.Skip(Math.Min(start, output.Count)).FirstOrDefault(predicate);
                if (line is not null) return line;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsLoopbackListening(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(endpoint => endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == port);

    private static async Task<bool> WaitForPortReleaseAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsLoopbackListening(port)) return true;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return !IsLoopbackListening(port);
    }
}
