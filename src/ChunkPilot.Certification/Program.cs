using System.Text.Json;
using System.Security.Cryptography;
using ChunkPilot.Infrastructure;
using ChunkPilot.Core;

if (args.Length > 0 && args[0].Equals("certify-paper", StringComparison.OrdinalIgnoreCase))
    return await CertifyPaperAsync(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("certify-loader", StringComparison.OrdinalIgnoreCase))
    return await CertifyLoaderAsync(args.Skip(1).ToArray());

if (args.Length > 0 && args[0].Equals("certify-terraria", StringComparison.OrdinalIgnoreCase))
    return await CertifyTerrariaAsync(args.Skip(1).ToArray());

if (args.Length == 0 || !args[0].Equals("certify-vanilla", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: ChunkPilot.Certification certify-vanilla --all [options]");
    Console.Error.WriteLine("       ChunkPilot.Certification certify-paper [--all-stable | --version <id>] [--build <id>] [options]");
    Console.Error.WriteLine("       ChunkPilot.Certification certify-loader --platform <Fabric|Quilt|Forge|NeoForge|LegacyFabric|Ornithe> [--all-stable | --version <id>] [--loader <id>] [options]");
    Console.Error.WriteLine("       ChunkPilot.Certification certify-terraria [--cache <path>] [--timeout-seconds <seconds>]");
    Console.Error.WriteLine("Runtime execution additionally requires --accept-minecraft-eula-for-certification.");
    return 64;
}

var values = args.Skip(1).ToArray();
var repository = FindRepositoryRoot();
var cacheRoot = Path.GetFullPath(Read(values, "--cache") ?? Path.Combine(repository, "artifacts", "vanilla-certification"));
var ledger = Path.GetFullPath(Read(values, "--ledger") ?? Path.Combine(cacheRoot, "vanilla-certification-ledger.json"));
var report = Path.GetFullPath(Read(values, "--report") ?? Path.Combine(cacheRoot, "vanilla-certification-summary.json"));
var evidence = Read(values, "--export-evidence");
var eula = Has(values, "--accept-minecraft-eula-for-certification");
var explicitJavaPaths = ReadJavaPaths(values);
var options = new VanillaCertificationCampaignOptions
{
    CacheRoot = cacheRoot,
    LedgerPath = ledger,
    ExactVersion = Read(values, "--version"),
    Category = Read(values, "--category") ?? "all",
    ExplicitEulaAuthorization = eula,
    Resume = !Has(values, "--no-resume"),
    RetryFailed = Has(values, "--retry-failed"),
    Force = Has(values, "--force"),
    MaximumConcurrency = ReadInt(values, "--max-concurrency", 1, 1, 4),
    PerVersionTimeout = TimeSpan.FromSeconds(ReadInt(values, "--timeout-seconds", 240, 30, 1800))
};

Console.WriteLine($"Certification cache: {cacheRoot}");
Console.WriteLine($"Explicit disposable EULA authorization: {(eula ? "present" : "absent")}");
if (!eula)
    Console.WriteLine("Preflight-only mode: no server JAR, Java runtime, eula.txt, world, or Java process will be created.");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
Directory.CreateDirectory(cacheRoot);
var paths = new AppDataPaths(Path.Combine(cacheRoot, "catalog-data"), Path.Combine(cacheRoot, "catalog-servers"));
var catalogService = new VanillaVersionCatalogService(paths);
var catalog = await catalogService.GetCatalogAsync(includeSnapshots: true, forceRefresh: Has(values, "--refresh"), cancellation.Token);
if (!catalog.ProviderAvailable)
{
    Console.Error.WriteLine(catalog.UnavailableDetail);
    return 69;
}

await using var runtime = new VanillaRuntimeCertifier(cacheRoot, explicitJavaPaths: explicitJavaPaths);
await runtime.InitializeAsync(cancellation.Token);
var campaign = new VanillaCertificationCampaign(runtime);
var progress = new Progress<string>(line => Console.WriteLine(line));
VanillaCertificationLedger result;
try
{
    result = await campaign.RunAsync(catalog, options, progress, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Certification cancelled. The resumable ledger was preserved.");
    return 130;
}

var groups = result.Entries.GroupBy(entry => entry.Result).ToDictionary(group => group.Key.ToString(), group => group.Count());
var summary = new
{
    result.CampaignId,
    result.StartedAt,
    result.UpdatedAt,
    result.ExplicitEulaAuthorization,
    total = result.Entries.Count,
    results = groups,
    attemptedRuntime = result.Entries.Count(entry => entry.RuntimeLaunched),
    passed = result.Entries.Count(entry => entry.Result == VanillaCertificationResult.Passed),
    cleanupFailures = result.Entries.Count(entry => !entry.CleanupSucceeded),
    artifactCacheBytes = Directory.Exists(Path.Combine(cacheRoot, "artifacts"))
        ? Directory.EnumerateFiles(Path.Combine(cacheRoot, "artifacts"), "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
        : 0,
    ledger
};
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
if (!string.IsNullOrWhiteSpace(evidence))
{
    var evidencePath = Path.GetFullPath(evidence);
    Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
    await File.WriteAllTextAsync(
        evidencePath,
        VanillaRuntimeCertificationEvidence.Export(result),
        new System.Text.UTF8Encoding(false),
        cancellation.Token);
    Console.WriteLine($"Production certification evidence: {evidencePath}");
}
Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

var failures = result.Entries.Count(entry => entry.Result is VanillaCertificationResult.FailedRuntimeStartup or
    VanillaCertificationResult.FailedReadiness or VanillaCertificationResult.FailedCleanStop or
    VanillaCertificationResult.FailedCapabilityCheck or VanillaCertificationResult.BlockedEnvironment);
var requestedBlocked = options.ExactVersion is not null && result.Entries.Any(entry => entry.Result.ToString().StartsWith("Blocked", StringComparison.Ordinal));
return failures > 0 ? 2 : requestedBlocked || !eula ? 3 : 0;

static bool Has(IReadOnlyCollection<string> arguments, string name) =>
    arguments.Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));

static string? Read(IReadOnlyList<string> arguments, string name)
{
    for (var index = 0; index < arguments.Count - 1; index++)
        if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase) && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            return arguments[index + 1];
    return null;
}

static int ReadInt(IReadOnlyList<string> arguments, string name, int fallback, int minimum, int maximum) =>
    int.TryParse(Read(arguments, name), out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

static IReadOnlyDictionary<int, string> ReadJavaPaths(IReadOnlyList<string> arguments)
{
    var result = new Dictionary<int, string>();
    for (var index = 0; index < arguments.Count - 1; index++)
    {
        if (!arguments[index].Equals("--java", StringComparison.OrdinalIgnoreCase))
            continue;
        var parts = arguments[index + 1].Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || major < 8 ||
            string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException("Each --java value must use the form <major>=<absolute-java.exe-path>.");
        result[major] = Path.GetFullPath(parts[1]);
    }
    return result;
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
        current = current.Parent;
    return current?.FullName ?? throw new DirectoryNotFoundException("Run the certification command from the ChunkPilot repository.");
}

static async Task<int> CertifyPaperAsync(string[] values)
{
    var repository = FindRepositoryRoot();
    var cacheRoot = Path.GetFullPath(Read(values, "--cache") ?? Path.Combine(repository, "artifacts", "paper-certification"));
    var report = Path.GetFullPath(Read(values, "--report") ?? Path.Combine(cacheRoot, "paper-certification-summary.json"));
    var evidencePath = Path.GetFullPath(Read(values, "--export-evidence") ??
        Path.Combine(cacheRoot, "paper-runtime-certification-evidence.json"));
    var eula = Has(values, "--accept-minecraft-eula-for-certification");
    if (!eula)
    {
        Console.Error.WriteLine("Paper runtime certification requires explicit disposable EULA authorization.");
        return 3;
    }

    Directory.CreateDirectory(cacheRoot);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    var paths = new AppDataPaths(Path.Combine(cacheRoot, "catalog-data"), Path.Combine(cacheRoot, "catalog-servers"));
    var catalogService = new PaperVersionCatalogService(paths);
    var versions = await catalogService.GetVersionsAsync(forceRefresh: Has(values, "--refresh"), cancellation.Token);
    if (!versions.ProviderAvailable)
    {
        Console.Error.WriteLine(versions.UnavailableDetail);
        return 69;
    }
    if (Has(values, "--all-stable"))
    {
        var ledgerPath = Path.GetFullPath(Read(values, "--ledger") ??
            Path.Combine(cacheRoot, "paper-certification-ledger.json"));
        await using var allRuntime = new VanillaRuntimeCertifier(cacheRoot, explicitJavaPaths: ReadJavaPaths(values));
        await allRuntime.InitializeAsync(cancellation.Token);
        var campaign = new PaperCertificationCampaign(catalogService, allRuntime);
        PaperCertificationLedger ledger;
        try
        {
            ledger = await campaign.RunAsync(versions, new PaperCertificationCampaignOptions
            {
                CacheRoot = cacheRoot,
                LedgerPath = ledgerPath,
                ExplicitEulaAuthorization = true,
                Resume = !Has(values, "--no-resume"),
                Force = Has(values, "--force"),
                RetryFailed = Has(values, "--retry-failed"),
                PerVersionTimeout = TimeSpan.FromSeconds(ReadInt(values, "--timeout-seconds", 600, 60, 1800))
            }, new Progress<string>(Console.WriteLine), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Paper certification cancelled. The resumable ledger was preserved.");
            return 130;
        }
        var allSummary = new
        {
            ledger.CampaignId,
            ledger.StartedAt,
            ledger.UpdatedAt,
            total = ledger.Entries.Count,
            passed = ledger.Entries.Count(entry => entry.Result == VanillaCertificationResult.Passed),
            failed = ledger.Entries.Count(entry => entry.Result is VanillaCertificationResult.FailedRuntimeStartup or VanillaCertificationResult.FailedReadiness or VanillaCertificationResult.FailedCleanStop or VanillaCertificationResult.FailedCapabilityCheck),
            blocked = ledger.Entries.Count(entry => entry.Result.ToString().StartsWith("Blocked", StringComparison.Ordinal)),
            cancelled = ledger.Entries.Count(entry => entry.Result == VanillaCertificationResult.Cancelled),
            pending = versions.Versions.Count(entry => entry.IsSelectable) - ledger.Entries.Count,
            cleanupFailures = ledger.Entries.Count(entry => !entry.CleanupSucceeded),
            cacheBytes = Directory.Exists(cacheRoot) ? Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0,
            ledger = ledgerPath
        };
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report,
            JsonSerializer.Serialize(allSummary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            new System.Text.UTF8Encoding(false), cancellation.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        await File.WriteAllTextAsync(evidencePath, PaperRuntimeCertificationEvidence.Export(ledger),
            new System.Text.UTF8Encoding(false), cancellation.Token);
        Console.WriteLine($"Production certification evidence: {evidencePath}");
        Console.WriteLine(JsonSerializer.Serialize(allSummary,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return allSummary.failed > 0 || allSummary.blocked > 0 || allSummary.pending > 0 ? 2 : 0;
    }
    var candidates = versions.Versions.Where(option => option.IsSelectable)
        .OrderByDescending(option => MinecraftVersionClassification.NumericVersion(option.VersionId))
        .ThenByDescending(option => option.VersionId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var requestedVersion = Read(values, "--version");
    var version = string.IsNullOrWhiteSpace(requestedVersion)
        ? candidates.FirstOrDefault()
        : candidates.FirstOrDefault(option => option.VersionId.Equals(requestedVersion, StringComparison.OrdinalIgnoreCase));
    if (version is null)
    {
        Console.Error.WriteLine("The requested Paper Minecraft version is not a selectable stable release in PaperMC's official inventory.");
        return 65;
    }

    var builds = await catalogService.GetBuildsAsync(version.VersionId, forceRefresh: Has(values, "--refresh"), cancellation.Token);
    if (!builds.ProviderAvailable)
    {
        Console.Error.WriteLine(builds.UnavailableDetail);
        return 69;
    }
    var stableBuilds = builds.Builds.Where(option => option.IsSelectable).OrderByDescending(option => option.BuildId).ToArray();
    var requestedBuild = int.TryParse(Read(values, "--build"), out var parsedBuild) ? parsedBuild : (int?)null;
    var build = requestedBuild is null
        ? stableBuilds.FirstOrDefault()
        : stableBuilds.FirstOrDefault(option => option.BuildId == requestedBuild.Value);
    if (build is null || version.RequiredJavaMajor is not { } javaMajor)
    {
        Console.Error.WriteLine("PaperMC did not publish a selectable integrity-complete stable build for this version.");
        return 65;
    }

    Console.WriteLine($"Paper certification: Minecraft {version.VersionId}, build {build.BuildId}, Java {javaMajor}");
    var artifact = await CachePaperArtifactAsync(build, cacheRoot, cancellation.Token);
    var runtimeOption = new VanillaVersionOption
    {
        VersionId = $"paper-{version.VersionId}-{build.BuildId}",
        Channel = VanillaReleaseChannel.Stable,
        ReleaseType = "release",
        ReleaseKind = MinecraftReleaseKind.Release,
        ReleaseTime = build.PublishedAt ?? DateTimeOffset.UtcNow,
        MetadataUrl = PaperVersionCatalogService.ProjectUrl,
        MetadataSha1 = new string('0', 40),
        HasServerDownload = true,
        ServerDownloadUrl = build.DownloadUrl,
        ServerSha1 = artifact.Sha1,
        ServerSizeBytes = build.ServerSizeBytes,
        RequiredJavaMajor = javaMajor,
        JavaRequirementSource = JavaRequirementSource.ChunkPilotPolicy,
        Support = VanillaVersionSupport.Supported,
        SupportTier = MinecraftVersionSupportTier.Experimental,
        LaunchProfile = new MinecraftLaunchProfile
        {
            Kind = MinecraftLaunchProfileKind.ModernEulaNogui,
            Arguments = "--nogui",
            ReadinessPattern = "Done (",
            StopCommand = "stop",
            RequiresEulaFile = true,
            Evidence = "PaperMC headless launch contract.",
            Capabilities = new MinecraftVersionCapabilities { StatusQuery = true }
        },
        Provenance = build.Provenance
    };
    var options = new VanillaCertificationCampaignOptions
    {
        CacheRoot = cacheRoot,
        LedgerPath = Path.Combine(cacheRoot, "paper-certification-ledger-unused.json"),
        ExplicitEulaAuthorization = true,
        PerVersionTimeout = TimeSpan.FromSeconds(ReadInt(values, "--timeout-seconds", 600, 60, 1800)),
        ExpectedGeneratedDirectory = "plugins"
    };
    await using var runtime = new VanillaRuntimeCertifier(cacheRoot);
    await runtime.InitializeAsync(cancellation.Token);
    var started = DateTimeOffset.UtcNow;
    var outcome = await runtime.CertifyAsync(runtimeOption, options, cancellation.Token);
    var recommended = candidates[0].VersionId.Equals(version.VersionId, StringComparison.OrdinalIgnoreCase) &&
                      stableBuilds[0].BuildId == build.BuildId;
    var summary = new
    {
        minecraftVersion = version.VersionId,
        buildId = build.BuildId,
        build.ServerSha256,
        build.ServerSizeBytes,
        javaMajor,
        startedAt = started,
        completedAt = DateTimeOffset.UtcNow,
        result = outcome.Result.ToString(),
        outcome.Reason,
        outcome.RuntimeLaunched,
        outcome.ReadinessConfirmed,
        outcome.StatusPingConfirmed,
        outcome.CleanStopConfirmed,
        outcome.ExpectedFilesConfirmed,
        outcome.NoUnexpectedGuiConfirmed,
        outcome.CleanupSucceeded,
        recommended
    };
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report,
        JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
        new System.Text.UTF8Encoding(false), cancellation.Token);
    if (outcome.Result == VanillaCertificationResult.Passed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        await File.WriteAllTextAsync(evidencePath,
            PaperRuntimeCertificationEvidence.Export(build, javaMajor, outcome, recommended),
            new System.Text.UTF8Encoding(false), cancellation.Token);
        Console.WriteLine($"Production certification evidence: {evidencePath}");
    }
    Console.WriteLine(JsonSerializer.Serialize(summary,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return outcome.Result == VanillaCertificationResult.Passed ? 0 : 2;
}

static async Task<int> CertifyTerrariaAsync(string[] values)
{
    var repository = FindRepositoryRoot();
    var cacheRoot = Path.GetFullPath(Read(values, "--cache") ??
        Path.Combine(repository, "artifacts", "terraria-certification"));
    var report = Path.GetFullPath(Read(values, "--report") ??
        Path.Combine(cacheRoot, "terraria-certification-evidence.json"));
    Directory.CreateDirectory(cacheRoot);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Console.WriteLine($"Terraria certification cache: {cacheRoot}");
    Console.WriteLine("Minecraft EULA authorization is not used: Terraria does not use eula.txt.");
    Console.WriteLine("The server is forced to 127.0.0.1 with UPnP disabled; no firewall or router mutation occurs.");
    var certifier = new TerrariaRuntimeCertifier();
    TerrariaCertificationEvidence evidence;
    try
    {
        evidence = await certifier.CertifyAsync(cacheRoot,
            TimeSpan.FromSeconds(ReadInt(values, "--timeout-seconds", 900, 60, 1_800)),
            new Progress<string>(Console.WriteLine), cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Terraria certification cancelled; owned cleanup was requested.");
        return 130;
    }
    await TerrariaRuntimeCertifier.WriteEvidenceAsync(report, evidence, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(evidence,
        new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }));
    return TerrariaRuntimeCertifier.Passed(evidence) ? 0 : 2;
}

static async Task<int> CertifyLoaderAsync(string[] values)
{
    var platformValue = Read(values, "--platform");
    if (!Enum.TryParse<ManagedLoaderPlatform>(platformValue, true, out var platform) ||
        !Enum.IsDefined(platform))
    {
        Console.Error.WriteLine("certify-loader requires a known managed-loader platform.");
        return 64;
    }
    var eula = Has(values, "--accept-minecraft-eula-for-certification");
    if (!eula)
    {
        Console.Error.WriteLine("Loader runtime certification requires explicit disposable EULA authorization.");
        return 3;
    }
    var repository = FindRepositoryRoot();
    var cacheRoot = Path.GetFullPath(Read(values, "--cache") ??
        Path.Combine(repository, "artifacts", "managed-loader-certification"));
    var reportPath = Path.GetFullPath(Read(values, "--report") ??
        Path.Combine(cacheRoot, $"{platform.ToString().ToLowerInvariant()}-certification-summary.json"));
    var ledgerPath = Path.GetFullPath(Read(values, "--ledger") ??
        Path.Combine(cacheRoot, $"{platform.ToString().ToLowerInvariant()}-certification-ledger.json"));
    var evidencePath = Path.GetFullPath(Read(values, "--export-evidence") ??
        Path.Combine(cacheRoot, "managed-loader-runtime-certification-evidence.json"));
    Directory.CreateDirectory(cacheRoot);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    var paths = new AppDataPaths(Path.Combine(cacheRoot, "catalog-data"),
        Path.Combine(cacheRoot, "catalog-servers"));
    paths.EnsureCreated();
    var catalog = new ManagedLoaderCatalogService(paths);
    await using var runtime = new ManagedLoaderRuntimeCertifier(cacheRoot,
        explicitJavaPaths: ReadJavaPaths(values));
    await runtime.InitializeAsync(cancellation.Token);
    var campaign = new ManagedLoaderCertificationCampaign(catalog, runtime);
    var options = new ManagedLoaderCertificationCampaignOptions
    {
        LedgerPath = ledgerPath,
        ExplicitEulaAuthorization = eula,
        ExactVersion = Read(values, "--version"),
        ExactLoaderVersion = Read(values, "--loader"),
        MaximumEntries = Has(values, "--all-stable")
            ? ReadInt(values, "--max-count", 10_000, 1, 10_000)
            : 1,
        Resume = !Has(values, "--no-resume"),
        RetryFailed = Has(values, "--retry-failed"),
        Force = Has(values, "--force"),
        RefreshCatalog = Has(values, "--refresh"),
        PerVersionTimeout = TimeSpan.FromSeconds(ReadInt(values, "--timeout-seconds", 900, 60, 1800))
    };
    ManagedLoaderCertificationLedger result;
    try
    {
        result = await campaign.RunAsync(platform, options,
            new Progress<string>(Console.WriteLine), cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Loader certification cancelled. The resumable ledger was preserved.");
        return 130;
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or
                                           InvalidOperationException)
    {
        Console.Error.WriteLine(SecretRedactor.Redact(exception.Message));
        return 69;
    }
    var groups = result.Entries.GroupBy(entry => entry.Result)
        .ToDictionary(group => group.Key.ToString(), group => group.Count());
    var summary = new
    {
        platform = platform.ToString(),
        result.CampaignId,
        result.StartedAt,
        result.UpdatedAt,
        result.ExplicitEulaAuthorization,
        result.LatestStableVersion,
        total = result.Entries.Count,
        results = groups,
        attemptedRuntime = result.Entries.Count(entry => entry.RuntimeLaunched),
        passed = result.Entries.Count(entry => entry.Result == VanillaCertificationResult.Passed),
        blocked = result.Entries.Count(entry => entry.Result.ToString().StartsWith("Blocked", StringComparison.Ordinal)),
        failed = result.Entries.Count(entry => entry.Result.ToString().StartsWith("Failed", StringComparison.Ordinal)),
        cancelled = result.Entries.Count(entry => entry.Result == VanillaCertificationResult.Cancelled),
        cleanupFailures = result.Entries.Count(entry => !entry.CleanupSucceeded),
        artifactCacheBytes = Directory.Exists(cacheRoot)
            ? Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}work{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Sum(path => new FileInfo(path).Length)
            : 0,
        ledger = ledgerPath
    };
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    await File.WriteAllTextAsync(reportPath,
        JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
        new System.Text.UTF8Encoding(false), cancellation.Token);
    var passedEvidence = ManagedLoaderCertificationCampaign.PassedEvidence(result);
    if (passedEvidence.Count > 0)
    {
        var existing = File.Exists(evidencePath)
            ? await File.ReadAllTextAsync(evidencePath, cancellation.Token)
            : null;
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        await File.WriteAllTextAsync(evidencePath,
            ManagedLoaderRuntimeCertificationEvidence.MergeAndExport(existing, passedEvidence),
            new System.Text.UTF8Encoding(false), cancellation.Token);
        Console.WriteLine($"Production certification evidence: {evidencePath}");
    }
    Console.WriteLine(JsonSerializer.Serialize(summary,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return result.Entries.Any(entry => entry.Result != VanillaCertificationResult.Passed) ? 2 : 0;
}

static async Task<(string Path, string Sha1)> CachePaperArtifactAsync(
    PaperBuildOption build,
    string cacheRoot,
    CancellationToken cancellationToken)
{
    var paperArtifacts = Path.Combine(cacheRoot, "paper-artifacts");
    Directory.CreateDirectory(paperArtifacts);
    var canonical = Path.Combine(paperArtifacts, build.ServerSha256.ToLowerInvariant() + ".jar");
    if (!await ValidPaperArtifactAsync(canonical, build, cancellationToken))
    {
        var partial = canonical + ".partial";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3.0 Paper certification");
            using var response = await http.GetAsync(build.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            if (!await ValidPaperArtifactAsync(partial, build, cancellationToken))
                throw new InvalidDataException("The downloaded Paper artifact did not match PaperMC's SHA-256 and size.");
            File.Move(partial, canonical, true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
    await using var sha1Stream = File.OpenRead(canonical);
#pragma warning disable CA5350 // VanillaRuntimeCertifier's hash-addressed cache uses SHA-1; Paper integrity remains SHA-256.
    var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(sha1Stream, cancellationToken)).ToLowerInvariant();
#pragma warning restore CA5350
    var runtimeArtifacts = Path.Combine(cacheRoot, "artifacts");
    Directory.CreateDirectory(runtimeArtifacts);
    var runtimePath = Path.Combine(runtimeArtifacts, sha1 + ".jar");
    if (!File.Exists(runtimePath)) File.Copy(canonical, runtimePath);
    return (runtimePath, sha1);
}

static async Task<bool> ValidPaperArtifactAsync(
    string path,
    PaperBuildOption build,
    CancellationToken cancellationToken)
{
    if (!File.Exists(path) || new FileInfo(path).Length != build.ServerSizeBytes)
        return false;
    await using var stream = File.OpenRead(path);
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    return hash.Equals(build.ServerSha256, StringComparison.OrdinalIgnoreCase);
}
