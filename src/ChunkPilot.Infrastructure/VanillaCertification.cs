using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public enum VanillaCertificationResult
{
    Passed,
    FailedRuntimeStartup,
    FailedReadiness,
    FailedCleanStop,
    FailedCapabilityCheck,
    BlockedMissingOfficialArtifact,
    BlockedIncompleteIntegrityMetadata,
    BlockedUnresolvedJava,
    BlockedUnresolvedLaunchProfile,
    BlockedEulaAuthorization,
    BlockedEnvironment,
    Cancelled
}

public sealed record VanillaCertificationEntry
{
    public string VersionId { get; init; } = "";
    public string ArtifactSha1 { get; init; } = "";
    public string MetadataSha1 { get; init; } = "";
    public MinecraftReleaseKind ReleaseKind { get; init; }
    public int? JavaMajor { get; init; }
    public MinecraftLaunchProfileKind LaunchProfile { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public VanillaCertificationResult Result { get; init; }
    public string Reason { get; init; } = "";
    public string JavaPath { get; init; } = "";
    public bool RuntimeLaunched { get; init; }
    public bool ReadinessConfirmed { get; init; }
    public bool CleanStopConfirmed { get; init; }
    public bool ExpectedFilesConfirmed { get; init; }
    public bool NoUnexpectedGuiConfirmed { get; init; }
    public bool StatusPingConfirmed { get; init; }
    public bool CleanupSucceeded { get; init; }
    public int RetryCount { get; init; }
    public string DiagnosticLog { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public sealed record VanillaCertificationLedger
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid CampaignId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ExplicitEulaAuthorization { get; init; }
    public DateTimeOffset? CatalogRetrievedAt { get; init; }
    public string LatestReleaseId { get; init; } = "";
    public IReadOnlyList<VanillaCertificationEntry> Entries { get; init; } = [];
}

public sealed record VanillaCertificationCampaignOptions
{
    public required string CacheRoot { get; init; }
    public required string LedgerPath { get; init; }
    public string? ExactVersion { get; init; }
    public string Category { get; init; } = "all";
    public bool ExplicitEulaAuthorization { get; init; }
    public bool Resume { get; init; } = true;
    public bool RetryFailed { get; init; }
    public bool Force { get; init; }
    public int MaximumConcurrency { get; init; } = 1;
    public TimeSpan PerVersionTimeout { get; init; } = TimeSpan.FromMinutes(4);
    public string ExpectedGeneratedDirectory { get; init; } = "";
}

public sealed record VanillaRuntimeCertificationOutcome
{
    public VanillaCertificationResult Result { get; init; }
    public string Reason { get; init; } = "";
    public string JavaPath { get; init; } = "";
    public bool RuntimeLaunched { get; init; }
    public bool ReadinessConfirmed { get; init; }
    public bool CleanStopConfirmed { get; init; }
    public bool ExpectedFilesConfirmed { get; init; }
    public bool NoUnexpectedGuiConfirmed { get; init; }
    public bool StatusPingConfirmed { get; init; }
    public bool CleanupSucceeded { get; init; }
    public string DiagnosticLog { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public interface IVanillaRuntimeCertifier
{
    Task<VanillaRuntimeCertificationOutcome> CertifyAsync(
        VanillaVersionOption version,
        VanillaCertificationCampaignOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resumable campaign orchestration. The absence of explicit EULA authorization is a terminal,
/// truthful result for the current campaign; it never falls through to artifact or Java execution.
/// </summary>
public sealed class VanillaCertificationCampaign(IVanillaRuntimeCertifier runtime)
{
    private readonly object ledgerWriteLock = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<VanillaCertificationLedger> RunAsync(
        VanillaVersionCatalog catalog,
        VanillaCertificationCampaignOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        var cacheRoot = Path.GetFullPath(options.CacheRoot);
        var ledgerPath = Path.GetFullPath(options.LedgerPath);
        if (!ledgerPath.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The certification ledger must remain inside the isolated certification cache.");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);

        var selected = Select(catalog.Options, options).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("No official catalog entries matched the requested certification selection.");
        if (options.ExplicitEulaAuthorization)
            ValidateStorage(selected, cacheRoot);

        var prior = options.Resume ? ReadLedger(ledgerPath) : null;
        var entries = new ConcurrentDictionary<string, VanillaCertificationEntry>(StringComparer.OrdinalIgnoreCase);
        if (prior is not null)
            foreach (var entry in prior.Entries)
                entries[entry.VersionId] = entry;

        var campaign = new VanillaCertificationLedger
        {
            CampaignId = prior?.CampaignId ?? Guid.NewGuid(),
            StartedAt = prior?.StartedAt ?? DateTimeOffset.UtcNow,
            ExplicitEulaAuthorization = options.ExplicitEulaAuthorization,
            CatalogRetrievedAt = catalog.RetrievedUtc,
            LatestReleaseId = catalog.ManifestLatestReleaseId,
            Entries = entries.Values.OrderBy(entry => entry.VersionId, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        WriteLedger(ledgerPath, campaign);

        var gate = new SemaphoreSlim(Math.Clamp(options.MaximumConcurrency, 1, 4));
        var tasks = selected.Select(async version =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.TryGetValue(version.VersionId, out var existing) &&
                ShouldKeep(existing, version, options))
            {
                if (!existing.CleanupSucceeded && NoResidualWorkRoot(options.CacheRoot, version.VersionId))
                {
                    existing = existing with { CleanupSucceeded = true };
                    entries[version.VersionId] = existing;
                }
                progress?.Report($"{version.VersionId}: retained {existing.Result} from the resumable ledger.");
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var started = DateTimeOffset.UtcNow;
                VanillaRuntimeCertificationOutcome outcome;
                var preflight = Preflight(version, options.ExplicitEulaAuthorization);
                if (preflight is not null)
                {
                    outcome = preflight;
                }
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(options.PerVersionTimeout);
                    try
                    {
                        outcome = await runtime.CertifyAsync(version, options, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        outcome = new VanillaRuntimeCertificationOutcome
                        {
                            Result = VanillaCertificationResult.FailedReadiness,
                            Reason = $"The exact runtime certification exceeded {options.PerVersionTimeout.TotalSeconds:0} seconds.",
                            CleanupSucceeded = false
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // One malformed provider response, unavailable runtime source, or unexpected
                        // certifier defect must become resumable evidence for this exact version. It
                        // must not abort the remaining campaign before the ledger can be finalized.
                        outcome = new VanillaRuntimeCertificationOutcome
                        {
                            Result = IsEnvironmentBlock(exception)
                                ? VanillaCertificationResult.BlockedEnvironment
                                : VanillaCertificationResult.FailedRuntimeStartup,
                            Reason = SecretRedactor.Redact(exception.Message),
                            CleanupSucceeded = false
                        };
                    }
                }

                var retry = entries.TryGetValue(version.VersionId, out var old) ? old.RetryCount + 1 : 0;
                entries[version.VersionId] = new VanillaCertificationEntry
                {
                    VersionId = version.VersionId,
                    ArtifactSha1 = version.ServerSha1,
                    MetadataSha1 = version.MetadataSha1,
                    ReleaseKind = version.ReleaseKind,
                    JavaMajor = version.RequiredJavaMajor,
                    LaunchProfile = version.LaunchProfile.Kind,
                    StartedAt = outcome.RuntimeLaunched ? started : null,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Result = outcome.Result,
                    Reason = outcome.Reason,
                    JavaPath = outcome.JavaPath,
                    RuntimeLaunched = outcome.RuntimeLaunched,
                    ReadinessConfirmed = outcome.ReadinessConfirmed,
                    CleanStopConfirmed = outcome.CleanStopConfirmed,
                    ExpectedFilesConfirmed = outcome.ExpectedFilesConfirmed,
                    NoUnexpectedGuiConfirmed = outcome.NoUnexpectedGuiConfirmed,
                    StatusPingConfirmed = outcome.StatusPingConfirmed,
                    CleanupSucceeded = outcome.CleanupSucceeded,
                    RetryCount = retry,
                    DiagnosticLog = outcome.DiagnosticLog,
                    Evidence = outcome.Evidence
                };
                WriteLedger(ledgerPath, campaign with
                {
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Entries = Ordered(entries.Values).ToArray()
                });
                progress?.Report($"{version.VersionId}: {outcome.Result} — {outcome.Reason}");
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            foreach (var version in selected.Where(version => !entries.ContainsKey(version.VersionId)))
                entries[version.VersionId] = EntryForCancellation(version);
            throw;
        }
        finally
        {
            campaign = campaign with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Entries = Ordered(entries.Values).ToArray()
            };
            WriteLedger(ledgerPath, campaign);
        }
        return campaign;
    }

    public static VanillaRuntimeCertificationOutcome? Preflight(
        VanillaVersionOption version,
        bool explicitEulaAuthorization)
    {
        if (!version.HasServerDownload)
            return Blocked(VanillaCertificationResult.BlockedMissingOfficialArtifact,
                "Mojang's official metadata publishes no dedicated server artifact for this entry.");
        if (version.ServerSizeBytes is not > 0 || string.IsNullOrWhiteSpace(version.ServerSha1))
            return Blocked(VanillaCertificationResult.BlockedIncompleteIntegrityMetadata,
                "The official server artifact lacks the SHA-1 or size required for integrity verification.");
        if (version.RequiredJavaMajor is null or < 8)
            return Blocked(VanillaCertificationResult.BlockedUnresolvedJava,
                "No defensible Java major version is established for this exact entry.");
        if (!version.LaunchProfile.IsResolved)
            return Blocked(VanillaCertificationResult.BlockedUnresolvedLaunchProfile,
                string.IsNullOrWhiteSpace(version.LaunchProfile.Evidence)
                    ? "No safe managed launch profile is established for this exact entry."
                    : version.LaunchProfile.Evidence);
        if (!explicitEulaAuthorization)
            return Blocked(VanillaCertificationResult.BlockedEulaAuthorization,
                "Exact runtime certification is blocked because this campaign was not explicitly authorized to write a disposable eula=true file.");
        return null;
    }

    private static VanillaRuntimeCertificationOutcome Blocked(VanillaCertificationResult result, string reason) => new()
    {
        Result = result,
        Reason = reason,
        CleanupSucceeded = true,
        Evidence = ["Official catalog preflight completed; no server process was started."]
    };

    private static bool IsEnvironmentBlock(Exception exception) =>
        exception is HttpRequestException && exception.GetBaseException() is SocketException socket &&
        socket.SocketErrorCode == SocketError.AccessDenied;

    private static IEnumerable<VanillaVersionOption> Select(
        IEnumerable<VanillaVersionOption> source,
        VanillaCertificationCampaignOptions options)
    {
        var query = source;
        if (!string.IsNullOrWhiteSpace(options.ExactVersion))
            query = query.Where(version => version.VersionId.Equals(options.ExactVersion, StringComparison.OrdinalIgnoreCase));
        query = options.Category.ToLowerInvariant() switch
        {
            "release" or "releases" or "stable" => query.Where(version => version.ReleaseKind == MinecraftReleaseKind.Release),
            "development" or "snapshot" or "snapshots" => query.Where(version => version.ReleaseKind is
                MinecraftReleaseKind.Snapshot or MinecraftReleaseKind.PreRelease or MinecraftReleaseKind.ReleaseCandidate or MinecraftReleaseKind.ExperimentalSnapshot),
            "beta" => query.Where(version => version.ReleaseKind == MinecraftReleaseKind.Beta),
            "alpha" => query.Where(version => version.ReleaseKind == MinecraftReleaseKind.Alpha),
            "all" => query,
            _ => throw new ArgumentException("Category must be all, releases, development, beta, or alpha.")
        };
        return query.OrderBy(version => Priority(version.ReleaseKind))
            .ThenByDescending(version => version.ReleaseTime)
            .ThenBy(version => version.VersionId, StringComparer.OrdinalIgnoreCase);
    }

    private static int Priority(MinecraftReleaseKind kind) => kind switch
    {
        MinecraftReleaseKind.Release => 0,
        MinecraftReleaseKind.ReleaseCandidate => 1,
        MinecraftReleaseKind.PreRelease => 2,
        MinecraftReleaseKind.Snapshot or MinecraftReleaseKind.ExperimentalSnapshot => 3,
        MinecraftReleaseKind.Beta => 4,
        MinecraftReleaseKind.Alpha => 5,
        _ => 6
    };

    internal static bool NoResidualWorkRoot(string cacheRoot, string versionId)
    {
        var work = Path.Combine(Path.GetFullPath(cacheRoot), "work");
        if (!Directory.Exists(work))
            return true;
        var safeVersion = string.Concat(versionId.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        try
        {
            return !Directory.EnumerateDirectories(work, safeVersion + "-*", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ShouldKeep(
        VanillaCertificationEntry existing,
        VanillaVersionOption version,
        VanillaCertificationCampaignOptions options)
    {
        if (options.Force || !existing.ArtifactSha1.Equals(version.ServerSha1, StringComparison.OrdinalIgnoreCase) ||
            !existing.MetadataSha1.Equals(version.MetadataSha1, StringComparison.OrdinalIgnoreCase))
            return false;
        if (existing.Result == VanillaCertificationResult.BlockedEulaAuthorization && options.ExplicitEulaAuthorization)
            return false;
        if (existing.Result == VanillaCertificationResult.BlockedUnresolvedJava && version.RequiredJavaMajor is >= 8)
            return false;
        if (existing.Result == VanillaCertificationResult.BlockedUnresolvedLaunchProfile && version.LaunchProfile.IsResolved)
            return false;
        // A previous campaign may have failed before Java launched because the older resolver
        // requested a JRE-only archive. The current resolver can use the official JDK fallback, so
        // this specific environment-resolution result is stale without reopening unrelated exact
        // runtime failures (for example a server build that genuinely crashes during bootstrap).
        if (existing.Result == VanillaCertificationResult.FailedRuntimeStartup &&
            existing.Reason.Contains("Eclipse Temurin did not return", StringComparison.OrdinalIgnoreCase))
            return false;
        // The first pre-restart campaign was interrupted by a machine-level outbound socket denial
        // while fetching legacy official Mojang artifacts. That is transient environment evidence,
        // not a statement about the exact server build, so retry only this narrow recorded failure.
        if (existing.Result == VanillaCertificationResult.BlockedEnvironment &&
            existing.Reason.Contains("launcher.mojang.com:443", StringComparison.OrdinalIgnoreCase) &&
            existing.Reason.Contains("socket", StringComparison.OrdinalIgnoreCase))
            return false;
        if (options.RetryFailed && existing.Result is VanillaCertificationResult.FailedRuntimeStartup or
            VanillaCertificationResult.FailedReadiness or VanillaCertificationResult.FailedCleanStop or
            VanillaCertificationResult.FailedCapabilityCheck or VanillaCertificationResult.BlockedEnvironment)
            return false;
        return true;
    }

    private static void ValidateStorage(IReadOnlyCollection<VanillaVersionOption> versions, string cacheRoot)
    {
        var forecast = AdditionalArtifactBytes(versions, cacheRoot);
        var drive = new DriveInfo(Path.GetPathRoot(cacheRoot)!);
        var reserve = 2L * 1024 * 1024 * 1024;
        if (drive.AvailableFreeSpace < forecast + reserve)
            throw new IOException("The certification cache drive does not have enough free space for the selected campaign and cleanup reserve.");
    }

    internal static long AdditionalArtifactBytes(
        IEnumerable<VanillaVersionOption> versions,
        string cacheRoot)
    {
        var artifacts = Path.Combine(cacheRoot, "artifacts");
        return versions
            .Where(version => version.ServerSizeBytes is > 0 && version.ServerSha1.Length == 40)
            .Where(version =>
            {
                var cached = Path.Combine(artifacts, version.ServerSha1.ToLowerInvariant() + ".jar");
                return !File.Exists(cached) || new FileInfo(cached).Length != version.ServerSizeBytes;
            })
            .Sum(version => version.ServerSizeBytes!.Value);
    }

    private static VanillaCertificationLedger? ReadLedger(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var ledger = JsonSerializer.Deserialize<VanillaCertificationLedger>(File.ReadAllText(path), Json);
            return ledger?.SchemaVersion == VanillaCertificationLedger.CurrentSchemaVersion ? ledger : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteLedger(string path, VanillaCertificationLedger ledger)
    {
        lock (ledgerWriteLock)
        {
            var partial = path + ".partial";
            File.WriteAllText(partial, JsonSerializer.Serialize(ledger, Json), new UTF8Encoding(false));
            File.Move(partial, path, true);
        }
    }

    private static IOrderedEnumerable<VanillaCertificationEntry> Ordered(IEnumerable<VanillaCertificationEntry> entries) =>
        entries.OrderBy(entry => Priority(entry.ReleaseKind))
            .ThenByDescending(entry => entry.CompletedAt)
            .ThenBy(entry => entry.VersionId, StringComparer.OrdinalIgnoreCase);

    private static VanillaCertificationEntry EntryForCancellation(VanillaVersionOption version) => new()
    {
        VersionId = version.VersionId,
        ArtifactSha1 = version.ServerSha1,
        MetadataSha1 = version.MetadataSha1,
        ReleaseKind = version.ReleaseKind,
        JavaMajor = version.RequiredJavaMajor,
        LaunchProfile = version.LaunchProfile.Kind,
        CompletedAt = DateTimeOffset.UtcNow,
        Result = VanillaCertificationResult.Cancelled,
        Reason = "The certification campaign was cancelled before this exact entry completed.",
        CleanupSucceeded = true
    };
}

/// <summary>Exact loopback-only runtime verifier used only by the explicit certification command.</summary>
public sealed class VanillaRuntimeCertifier : IVanillaRuntimeCertifier, IAsyncDisposable
{
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;
    private readonly ManagedJavaRuntimeService java;
    private readonly HttpClient http;
    private readonly SemaphoreSlim javaGate = new(1, 1);
    private readonly ConcurrentDictionary<int, ManagedJavaRuntime> runtimes = new();
    private readonly IReadOnlyDictionary<int, string> explicitJavaPaths;

    public VanillaRuntimeCertifier(
        string cacheRoot,
        HttpClient? httpClient = null,
        IReadOnlyDictionary<int, string>? explicitJavaPaths = null)
    {
        var root = Path.GetFullPath(cacheRoot);
        paths = new AppDataPaths(Path.Combine(root, "runtime-data"), Path.Combine(root, "runtime-servers"));
        paths.EnsureCreated();
        store = new ChunkPilotStore(paths);
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        java = new ManagedJavaRuntimeService(paths, store, new AdoptiumTemurinProvider(http), http);
        this.explicitJavaPaths = explicitJavaPaths ?? new Dictionary<int, string>();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

    public async Task<VanillaRuntimeCertificationOutcome> CertifyAsync(
        VanillaVersionOption version,
        VanillaCertificationCampaignOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.ExplicitEulaAuthorization)
            throw new InvalidOperationException("Runtime certification requires explicit disposable EULA authorization.");
        var diagnostics = Path.Combine(options.CacheRoot, "diagnostics");
        var logs = new ConcurrentQueue<string>();
        ManagedJavaRuntime? runtime = null;
        var runRoot = "";
        Process? process = null;
        var readiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = false;
        var diagnosticPath = "";
        try
        {
            var artifact = await GetArtifactAsync(version, options.CacheRoot, cancellationToken).ConfigureAwait(false);
            runtime = await GetJavaAsync(version.RequiredJavaMajor!.Value, cancellationToken).ConfigureAwait(false);
            runRoot = Path.Combine(options.CacheRoot, "work", $"{Safe(version.VersionId)}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runRoot);
            Directory.CreateDirectory(diagnostics);
            var port = AllocateLoopbackPort();
            await File.WriteAllTextAsync(Path.Combine(runRoot, "eula.txt"), "eula=true\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(runRoot, "server.properties"), CertificationServerProperties(port),
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo
            {
                FileName = runtime.JavaPath,
                WorkingDirectory = runRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-Xms256M", "-Xmx1024M", "-jar", artifact, version.LaunchProfile.Arguments })
                if (!string.IsNullOrWhiteSpace(argument)) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the owned Java process.");
            void ReadLine(string source, string? line)
            {
                if (line is null) return;
                logs.Enqueue($"[{source}] {line}");
                while (logs.Count > 4000) logs.TryDequeue(out _);
                if (line.Contains(version.LaunchProfile.ReadinessPattern, StringComparison.OrdinalIgnoreCase))
                    readiness.TrySetResult();
            }
            process.OutputDataReceived += (_, eventArgs) => ReadLine("stdout", eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => ReadLine("stderr", eventArgs.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var exit = process.WaitForExitAsync(cancellationToken);
            var winner = await Task.WhenAny(readiness.Task, exit).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (winner == exit)
                return await FailureAsync(VanillaCertificationResult.FailedRuntimeStartup,
                    $"The exact server process exited before readiness with code {process.ExitCode}.", runtime.JavaPath,
                    true, false, false, process.MainWindowHandle == IntPtr.Zero, logs, diagnostics, version.VersionId, runRoot).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
                return await FailureAsync(VanillaCertificationResult.FailedRuntimeStartup,
                    $"The exact server process exited during the stability window with code {process.ExitCode}.", runtime.JavaPath,
                    true, true, false, process.MainWindowHandle == IntPtr.Zero, logs, diagnostics, version.VersionId, runRoot).ConfigureAwait(false);
            var noGui = process.MainWindowHandle == IntPtr.Zero;
            // Historical versions do not necessarily implement the modern JSON status handshake.
            // Certification must prove one exact, bounded status strategy rather than treating a
            // disabled modern capability flag as an automatic pass.
            var statusEvidence = await new MinecraftStatusClient()
                .QueryDetailedAsync("127.0.0.1", port, version.VersionId, cancellationToken)
                .ConfigureAwait(false);
            var ping = statusEvidence is { Exact: true, Online: not null, Maximum: not null };
            await process.StandardInput.WriteLineAsync("list").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(version.LaunchProfile.StopCommand).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return await FailureAsync(VanillaCertificationResult.FailedCleanStop,
                    "The exact server did not stop within the bounded clean-stop window.", runtime.JavaPath,
                    true, true, false, noGui, logs, diagnostics, version.VersionId, runRoot).ConfigureAwait(false);
            }
            var expected = File.Exists(Path.Combine(runRoot, "server.properties")) &&
                           (Directory.Exists(Path.Combine(runRoot, "world")) || Directory.Exists(Path.Combine(runRoot, "logs"))) &&
                           (string.IsNullOrWhiteSpace(options.ExpectedGeneratedDirectory) ||
                            Directory.Exists(Path.Combine(runRoot, options.ExpectedGeneratedDirectory)));
            cleanup = DeleteRunRoot(runRoot);
            return new VanillaRuntimeCertificationOutcome
            {
                Result = ping && expected && noGui ? VanillaCertificationResult.Passed : VanillaCertificationResult.FailedCapabilityCheck,
                Reason = ping && expected && noGui
                    ? "The exact official server reached readiness, remained stable, answered supported local checks, and stopped cleanly."
                    : "The runtime started and stopped, but one or more expected capability checks did not pass.",
                JavaPath = runtime.JavaPath,
                RuntimeLaunched = true,
                ReadinessConfirmed = true,
                CleanStopConfirmed = true,
                ExpectedFilesConfirmed = expected,
                NoUnexpectedGuiConfirmed = noGui,
                StatusPingConfirmed = ping,
                CleanupSucceeded = cleanup,
                Evidence =
                [
                    $"Official artifact SHA-1 {version.ServerSha1}",
                    $"Java {runtime.MajorVersion}",
                    $"Loopback port {port}",
                    statusEvidence is null
                        ? "No supported player-status protocol answered."
                        : $"Player status source {statusEvidence.Source}."
                ]
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnosticPath = await WriteDiagnosticsAsync(diagnostics, version.VersionId, logs, exception.Message).ConfigureAwait(false);
            cleanup = string.IsNullOrWhiteSpace(runRoot) || DeleteRunRoot(runRoot);
            return new VanillaRuntimeCertificationOutcome
            {
                Result = IsEnvironmentBlock(exception)
                    ? VanillaCertificationResult.BlockedEnvironment
                    : VanillaCertificationResult.FailedRuntimeStartup,
                Reason = SecretRedactor.Redact(exception.Message),
                JavaPath = runtime?.JavaPath ?? "",
                RuntimeLaunched = process is not null,
                NoUnexpectedGuiConfirmed = process is null || process.MainWindowHandle == IntPtr.Zero,
                CleanupSucceeded = cleanup,
                DiagnosticLog = diagnosticPath
            };
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
            if (!cleanup && !string.IsNullOrWhiteSpace(runRoot))
                _ = DeleteRunRoot(runRoot);
        }
    }

    private async Task<ManagedJavaRuntime> GetJavaAsync(int major, CancellationToken cancellationToken)
    {
        if (runtimes.TryGetValue(major, out var existing)) return existing;
        await javaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtimes.TryGetValue(major, out existing)) return existing;
            if (explicitJavaPaths.TryGetValue(major, out var explicitJava))
            {
                var inspected = await InspectExplicitJavaAsync(explicitJava, major, cancellationToken).ConfigureAwait(false);
                runtimes[major] = inspected;
                return inspected;
            }
            var installed = (await store.GetManagedJavaRuntimesAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.MajorVersion == major && item.Health == RuntimeHealth.Healthy);
            installed ??= await java.InstallAsync(major, cancellationToken: cancellationToken).ConfigureAwait(false);
            runtimes[major] = installed;
            return installed;
        }
        finally { javaGate.Release(); }
    }

    private static async Task<ManagedJavaRuntime> InspectExplicitJavaAsync(
        string javaPath,
        int expectedMajor,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(javaPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The explicitly supplied certification Java executable was not found.", fullPath);
        var start = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-XshowSettings:properties");
        start.ArgumentList.Add("-version");
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Windows did not start the explicitly supplied Java health check.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false) + Environment.NewLine +
                     await errorTask.ConfigureAwait(false);
        var marker = output.Contains("version \"1.", StringComparison.OrdinalIgnoreCase)
            ? "version \"1." : "version \"";
        var index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var digits = index < 0 ? "" : new string(output.Skip(index + marker.Length).TakeWhile(char.IsDigit).ToArray());
        var actualMajor = int.TryParse(digits, out var parsed) ? parsed : 0;
        var x64 = output.Contains("sun.arch.data.model = 64", StringComparison.OrdinalIgnoreCase) ||
                  output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase);
        if (process.ExitCode != 0 || actualMajor != expectedMajor || !x64)
            throw new InvalidDataException(
                $"The explicit Java path did not verify as healthy 64-bit Java {expectedMajor}.");
        return new ManagedJavaRuntime
        {
            Vendor = output.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
                     output.Contains("Eclipse Adoptium", StringComparison.OrdinalIgnoreCase)
                ? "Eclipse Temurin" : "User-supplied certification runtime",
            Version = $"Java {actualMajor}",
            MajorVersion = actualMajor,
            Architecture = "x64",
            JavaPath = fullPath,
            InstallationRoot = Path.GetDirectoryName(Path.GetDirectoryName(fullPath)!)!,
            SourceUrl = "local-certification-path",
            IsManaged = false,
            Health = RuntimeHealth.Healthy,
            LastHealthCheckAt = DateTimeOffset.UtcNow
        };
    }

    internal async Task<string> GetArtifactAsync(VanillaVersionOption version, string cacheRoot, CancellationToken cancellationToken)
    {
        var artifacts = Path.Combine(cacheRoot, "artifacts");
        Directory.CreateDirectory(artifacts);
        var target = Path.Combine(artifacts, version.ServerSha1.ToLowerInvariant() + ".jar");
        if (File.Exists(target) && await IsValidArtifactAsync(target, version, cancellationToken).ConfigureAwait(false)) return target;
        var partial = target + ".partial";
        try
        {
            using var response = await http.GetAsync(version.ServerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!await IsValidArtifactAsync(partial, version, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The downloaded official server artifact did not match Mojang's SHA-1 and size.");
            File.Move(partial, target, true);
            return target;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static async Task<bool> IsValidArtifactAsync(string path, VanillaVersionOption version, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != version.ServerSizeBytes) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
#pragma warning disable CA5350 // Mojang's official artifact metadata is SHA-1; this is integrity validation, not password/security hashing.
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5350
        return Convert.ToHexString(hash).Equals(version.ServerSha1, StringComparison.OrdinalIgnoreCase);
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static string CertificationServerProperties(int port) => string.Join('\n',
        "server-ip=127.0.0.1", $"server-port={port}", "online-mode=false", "enable-query=false",
        "enable-rcon=false", "max-players=1", "motd=ChunkPilot certification", "view-distance=4",
        "simulation-distance=4", "");

    private static async Task<VanillaRuntimeCertificationOutcome> FailureAsync(
        VanillaCertificationResult result, string reason, string javaPath, bool launched, bool ready, bool stopped,
        bool noGui, ConcurrentQueue<string> logs, string diagnostics, string version, string runRoot)
    {
        var path = await WriteDiagnosticsAsync(diagnostics, version, logs, reason).ConfigureAwait(false);
        return new VanillaRuntimeCertificationOutcome
        {
            Result = result, Reason = reason, JavaPath = javaPath, RuntimeLaunched = launched,
            ReadinessConfirmed = ready, CleanStopConfirmed = stopped, NoUnexpectedGuiConfirmed = noGui,
            CleanupSucceeded = DeleteRunRoot(runRoot), DiagnosticLog = path
        };
    }

    private static async Task<string> WriteDiagnosticsAsync(string directory, string version, IEnumerable<string> logs, string reason)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Safe(version)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
        await File.WriteAllLinesAsync(path, new[] { reason }.Concat(logs.TakeLast(4000)), new UTF8Encoding(false)).ConfigureAwait(false);
        return path;
    }

    private static bool DeleteRunRoot(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); return !Directory.Exists(path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string Safe(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static bool IsEnvironmentBlock(Exception exception) =>
        exception is HttpRequestException && exception.GetBaseException() is SocketException socket &&
        socket.SocketErrorCode == SocketError.AccessDenied;

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        javaGate.Dispose();
        await store.DisposeAsync().ConfigureAwait(false);
    }
}
