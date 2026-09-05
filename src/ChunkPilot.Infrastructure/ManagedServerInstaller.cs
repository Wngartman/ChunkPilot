using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record ServerDownload(
    string Url,
    string FileName,
    string MinecraftVersion,
    string Build,
    string Sha1,
    string Sha256,
    long? Size);

public sealed class ServerDownloadCatalog
{
    private const string UserAgent = "ChunkPilot/1.3.0 (local Windows Minecraft server manager)";
    private readonly HttpClient http;

    public ServerDownloadCatalog(HttpClient? httpClient = null)
    {
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(
        InstallSourceType sourceType,
        bool includeSnapshots = false,
        CancellationToken cancellationToken = default)
    {
        return sourceType switch
        {
            InstallSourceType.Vanilla => await GetVanillaVersionsAsync(includeSnapshots, cancellationToken).ConfigureAwait(false),
            InstallSourceType.Paper => await GetPaperVersionsAsync(cancellationToken).ConfigureAwait(false),
            InstallSourceType.Purpur => await GetPurpurVersionsAsync(cancellationToken).ConfigureAwait(false),
            InstallSourceType.Fabric or InstallSourceType.Quilt or InstallSourceType.Forge or
                InstallSourceType.NeoForge =>
                await GetVanillaVersionsAsync(includeSnapshots, cancellationToken).ConfigureAwait(false),
            _ => []
        };
    }

    public async Task<ServerDownload> ResolveAsync(
        InstallSourceType sourceType,
        string minecraftVersion,
        string build,
        CancellationToken cancellationToken = default)
    {
        return sourceType switch
        {
            InstallSourceType.Vanilla => await ResolveVanillaAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            InstallSourceType.Paper => await ResolvePaperAsync(minecraftVersion, build, cancellationToken).ConfigureAwait(false),
            InstallSourceType.Purpur => await ResolvePurpurAsync(minecraftVersion, build, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"{sourceType} does not use a built-in metadata catalog.")
        };
    }

    private async Task<IReadOnlyList<string>> GetVanillaVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("versions").EnumerateArray()
            .Where(item => includeSnapshots || item.GetProperty("type").GetString() == "release")
            .Select(item => item.GetProperty("id").GetString())
            .OfType<string>()
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> GetPaperVersionsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("https://fill.papermc.io/v3/projects/paper", cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("versions").EnumerateObject()
            .SelectMany(group => group.Value.EnumerateArray())
            .Select(item => item.GetString())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> GetPurpurVersionsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("https://api.purpurmc.org/v2/purpur", cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("versions").EnumerateArray()
            .Select(item => item.GetString())
            .OfType<string>()
            .Reverse()
            .ToArray();
    }

    private async Task<ServerDownload> ResolveVanillaAsync(string version, CancellationToken cancellationToken)
    {
        using var manifest = await GetJsonAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", cancellationToken).ConfigureAwait(false);
        var versionMetadataUrl = manifest.RootElement.GetProperty("versions").EnumerateArray()
            .Where(item => item.GetProperty("id").GetString()?.Equals(version, StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => item.GetProperty("url").GetString())
            .FirstOrDefault() ?? throw new InvalidOperationException($"Vanilla release {version} was not found.");
        using var metadata = await GetJsonAsync(versionMetadataUrl, cancellationToken).ConfigureAwait(false);
        var server = metadata.RootElement.GetProperty("downloads").GetProperty("server");
        return new ServerDownload(
            server.GetProperty("url").GetString() ?? throw new InvalidDataException("Vanilla server URL was missing."),
            $"minecraft_server.{version}.jar",
            version,
            "release",
            server.TryGetProperty("sha1", out var sha1) ? sha1.GetString() ?? "" : "",
            "",
            server.TryGetProperty("size", out var size) ? size.GetInt64() : null);
    }

    private async Task<ServerDownload> ResolvePaperAsync(
        string version,
        string requestedBuild,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds",
            cancellationToken).ConfigureAwait(false);
        var selected = document.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("channel", out var channel) &&
                           channel.GetString() is { } value &&
                           (value.Equals("STABLE", StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(requestedBuild) &&
                             (value.Equals("BETA", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("ALPHA", StringComparison.OrdinalIgnoreCase)))))
            .Where(item => string.IsNullOrWhiteSpace(requestedBuild) ||
                           (item.TryGetProperty("id", out var id) && id.ToString() == requestedBuild) ||
                           (item.TryGetProperty("number", out var number) && number.ToString() == requestedBuild))
            .FirstOrDefault();
        if (selected.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(requestedBuild)
                ? $"No stable Paper build is available for {version}."
                : $"PaperMC did not return that exact supported Paper build for {version}.");
        var build = selected.TryGetProperty("id", out var idValue) ? idValue.ToString() :
            selected.TryGetProperty("number", out var numberValue) ? numberValue.ToString() : "stable";
        var download = selected.GetProperty("downloads").GetProperty("server:default");
        var checksums = download.TryGetProperty("checksums", out var checksumObject) ? checksumObject : default;
        return new ServerDownload(
            download.GetProperty("url").GetString() ?? throw new InvalidDataException("Paper download URL was missing."),
            download.GetProperty("name").GetString() ?? $"paper-{version}-{build}.jar",
            version,
            build,
            "",
            checksums.ValueKind == JsonValueKind.Object && checksums.TryGetProperty("sha256", out var sha256)
                ? sha256.GetString() ?? "" : "",
            download.TryGetProperty("size", out var size) ? size.GetInt64() : null);
    }

    private async Task<ServerDownload> ResolvePurpurAsync(
        string version,
        string requestedBuild,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(version)}",
            cancellationToken).ConfigureAwait(false);
        var builds = document.RootElement.GetProperty("builds");
        var build = !string.IsNullOrWhiteSpace(requestedBuild)
            ? requestedBuild
            : builds.TryGetProperty("latest", out var latest) ? latest.ToString() : "";
        if (string.IsNullOrWhiteSpace(build))
            throw new InvalidDataException("Purpur did not provide a latest build.");
        var metadataUrl = $"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(build)}";
        using var metadata = await GetJsonAsync(metadataUrl, cancellationToken).ConfigureAwait(false);
        var root = metadata.RootElement;
        var md5 = root.TryGetProperty("md5", out var md5Value) ? md5Value.GetString() ?? "" : "";
        var sha256 = root.TryGetProperty("sha256", out var shaValue) ? shaValue.GetString() ?? "" : "";
        return new ServerDownload(
            $"{metadataUrl}/download",
            $"purpur-{version}-{build}.jar",
            version,
            build,
            "",
            sha256,
            null);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ManagedServerInstaller
{
    public const string EulaUrl = "https://www.minecraft.net/eula";
    private static readonly string[] DisallowedLaunchDirectories =
        ["mods", "plugins", "libraries", "versions"];
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;
    private readonly ServerDownloadCatalog catalog;
    private readonly LoaderInstallationService? loaderInstaller;
    private readonly ModrinthPackServerService packInstaller;
    private readonly ServerCreationTransaction transaction;
    private readonly CreationWorldSourceService worldSources;
    private readonly CurseForgeApiClient? curseForge;
    private readonly CurseForgePackService? curseForgePacks;
    private readonly IStagedServerValidator stagedValidator;
    private readonly HttpClient http;

    public ManagedServerInstaller(
        AppDataPaths paths,
        ChunkPilotStore store,
        ServerDownloadCatalog catalog,
        HttpClient? httpClient = null,
        LoaderInstallationService? loaderInstaller = null,
        ServerCreationTransaction? transaction = null,
        ModrinthPackServerService? packInstaller = null,
        CreationWorldSourceService? worldSources = null,
        CurseForgeApiClient? curseForge = null,
        CurseForgePackService? curseForgePacks = null,
        IStagedServerValidator? stagedValidator = null)
    {
        this.paths = paths;
        this.store = store;
        this.catalog = catalog;
        this.loaderInstaller = loaderInstaller;
        this.packInstaller = packInstaller ?? new ModrinthPackServerService(loaderInstaller: loaderInstaller);
        this.transaction = transaction ?? new ServerCreationTransaction(store);
        this.worldSources = worldSources ?? new CreationWorldSourceService();
        this.curseForge = curseForge;
        this.curseForgePacks = curseForgePacks ?? (curseForge is null
            ? null
            : new CurseForgePackService(curseForge, loaders: loaderInstaller));
        this.stagedValidator = stagedValidator ?? new StagedServerValidator();
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3.0 (local Windows server manager)");
    }

    public static string MakeSafeInstanceName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(name.Trim().Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        cleaned = string.Join("-", cleaned.Split([' ', '.', '-'], StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 64)
            cleaned = cleaned[..64].TrimEnd('-', '.');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
            throw new ArgumentException("The server name does not produce a safe instance folder.", nameof(name));
        return cleaned;
    }

    /// <summary>
    /// Creates one managed server as a journalled transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collecting the files is unchanged and still lives here. Everything after that - checking the
    /// destination, promoting the candidate, writing and verifying the server record, rolling back
    /// and cleaning up - belongs to <see cref="ServerCreationTransaction"/>, which journals each step
    /// durably so an interruption is recoverable rather than ambiguous.
    /// </para>
    /// <para>
    /// Registration is part of this call. It used to happen afterwards, in the Agent's coordinator,
    /// which left a window where the folder existed and no server record did and nothing recorded
    /// that fact.
    /// </para>
    /// </remarks>
    public async Task<InstallationResult> InstallAsync(
        ServerInstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var existingJournal = (await store.GetCreationJournalAsync(request.OperationId, cancellationToken))?.Entry;
        if (existingJournal is not null && !request.RetryVerifiedInput)
            throw new InvalidOperationException("This creation already has recovery evidence. Use its retry or discard action.");
        var instanceRoot = CreationPathSafety.Canonical(
            string.IsNullOrWhiteSpace(request.InstanceRoot) ? paths.ManagedServers : request.InstanceRoot);
        Directory.CreateDirectory(instanceRoot);
        var safeName = MakeSafeInstanceName(request.ServerName);
        var finalPath = Path.Combine(instanceRoot, safeName);
        var stagingPath = Path.Combine(instanceRoot, ServerCreationTransaction.StagingFolderName(request.OperationId));
        var logPath = Path.Combine(paths.Staging, $"{request.OperationId:N}.log");
        CreationPathSafety.EnsureWithin(instanceRoot, stagingPath);
        CreationPathSafety.EnsureWithin(instanceRoot, finalPath);

        CreationJournalEntry? retry = null;
        if (request.RetryVerifiedInput)
        {
            retry = existingJournal;
            if (retry is null || retry.Outcome != CreationOutcome.StagingResumable || retry.ActivationBegan ||
                retry.RegistrationBegan || retry.RetryGeneration != request.RetryGeneration || retry.CanonicalDestination != finalPath ||
                request.SourceType != InstallSourceType.CurseForgeServerPack || retry.RetrySettings is not { } settings ||
                settings.ProjectId != request.PackProjectId || settings.ClientFileId != request.PackVersionId ||
                settings.ServerFileId != request.PackServerFileId || settings.MinecraftVersion != request.MinecraftVersion ||
                settings.LoaderVersion != request.PackLoaderVersion)
                throw new InvalidDataException("The retained operation does not match this exact reviewed creation.");
            await new VerifiedCreationArchiveStore(paths).VerifyAsync(retry, cancellationToken);
            var cleanup = ServerCreationTransaction.CleanupOwnedTemporaries(retry);
            if (cleanup.Count != 0) throw new IOException("Temporary cleanup needs attention before retry. " + string.Join(" ", cleanup));
        }
        var serverId = retry?.ServerId ?? Guid.NewGuid();
        StagedPayload? staged = null;
        string launchPath = "";

        var result = await transaction.RunAsync(
            new CreationTransactionRequest
            {
                OperationId = request.OperationId,
                ServerId = serverId,
                ServerName = request.ServerName.Trim(),
                CreationKind = request.SourceType.ToString(),
                InstanceRoot = instanceRoot,
                Destination = finalPath,
                StagingPath = stagingPath,
                LogPath = logPath,
                EulaAcceptedAt = request.EulaAcceptedAt ?? default,
                EulaUrl = EulaUrl,
                VerifiedInput = retry?.VerifiedInput,
                RetryGeneration = request.RetryGeneration,
                RetrySettings = request.SourceType == InstallSourceType.CurseForgeServerPack ? new CreationRetrySettings
                {
                    ProjectId = request.PackProjectId, ClientFileId = request.PackVersionId, ServerFileId = request.PackServerFileId,
                    MinecraftVersion = request.MinecraftVersion, Loader = request.PackLoader, LoaderVersion = request.PackLoaderVersion,
                    RequiredJavaMajor = JavaRuntimePolicy.TryRequiredMajorForMinecraft(request.MinecraftVersion) ?? 0,
                    MinimumRamMb = request.MinimumRamMb, MaximumRamMb = request.MaximumRamMb, MaxPlayers = request.MaxPlayers,
                    Port = request.Port, NetworkingPreference = request.CreationNetworkingPreference, InitialWorld = request.InitialWorld
                } : null
            },
            async (context, token) =>
            {
                var runtimeJava = ResolveJava(request.JavaPath);
                var installerJava = string.IsNullOrWhiteSpace(request.InstallerJavaPath)
                    ? runtimeJava
                    : ResolveJava(request.InstallerJavaPath);
                var payload = await StagePayloadAsync(request, installerJava, context, progress, context.LogPath, token)
                    .ConfigureAwait(false);
                staged = payload;
                if (request.SourceType is InstallSourceType.CurseForgeServerPack or
                    InstallSourceType.CurseForgeGeneratedPack)
                    await ProviderOwnershipManifest.WriteAsync(context.StagingPath, "CurseForge", token)
                        .ConfigureAwait(false);
                if (request.InitialWorld is { } initialWorld)
                {
                    Report(progress, request.OperationId, InstallState.Extracting,
                        CreationStage.PreparingServerFiles, $"Copying existing world {initialWorld.WorldName}",
                        78, 0, initialWorld.ExpandedSizeBytes, 0, initialWorld.WorldName, context.LogPath);
                    var worldProgress = new CallbackProgress<CreationWorldCopyProgress>(update =>
                    {
                        var fraction = update.TotalBytes > 0
                            ? Math.Clamp((double)update.CopiedBytes / update.TotalBytes, 0, 1)
                            : update.TotalFiles > 0
                                ? Math.Clamp((double)update.CopiedFiles / update.TotalFiles, 0, 1)
                                : 0;
                        Report(progress, request.OperationId, InstallState.Extracting,
                            CreationStage.PreparingServerFiles,
                            $"Copying existing world ({update.CopiedFiles}/{update.TotalFiles} files)",
                            78 + fraction * 4,
                            update.CopiedBytes,
                            update.TotalBytes,
                            0,
                            update.CurrentFile,
                            context.LogPath);
                    });
                    await worldSources.MaterializeAsync(initialWorld, context.StagingPath, worldProgress, token)
                        .ConfigureAwait(false);
                }
                await WriteInitialPropertiesAsync(context.StagingPath, request, token).ConfigureAwait(false);
                if (payload.Ecosystem == ServerEcosystem.NeoForge &&
                    request.CreationNetworkingPreference == VanillaNetworkingPreference.ThisComputerOnly)
                {
                    var initial = ServerPropertiesDocument.Parse(await File.ReadAllTextAsync(
                        Path.Combine(context.StagingPath, "server.properties"), token).ConfigureAwait(false));
                    await NeoForgeLanAdvertisement.DisableForNewCreationAsync(context.StagingPath,
                        initial.Get("level-name") ?? "world", token).ConfigureAwait(false);
                    await AppendLogAsync(context.LogPath,
                        "This computer only: disabled NeoForge dedicated-server LAN advertisement; gameplay settings preserved.", token)
                        .ConfigureAwait(false);
                }
                if (!request.EulaAccepted || request.EulaAcceptedAt is null)
                    throw new InvalidOperationException(
                        "Minecraft EULA acceptance requires the unchecked wizard checkbox to be selected deliberately.");
                await File.WriteAllTextAsync(Path.Combine(context.StagingPath, "eula.txt"), "eula=true\r\n",
                    new UTF8Encoding(false), token).ConfigureAwait(false);

                launchPath = payload.UsesArgumentFile
                    ? Path.GetFullPath(Path.Combine(context.StagingPath, payload.FileName))
                    : FindLaunchJar(context.StagingPath, payload.FileName);
                var relativeLaunchPath = Path.GetRelativePath(context.StagingPath, launchPath);
                var launchArguments = payload.UsesArgumentFile
                    ? $"-Xms{request.MinimumRamMb}M -Xmx{request.MaximumRamMb}M " +
                      $"@{CommandLineQuoter.QuoteWindowsArgument(Path.Combine(context.DestinationPath, relativeLaunchPath))}"
                    : $"-Xms{request.MinimumRamMb}M -Xmx{request.MaximumRamMb}M -jar " +
                      CommandLineQuoter.QuoteWindowsArgument(Path.Combine(context.DestinationPath, relativeLaunchPath));
                var definition = new ServerDefinition
                {
                    Id = serverId,
                    Name = request.ServerName.Trim(),
                    RootPath = context.DestinationPath,
                    Executable = runtimeJava,
                    Arguments = ServerLaunchPolicy.EnsureNoGui(launchArguments, payload.Ecosystem),
                    WorkingDirectory = context.DestinationPath,
                    Ecosystem = payload.Ecosystem,
                    MinecraftVersion = payload.MinecraftVersion,
                    LoaderVersion = payload.Build,
                    Port = request.Port,
                    IsManaged = true,
                    ManagedInstanceRoot = instanceRoot,
                    RunInBackground = true,
                    MinimumRamMb = request.MinimumRamMb,
                    MaximumRamMb = request.MaximumRamMb,
                    CreationNetworkingPreference = request.CreationNetworkingPreference
                };
                await WritePackIdentityAsync(context.StagingPath, definition, request, payload, token)
                    .ConfigureAwait(false);
                if (request.SourceType is InstallSourceType.CurseForgeServerPack or
                    InstallSourceType.CurseForgeGeneratedPack)
                {
                    var validationProgress = new CallbackProgress<StagedValidationProgress>(update => progress?.Report(new InstallProgress
                    {
                        OperationId = request.OperationId, State = InstallState.Validating,
                        Stage = CreationStage.FinalSafetyCheck, Phase = CreationPhase.MaterializingCandidate,
                        CurrentStep = update.Stage, IsIndeterminate = true, StageElapsedSeconds = update.ElapsedSeconds,
                        LastMeaningfulStatus = update.LastMilestone, SecondsSinceMeaningfulUpdate = update.SecondsSinceMilestone,
                        RecentStatus = update.RecentStatus ?? "", NewLogOutputObserved = update.NewOutputObserved,
                        StagingLogPath = context.LogPath
                    }));
                    StagedServerValidationResult validation;
                    try
                    {
                        validation = await stagedValidator.ValidateAsync(runtimeJava, context.StagingPath,
                            relativeLaunchPath, payload.UsesArgumentFile,
                            request.MinimumRamMb, request.MaximumRamMb, TimeSpan.FromMinutes(10), validationProgress,
                            payload.Ecosystem, token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception failure) when (failure is StagedServerCleanupException or StagedServerCancelledException)
                    {
                        var evidence = failure is StagedServerCleanupException cleanup ? cleanup.Validation :
                            ((StagedServerCancelledException)failure).Validation;
                        if (evidence is not null)
                        {
                            try { await WriteValidationEvidenceAsync(context.StagingPath, context.LogPath, evidence,
                                CancellationToken.None).ConfigureAwait(false); }
                            catch (Exception evidenceFailure) when (evidenceFailure is IOException or UnauthorizedAccessException)
                            {
                                throw new StagedServerCleanupException("Validation evidence could not be saved; recovery is required.",
                                    new AggregateException(failure, evidenceFailure)) { Validation = evidence };
                            }
                        }
                        throw;
                    }
                    await AppendLogAsync(context.LogPath,
                        "[staged-validation] Local candidate validation completed; server output was not retained.",
                        token).ConfigureAwait(false);
                    await WriteValidationEvidenceAsync(context.StagingPath, context.LogPath, validation, token).ConfigureAwait(false);
                    if (!validation.Succeeded)
                        throw new InvalidDataException(
                            "Startup check could not finish. Your chosen server folder was not changed. " +
                            DescribeStagedValidationFailure(validation));
                }
                return new CreationCandidate(definition, payload.SourceUrl, payload.Sha256,
                    $"Source={request.SourceType}; Version={payload.MinecraftVersion}; Build={payload.Build}");
            },
            (staging, candidate) => ValidateStaging(staging, launchPath, staged!.UsesArgumentFile),
            progress,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var durableFailure = request.SourceType is InstallSourceType.CurseForgeServerPack or
                InstallSourceType.CurseForgeGeneratedPack
                ? CurseForgePersistencePolicy.LocalCreationFailureDetail
                : string.Join(" ", result.Warnings);
            await AppendLogAsync(logPath,
                $"{result.Phase}: {durableFailure}", CancellationToken.None).ConfigureAwait(false);
            if (result.Failure is not null)
                ExceptionDispatchInfo.Capture(result.Failure).Throw();
            throw new InvalidOperationException(CreationPhasePolicy.Describe(result.Outcome));
        }

        await AppendLogAsync(logPath, $"Completed: {result.Outcome}", CancellationToken.None).ConfigureAwait(false);
        return new InstallationResult
        {
            Definition = result.Definition!,
            SourceUrl = staged?.SourceUrl ?? "",
            Sha256 = staged?.Sha256 ?? "",
            StagingLogPath = logPath,
            Outcome = result.Outcome,
            Warnings = result.Warnings
        };
    }

    private static string DescribeStagedValidationFailure(StagedServerValidationResult validation)
    {
        const int maximumLines = 12;
        const int maximumLineCharacters = 512;
        const int maximumTailCharacters = 4_000;
        var lines = validation.Tail
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(maximumLines)
            .Select(line => SecretRedactor.Redact(line)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim())
            .Where(line => line.Length > 0)
            .Select(line => line[..Math.Min(line.Length, maximumLineCharacters)])
            .ToArray();
        if (lines.Length == 0)
            return validation.Summary;
        var tail = string.Join(" | ", lines);
        tail = tail[..Math.Min(tail.Length, maximumTailCharacters)];
        return validation.Summary + " Recent redacted output: " + tail;
    }

    private async Task<StagedPayload> StagePayloadAsync(
        ServerInstallRequest request,
        string javaPath,
        CreationMaterializationContext context,
        IProgress<InstallProgress>? progress,
        string logPath,
        CancellationToken cancellationToken)
    {
        var stagingPath = context.StagingPath;
        if (request.SourceType is InstallSourceType.Fabric or InstallSourceType.Quilt or
            InstallSourceType.Forge or InstallSourceType.NeoForge)
        {
            var service = loaderInstaller ??
                          new LoaderInstallationService(new LoaderMetadataService());
            LoaderInstallResult installed;
            if (!string.IsNullOrWhiteSpace(request.Source))
            {
                installed = await service.InstallExactAsync(new LoaderInstallPlan
                {
                    Loader = request.SourceType,
                    MinecraftVersion = request.MinecraftVersion,
                    LoaderVersion = request.Build,
                    InstallerVersion = request.InstallerVersion,
                    DownloadUrl = request.Source,
                    Sha1 = request.ExpectedSha1,
                    Sha256 = request.ExpectedSha256,
                    InstallerArgument = request.SourceType switch
                    {
                        InstallSourceType.Quilt =>
                            $"install server {request.MinecraftVersion} {request.Build} --download-server --install-dir=.",
                        InstallSourceType.Forge or InstallSourceType.NeoForge => "--installServer",
                        _ => ""
                    },
                    ExpectedLaunchFile = request.SourceType switch
                    {
                        InstallSourceType.Fabric => "fabric-server-launch.jar",
                        InstallSourceType.Quilt => "quilt-server-launch.jar",
                        _ => "run.bat"
                    },
                    RequiredJavaMajor = JavaRuntimePolicy.RequiredMajorForMinecraft(request.MinecraftVersion),
                    RunsInstaller = request.SourceType is InstallSourceType.Quilt or InstallSourceType.Forge or
                        InstallSourceType.NeoForge
                }, javaPath, stagingPath, logPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                installed = await service.InstallAsync(
                    request.SourceType,
                    request.MinecraftVersion,
                    request.Build,
                    javaPath,
                    stagingPath,
                    logPath,
                    cancellationToken).ConfigureAwait(false);
            }
            var loaderTarget = string.IsNullOrWhiteSpace(installed.ArgumentsFile)
                ? installed.LaunchFile : installed.ArgumentsFile;
            return new StagedPayload(
                Path.GetRelativePath(stagingPath, loaderTarget),
                request.MinecraftVersion,
                request.Build,
                installed.ArtifactUrl.Length > 0 ? installed.ArtifactUrl : $"official:{request.SourceType}",
                installed.DownloadSha256,
                !string.IsNullOrWhiteSpace(installed.ArgumentsFile),
                ToEcosystem(request.SourceType),
                request.InstallerVersion);
        }

        if (request.SourceType is InstallSourceType.Vanilla or InstallSourceType.Paper or InstallSourceType.Purpur)
        {
            var resolved = await catalog.ResolveAsync(request.SourceType, request.MinecraftVersion, request.Build, cancellationToken)
                .ConfigureAwait(false);
            var destination = Path.Combine(stagingPath, "server.jar");
            var hash = await DownloadAsync(request.OperationId, resolved.Url, destination, resolved.Size, progress, logPath,
                cancellationToken).ConfigureAwait(false);
            Report(progress, request.OperationId, InstallState.Validating, CreationStage.VerifyingServerDownload,
                "Verifying the download", 52, 0, null, 0, Path.GetFileName(destination), logPath);
            VerifyHash(destination, resolved.Sha1, resolved.Sha256);
            // When the caller reviewed a specific artifact hash, it must still match. This catches an
            // artifact that changed between the review screen and the download rather than installing
            // whatever the provider now happens to serve.
            VerifyHash(destination, request.ExpectedSha1, request.ExpectedSha256, request.ExpectedSha512);
            return new StagedPayload("server.jar", resolved.MinecraftVersion, resolved.Build, resolved.Url, hash, false,
                ToEcosystem(request.SourceType), request.InstallerVersion);
        }

        if (request.SourceType == InstallSourceType.ModrinthPack)
        {
            var archivePath = request.Source;
            var removeArchive = false;
            try
            {
                if (Uri.TryCreate(request.Source, UriKind.Absolute, out var sourceUri))
                {
                    if (!sourceUri.IdnHost.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Remote Modrinth packs must use the trusted Modrinth CDN.");
                    archivePath = Path.Combine(paths.Staging, $"{request.OperationId:N}.mrpack");
                    removeArchive = true;
                    await DownloadTrustedPackArchiveAsync(sourceUri, archivePath, request, progress, logPath,
                        cancellationToken).ConfigureAwait(false);
                }
                if (!Path.GetExtension(archivePath).Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A Modrinth server pack must use the .mrpack format.");
                if (request.ExpectedSizeBytes is > 0 &&
                    new FileInfo(archivePath).Length != request.ExpectedSizeBytes.Value)
                    throw new InvalidDataException("The selected Modrinth release size no longer matches the reviewed artifact.");
                VerifyHash(archivePath, request.ExpectedSha1, request.ExpectedSha256, request.ExpectedSha512);
                Report(progress, request.OperationId, InstallState.Extracting, CreationStage.PreparingServerFiles,
                    "Building the verified server pack", 52, 0, null, 0, Path.GetFileName(archivePath), logPath);
                var packProgress = new CallbackProgress<ModrinthMaterializationProgress>(update =>
                {
                    var fraction = update.TotalBytes > 0
                        ? Math.Clamp((double)update.CompletedBytes / update.TotalBytes, 0, 1)
                        : update.TotalFiles > 0
                            ? Math.Clamp((double)update.CompletedFiles / update.TotalFiles, 0, 1)
                            : 0;
                    Report(progress, request.OperationId, InstallState.Extracting,
                        CreationStage.PreparingServerFiles,
                        $"Building the verified server pack ({update.CompletedFiles}/{update.TotalFiles} files)",
                        52 + fraction * 20,
                        update.CompletedBytes,
                        update.TotalBytes > 0 ? update.TotalBytes : null,
                        0,
                        string.IsNullOrWhiteSpace(update.CurrentFile)
                            ? Path.GetFileName(archivePath)
                            : update.CurrentFile,
                        logPath);
                });
                var installed = await packInstaller.MaterializeAndInstallAsync(
                    archivePath, stagingPath, javaPath, logPath, packProgress, cancellationToken)
                    .ConfigureAwait(false);
                if (!installed.Manifest.Dependencies["minecraft"].Equals(
                        request.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"The selected release declared Minecraft {request.MinecraftVersion}, but its pack manifest requires {installed.Manifest.Dependencies["minecraft"]}.");
                Report(progress, request.OperationId, InstallState.Validating, CreationStage.VerifyingServerDownload,
                    "Verifying the exact loader and server files", 76, 0, null, 0,
                    $"{installed.Loader} {installed.LoaderVersion}", logPath);
                return new StagedPayload(
                    installed.LaunchRelativePath,
                    installed.Manifest.Dependencies["minecraft"],
                    installed.LoaderVersion,
                    request.Source,
                    string.IsNullOrWhiteSpace(request.ExpectedSha256)
                        ? Sha256(archivePath)
                        : request.ExpectedSha256,
                    installed.UsesArgumentFile,
                    installed.Ecosystem,
                    installed.InstallerVersion);
            }
            finally
            {
                if (removeArchive && File.Exists(archivePath))
                    File.Delete(archivePath);
            }
        }

        if (request.SourceType == InstallSourceType.CurseForgeServerPack)
        {
            if (curseForge is null)
                throw new InvalidOperationException("The native CurseForge download boundary is unavailable.");
            if (!Uri.TryCreate(request.Source, UriKind.Absolute, out var sourceUri) ||
                !CurseForgeApiClient.IsApprovedDownloadUri(sourceUri))
                throw new InvalidDataException("The official server pack does not use an approved CurseForge CDN host.");
            var inputs = new VerifiedCreationArchiveStore(paths);
            var owner = CreationOwnershipMarker.TryRead(stagingPath) ??
                throw new InvalidDataException("Creation staging has no ownership marker.");
            var archivePath = inputs.ArchiveFor(request.OperationId);
            var finalized = context.VerifiedInput is not null;
            try
            {
                if (!finalized)
                {
                    var partial = await inputs.PrepareAsync(owner, request.ExpectedSizeBytes ?? 0, cancellationToken);
                    await DownloadCurseForgeArchiveAsync(sourceUri, partial, request, progress, logPath,
                        "Downloading the exact official server pack", cancellationToken);
                    VerifyHash(partial, request.ExpectedSha1, request.ExpectedSha256, request.ExpectedSha512);
                    var proof = await inputs.FinalizeAsync(owner, request, cancellationToken);
                    finalized = true;
                    if (context.RecordVerifiedInputAsync is null)
                        throw new InvalidOperationException("The creation journal cannot record verified input.");
                    await context.RecordVerifiedInputAsync(proof, cancellationToken);
                }
                else
                {
                    // InstallAsync rehashed the local ownership proof; compare fresh provider digests too.
                    VerifyHash(archivePath, request.ExpectedSha1, request.ExpectedSha256, request.ExpectedSha512);
                    Report(progress, request.OperationId, InstallState.Validating, CreationStage.VerifyingServerDownload,
                        "Reusing the verified official server pack; no archive download", 50, 0, null, 0, "", logPath);
                }
                Report(progress, request.OperationId, InstallState.Extracting, CreationStage.PreparingServerFiles,
                    "Inspecting and extracting the verified official server pack", 55, 0, null, 0,
                    Path.GetFileName(archivePath), logPath);
                await ExtractZipSafeAsync(archivePath, stagingPath, cancellationToken).ConfigureAwait(false);
                NormalizeSinglePackageRoot(stagingPath);
                var launch = FindKnownServerPackLaunch(stagingPath);
                var installedLoaderVersion = request.InstallerVersion;
                if (launch is null)
                {
                    var loader = ToManagedLoaderSourceType(request.PackLoader);
                    var exactLoaderVersion = string.IsNullOrWhiteSpace(request.PackLoaderVersion)
                        ? request.Build
                        : request.PackLoaderVersion;
                    Report(progress, request.OperationId, InstallState.Extracting,
                        CreationStage.PreparingServerFiles,
                        $"Materializing the exact managed {loader} loader without executing pack scripts",
                        70, 0, null, 0, exactLoaderVersion, logPath);
                    var service = loaderInstaller ??
                                  new LoaderInstallationService(new LoaderMetadataService());
                    var installed = await service.InstallAsync(
                        loader, request.MinecraftVersion, exactLoaderVersion, javaPath,
                        stagingPath, logPath, cancellationToken).ConfigureAwait(false);
                    var target = string.IsNullOrWhiteSpace(installed.ArgumentsFile)
                        ? installed.LaunchFile
                        : installed.ArgumentsFile;
                    launch = (target, !string.IsNullOrWhiteSpace(installed.ArgumentsFile));
                    installedLoaderVersion = installed.InstallerVersion;
                }
                return new StagedPayload(
                    Path.GetRelativePath(stagingPath, launch.Value.Path), request.MinecraftVersion,
                    string.IsNullOrWhiteSpace(request.PackLoaderVersion) ? request.Build : request.PackLoaderVersion,
                    request.Source, Sha256(archivePath), launch.Value.UsesArgumentFile,
                    ToEcosystemFromLoader(request.PackLoader), installedLoaderVersion);
            }
            finally
            {
                // Successful immutable input survives a later loader/startup failure. Partial input
                // has no reusable proof and is removed only through the exact operation marker.
                if (!finalized && Directory.Exists(inputs.DirectoryFor(request.OperationId)))
                    inputs.Discard(new CreationJournalEntry
                    {
                        OperationId = owner.OperationId, ServerId = owner.ServerId,
                        CanonicalDestination = owner.CanonicalDestination
                    });
            }
        }

        if (request.SourceType == InstallSourceType.CurseForgeGeneratedPack)
        {
            if (curseForge is null || curseForgePacks is null)
                throw new InvalidOperationException("The native CurseForge generated-pack boundary is unavailable.");
            var archivePath = request.Source;
            var removeArchive = false;
            try
            {
                if (Uri.TryCreate(request.Source, UriKind.Absolute, out var sourceUri))
                {
                    if (!CurseForgeApiClient.IsApprovedDownloadUri(sourceUri))
                        throw new InvalidDataException("The CurseForge client pack does not use an approved CDN host.");
                    archivePath = Path.Combine(paths.Staging, $"{request.OperationId:N}-curseforge-client.zip");
                    removeArchive = true;
                    await DownloadCurseForgeArchiveAsync(sourceUri, archivePath, request, progress, logPath,
                        "Downloading the exact CurseForge client manifest pack", cancellationToken).ConfigureAwait(false);
                }
                else if (!File.Exists(archivePath))
                {
                    throw new FileNotFoundException("The selected local CurseForge manifest archive was not found.", archivePath);
                }
                if (request.ExpectedSizeBytes is not > 0 ||
                    new FileInfo(archivePath).Length != request.ExpectedSizeBytes.Value)
                    throw new InvalidDataException("The CurseForge client archive size no longer matches the reviewed artifact.");
                VerifyHash(archivePath, request.ExpectedSha1, request.ExpectedSha256, request.ExpectedSha512);
                Report(progress, request.OperationId, InstallState.Extracting, CreationStage.PreparingServerFiles,
                    "Materializing the exact reviewed CurseForge files and managed loader", 52, 0, null, 0,
                    Path.GetFileName(archivePath), logPath);
                CurseForgePackLaunchResult installed;
                if (request.CurseForgeGeneratedPlan is { } reviewedPlan)
                {
                    installed = await curseForgePacks.MaterializeAndInstallAsync(
                        archivePath, stagingPath, javaPath, logPath, reviewedPlan, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    if (Uri.TryCreate(request.Source, UriKind.Absolute, out _))
                        throw new InvalidDataException(
                            "Provider-backed generated CurseForge creation requires its exact authorized file plan.");
                    installed = await curseForgePacks.MaterializeAndInstallAsync(
                        archivePath, stagingPath, javaPath, logPath, cancellationToken).ConfigureAwait(false);
                }
                if (!installed.Manifest.MinecraftVersion.Equals(request.MinecraftVersion, StringComparison.OrdinalIgnoreCase) ||
                    !installed.Manifest.Loader.ToString().Equals(request.PackLoader, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The CurseForge manifest identity changed after the creation review.");
                if (!string.IsNullOrWhiteSpace(request.PackLoaderVersion) &&
                    !installed.LoaderVersion.Equals(request.PackLoaderVersion, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The CurseForge loader version changed after the creation review.");
                Report(progress, request.OperationId, InstallState.Validating, CreationStage.VerifyingServerDownload,
                    "Verifying generated server candidate identity", 76, 0, null, 0,
                    $"{installed.Manifest.Loader} {installed.LoaderVersion}", logPath);
                return new StagedPayload(installed.LaunchRelativePath, installed.Manifest.MinecraftVersion,
                    installed.LoaderVersion, request.Source, Sha256(archivePath), installed.UsesArgumentFile,
                    installed.Ecosystem, installed.InstallerVersion);
            }
            finally
            {
                if (removeArchive && File.Exists(archivePath)) File.Delete(archivePath);
            }
        }

        if (request.SourceType == InstallSourceType.ExistingPackageFolder)
        {
            var source = Path.GetFullPath(request.Source);
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException(source);
            await CopyDirectoryAsync(source, stagingPath, cancellationToken).ConfigureAwait(false);
            var jar = FindLaunchJar(stagingPath, request.LaunchRelativePath);
            return new StagedPayload(Path.GetRelativePath(stagingPath, jar), request.MinecraftVersion, request.Build,
                source, Sha256(jar), false, ServerEcosystem.Custom, request.InstallerVersion);
        }

        var localSource = request.Source;
        var removeLocalSource = false;
        try
        {
            if (request.SourceType == InstallSourceType.DirectUrl)
            {
                var uri = ValidateDownloadUri(request.Source, request.AllowHttp);
                var fileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "server-package";
                localSource = Path.Combine(paths.Staging, $"{request.OperationId:N}-{MakeSafeDownloadName(fileName)}");
                removeLocalSource = true;
                _ = await DownloadAsync(request.OperationId, uri.ToString(), localSource, null, progress, logPath, cancellationToken)
                    .ConfigureAwait(false);
                VerifyHash(localSource, request.ExpectedSha1, request.ExpectedSha256, request.ExpectedSha512);
            }

            if (!File.Exists(localSource))
                throw new FileNotFoundException("The selected local server package was not found.", localSource);
            if (request.ExpectedSizeBytes is > 0 && new FileInfo(localSource).Length != request.ExpectedSizeBytes.Value)
                throw new InvalidDataException("The selected local server package size changed after review.");
            VerifyHash(localSource, request.ExpectedSha1, request.ExpectedSha256, request.ExpectedSha512);
            var extension = Path.GetExtension(localSource);
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Report(progress, request.OperationId, InstallState.Extracting, CreationStage.PreparingServerFiles,
                    "Extracting reviewed ZIP package", 55, 0, null, 0, localSource, logPath);
                await ExtractZipSafeAsync(localSource, stagingPath, cancellationToken).ConfigureAwait(false);
                var jar = FindLaunchJar(stagingPath, request.LaunchRelativePath);
                return new StagedPayload(Path.GetRelativePath(stagingPath, jar), request.MinecraftVersion, request.Build,
                    "user-supplied:" + Sha256(localSource), Sha256(localSource), false,
                    request.SourceType == InstallSourceType.LocalServerJar ? ServerEcosystem.Vanilla : ServerEcosystem.Custom,
                    request.InstallerVersion);
            }
            if (!extension.Equals(".jar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Managed local packages must be a ZIP or server JAR.");
            var target = Path.Combine(stagingPath, "server.jar");
            File.Copy(localSource, target, overwrite: false);
            return new StagedPayload("server.jar", request.MinecraftVersion, request.Build,
                request.SourceType == InstallSourceType.LocalServerJar
                    ? "user-supplied:" + request.ExpectedSha256
                    : request.Source,
                Sha256(target), false,
                request.SourceType == InstallSourceType.LocalServerJar ? ServerEcosystem.Vanilla : ServerEcosystem.Custom,
                request.InstallerVersion);
        }
        finally
        {
            if (removeLocalSource && File.Exists(localSource))
                File.Delete(localSource);
        }
    }

    public static async Task ExtractZipSafeAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
        => await ServerImportInspectionService.ExtractAsync(archivePath, destinationPath, cancellationToken)
            .ConfigureAwait(false);

    public static void VerifyHash(
        string filePath,
        string expectedSha1,
        string expectedSha256,
        string expectedSha512 = "")
    {
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = Sha256(filePath);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 mismatch for {Path.GetFileName(filePath)}. Expected {expectedSha256}; received {actual}.");
        }
        if (!string.IsNullOrWhiteSpace(expectedSha1))
        {
            using var stream = File.OpenRead(filePath);
#pragma warning disable CA5350 // Mojang's signed version manifest publishes SHA-1 for vanilla server jars.
            var actual = Convert.ToHexString(SHA1.HashData(stream));
#pragma warning restore CA5350
            if (!actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-1 mismatch for {Path.GetFileName(filePath)}.");
        }
        if (!string.IsNullOrWhiteSpace(expectedSha512))
        {
            using var stream = File.OpenRead(filePath);
            var actual = Convert.ToHexString(SHA512.HashData(stream));
            if (!actual.Equals(expectedSha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-512 mismatch for {Path.GetFileName(filePath)}.");
        }
    }

    private async Task<string> DownloadAsync(
        Guid operationId,
        string url,
        string destination,
        long? knownSize,
        IProgress<InstallProgress>? progress,
        string logPath,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? knownSize;
        await AppendLogAsync(logPath, $"Downloading {new Uri(url).Host}/{Path.GetFileName(new Uri(url).LocalPath)} ({total?.ToString() ?? "unknown size"} bytes).",
            cancellationToken).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            incremental.AppendData(buffer, 0, count);
            downloaded += count;
            var fraction = total > 0 ? (double)downloaded / total.Value : 0;
            Report(progress, operationId, InstallState.Downloading, CreationStage.DownloadingServer,
                "Downloading the server", 10 + fraction * 40, downloaded, total,
                downloaded / Math.Max(.1, timer.Elapsed.TotalSeconds),
                Path.GetFileName(destination), logPath);
        }
        return Convert.ToHexString(incremental.GetHashAndReset());
    }

    private static void ValidateRequest(ServerInstallRequest request)
    {
        if (!request.EulaAccepted || request.EulaAcceptedAt is null)
            throw new InvalidOperationException("Check “I have read and agree to the Minecraft EULA” before installing.");
        if (request.Port is < 1 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(request), "Server port must be from 1 through 65535.");
        if (request.MinimumRamMb < 512 || request.MaximumRamMb < request.MinimumRamMb)
            throw new ArgumentException("RAM values are invalid.");
        _ = MakeSafeInstanceName(request.ServerName);
        if (request.SourceType == InstallSourceType.DirectUrl)
            _ = ValidateDownloadUri(request.Source, request.AllowHttp);
        if (request.SourceType == InstallSourceType.ModrinthPack &&
            !Uri.TryCreate(request.Source, UriKind.Absolute, out _) && !File.Exists(request.Source))
            throw new FileNotFoundException("The selected local Modrinth pack was not found.", request.Source);
        if (request.SourceType == InstallSourceType.CurseForgeServerPack &&
            (!Uri.TryCreate(request.Source, UriKind.Absolute, out var curseForgeSource) ||
             !CurseForgeApiClient.IsApprovedDownloadUri(curseForgeSource)))
            throw new InvalidDataException("The selected CurseForge server-pack URL is not approved.");
        if (request.SourceType == InstallSourceType.CurseForgeGeneratedPack &&
            !File.Exists(request.Source) &&
            (!Uri.TryCreate(request.Source, UriKind.Absolute, out var curseForgeClient) ||
             !CurseForgeApiClient.IsApprovedDownloadUri(curseForgeClient)))
            throw new InvalidDataException("The selected CurseForge manifest archive is neither a reviewed local file nor an approved provider download.");
        if (request.InitialWorld is { } world)
        {
            var problems = world.Problems();
            if (problems.Count > 0)
                throw new InvalidDataException(string.Join(" ", problems));
        }
    }

    private static Uri ValidateDownloadUri(string source, bool allowHttp)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && (!allowHttp || uri.Scheme != Uri.UriSchemeHttp)))
            throw new ArgumentException("Use an HTTPS download URL. HTTP requires the explicit advanced warning option.", nameof(source));
        return uri;
    }

    private static async Task WriteInitialPropertiesAsync(
        string stagingPath,
        ServerInstallRequest request,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stagingPath, "server.properties");
        var document = File.Exists(path)
            ? ServerPropertiesDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
            : ServerPropertiesDocument.Parse(
                "# Created by ChunkPilot after explicit EULA acceptance\r\n" +
                "gamemode=survival\r\ndifficulty=normal\r\nonline-mode=true\r\nwhite-list=true\r\n");
        document.Set("server-port", request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        document.Set("max-players", request.MaxPlayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        document.Set("white-list", "true");
        foreach (var property in request.InitialProperties)
            document.Set(property.Key, property.Value);
        if (request.InitialWorld is { } initialWorld)
            document.Set("level-name", initialWorld.WorldName);
        if (request.CreationNetworkingPreference == VanillaNetworkingPreference.ThisComputerOnly)
            document.Set("server-ip", "127.0.0.1");
        else
            document.Set("server-ip", "");
        if (IsModpackCreation(request.SourceType))
        {
            document.Set("enable-query", "false");
            document.Set("enable-rcon", "false");
            document.Set("broadcast-rcon-to-ops", "false");
        }
        await File.WriteAllTextAsync(path, document.ToString(), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsModpackCreation(InstallSourceType sourceType) =>
        sourceType is InstallSourceType.ModrinthPack or InstallSourceType.CurseForgeServerPack or
            InstallSourceType.CurseForgeGeneratedPack;

    private static async Task WritePackIdentityAsync(
        string stagingPath,
        ServerDefinition definition,
        ServerInstallRequest request,
        StagedPayload payload,
        CancellationToken cancellationToken)
    {
        if (request.SourceType is not (InstallSourceType.ModrinthPack or InstallSourceType.CurseForgeServerPack or
                InstallSourceType.CurseForgeGeneratedPack) ||
            request.PackProvider == UpdateProvider.None)
            return;
        var metadataRoot = Path.Combine(stagingPath, ".chunkpilot");
        Directory.CreateDirectory(metadataRoot);
        var source = new UpdateSource
        {
            ServerId = definition.Id,
            Provider = request.PackProvider,
            ProjectName = request.PackProjectName,
            ProjectId = request.PackProjectId,
            ProjectSlug = request.PackProjectSlug,
            InstalledVersionId = request.PackVersionId,
            InstalledVersionName = request.PackVersionName,
            InstalledFileId = string.IsNullOrWhiteSpace(request.PackServerFileId)
                ? string.IsNullOrWhiteSpace(request.PackVersionId) ? payload.Sha256 : request.PackVersionId
                : request.PackServerFileId,
            IdentityOrigin = CurseForgePersistencePolicy.IdentityOriginFor(request),
            MinecraftVersion = payload.MinecraftVersion,
            Loader = payload.Ecosystem.ToString(),
            LoaderVersion = payload.Build,
            InstallerVersion = payload.InstallerVersion,
            ReleaseChannel = request.PackReleaseChannel,
            SourceUrl = request.Source,
            InstalledAt = DateTimeOffset.UtcNow,
            IsUserLinked = request.PackProvider is UpdateProvider.Modrinth or UpdateProvider.CurseForge,
            DetectionEvidence = request.PackProvider == UpdateProvider.CurseForge
                ? string.IsNullOrWhiteSpace(request.PackProjectId)
                    ? "Recorded from an exact local CurseForge manifest archive; every materialized project/file was verified natively, but the pack project/release itself is not linked."
                    : string.IsNullOrWhiteSpace(request.PackServerFileId)
                        ? $"Recorded from exact reviewed CurseForge project slug {request.PackProjectSlug} and client file {request.PackVersionId}; manifest files, provider SHA-1 values, and local SHA-256 baselines were verified."
                        : $"Recorded from exact reviewed CurseForge project slug {request.PackProjectSlug}, client file {request.PackVersionId}, and official server-pack file {request.PackServerFileId}; provider SHA-1 and local SHA-256 were verified."
                : request.PackProvider == UpdateProvider.Modrinth
                ? "Recorded from the exact reviewed Modrinth project and release; archive and all indexed files were verified."
                : "Recorded from a locally selected .mrpack. Provider updates require a separately proven project identity."
        };
        source = CurseForgePersistencePolicy.Minimize(source);
        await File.WriteAllTextAsync(Path.Combine(metadataRoot, "update-source.json"),
            JsonSerializer.Serialize(source, ProtocolJson.Options), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DownloadTrustedPackArchiveAsync(
        Uri uri,
        string destination,
        ServerInstallRequest request,
        IProgress<InstallProgress>? progress,
        string logPath,
        CancellationToken cancellationToken)
    {
        using var source = new ModrinthPackHttpDownloadSource();
        await using var input = await source.OpenReadAsync(uri, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long transferred = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            transferred = checked(transferred + count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            Report(progress, request.OperationId, InstallState.Downloading, CreationStage.DownloadingServer,
                "Downloading the exact Modrinth release", 12, transferred, request.ExpectedSizeBytes, 0,
                Path.GetFileName(destination), logPath);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FindLaunchJar(string stagingPath, string preferredRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(preferredRelativePath))
        {
            var preferred = Path.GetFullPath(Path.Combine(stagingPath, preferredRelativePath));
            EnsureChildPath(stagingPath, preferred);
            if (!File.Exists(preferred) ||
                !Path.GetExtension(preferred).Equals(".jar", StringComparison.OrdinalIgnoreCase) ||
                HasDisallowedLaunchDirectory(stagingPath, preferred))
                throw new InvalidDataException(
                    "The exact reviewed server launcher is missing or is not an eligible server JAR.");
            return preferred;
        }
        var candidates = Directory.EnumerateFiles(stagingPath, "*.jar", SearchOption.AllDirectories)
            .Where(path => !HasDisallowedLaunchDirectory(stagingPath, path) && IsKnownServerLaunchJar(path))
            .OrderBy(path => Path.GetRelativePath(stagingPath, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var exactServer = candidates.Where(path =>
                Path.GetFileName(path).Equals("server.jar", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactServer.Length == 1)
            return exactServer[0];
        if (exactServer.Length > 1 || candidates.Length > 1)
            throw new InvalidDataException(
                "The staged package contains multiple ambiguous server launch JARs; select one exact reviewed launcher.");
        return candidates.SingleOrDefault() ??
               throw new InvalidDataException(
                   "No eligible server launcher was found; content, dependency, client, and installer JARs are never executed as the server.");
    }

    private static async Task WriteValidationEvidenceAsync(
        string stagingPath,
        string logPath,
        StagedServerValidationResult validation,
        CancellationToken cancellationToken)
    {
        var metadataRoot = Path.Combine(stagingPath, ".chunkpilot");
        Directory.CreateDirectory(metadataRoot);
        var evidence = new
        {
            schemaVersion = 2,
            validatedAtUtc = DateTimeOffset.UtcNow,
            validation.Succeeded,
            validation.ReadinessConfirmed,
            validation.LoopbackStatusConfirmed,
            validation.CleanStopConfirmed,
            validation.NoUnexpectedGuiConfirmed,
            validation.Summary,
            validation.AttemptId,
            validation.Network,
            validation.EndpointHistory,
            validation.JobEmptyConfirmed,
            validation.SelectedPortListenerAbsent,
            validation.ValidationPort,
            validation.ConfigurationRestored,
            validation.ValidationWorldRemoved,
            validation.ElapsedSeconds
        };
        // Independent of mutable staging, so a failed creation's cleanup cannot erase its decision.
        CreationStagingSafety.EnsureNoReparseTraversal(Path.GetDirectoryName(logPath)!);
        var durableEvidence = logPath + ".validation-" + validation.AttemptId.ToString("N") + ".json";
        await using (var stream = new FileStream(durableEvidence, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 16 * 1024, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, evidence, ProtocolJson.Options, cancellationToken)
                .ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        await File.WriteAllTextAsync(Path.Combine(metadataRoot, "staged-validation.json"),
            JsonSerializer.Serialize(evidence, ProtocolJson.Options), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DownloadCurseForgeArchiveAsync(
        Uri uri,
        string destination,
        ServerInstallRequest request,
        IProgress<InstallProgress>? progress,
        string logPath,
        string message,
        CancellationToken cancellationToken)
    {
        if (curseForge is null) throw new InvalidOperationException("The native CurseForge download boundary is unavailable.");
        if (request.ExpectedSizeBytes is not > 0 or > ServerImportInspectionService.MaximumCompressedBytes)
            throw new InvalidDataException("The CurseForge archive does not have a safe declared size.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var response = await curseForge.SendDownloadAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != request.ExpectedSizeBytes.Value)
            throw new InvalidDataException("The CurseForge archive response size changed after review.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long transferred = 0;
        var transferWatch = System.Diagnostics.Stopwatch.StartNew();
        var lastTransferReport = TimeSpan.Zero;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            transferred = checked(transferred + read);
            if (transferred > request.ExpectedSizeBytes.Value)
                throw new InvalidDataException("The CurseForge archive exceeded its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (transferWatch.Elapsed - lastTransferReport >= TimeSpan.FromMilliseconds(200) || transferred == request.ExpectedSizeBytes)
            {
                Report(progress, request.OperationId, InstallState.Downloading, CreationStage.DownloadingServer,
                    message, 10 + 40d * transferred / request.ExpectedSizeBytes.Value, transferred, request.ExpectedSizeBytes,
                    transferred / Math.Max(transferWatch.Elapsed.TotalSeconds, 0.001), "", logPath);
                lastTransferReport = transferWatch.Elapsed;
            }
        }
        if (transferred != request.ExpectedSizeBytes.Value)
            throw new InvalidDataException("The CurseForge archive ended before its declared size.");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    internal static (string Path, bool UsesArgumentFile)? FindKnownServerPackLaunch(string stagingPath)
    {
        var candidates = Directory.EnumerateFiles(stagingPath, "*.jar", SearchOption.AllDirectories)
            .Where(path => !HasDisallowedLaunchDirectory(stagingPath, path) && IsKnownServerLaunchJar(path))
            .OrderBy(path => Path.GetRelativePath(stagingPath, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var exactServer = candidates.Where(path =>
                Path.GetFileName(path).Equals("server.jar", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactServer.Length == 1)
            return (exactServer[0], false);
        if (exactServer.Length > 1 || candidates.Length > 1)
            throw new InvalidDataException(
                "The official server pack contains multiple ambiguous native server launchers.");
        return candidates.Length == 1 ? (candidates[0], false) : null;
    }

    private static bool IsKnownServerLaunchJar(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("client", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("serverstarter", StringComparison.OrdinalIgnoreCase))
            return false;
        return name.Equals("server.jar", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("minecraft_server.", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("fabric-server-launch.jar", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("quilt-server-launch.jar", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("paper", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("purpur", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("spigot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDisallowedLaunchDirectory(string root, string path) =>
        DisallowedLaunchDirectories
            .Any(segment => HasRelativeDirectorySegment(root, path, segment));

    private static bool HasRelativeDirectorySegment(string root, string path, string expected)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        EnsureChildPath(fullRoot, fullPath);
        var directory = Path.GetDirectoryName(Path.GetRelativePath(fullRoot, fullPath)) ?? "";
        return directory.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static InstallSourceType ToManagedLoaderSourceType(string loader) =>
        loader.Trim().ToLowerInvariant() switch
        {
            "fabric" => InstallSourceType.Fabric,
            "quilt" => InstallSourceType.Quilt,
            "forge" => InstallSourceType.Forge,
            "neoforge" => InstallSourceType.NeoForge,
            _ => throw new InvalidDataException(
                "The official server pack has no native launch profile and its exact loader cannot be materialized safely by ChunkPilot.")
        };

    private static void NormalizeSinglePackageRoot(string stagingPath)
    {
        var markerPath = CreationOwnershipMarker.PathIn(stagingPath);
        if (CreationStagingSafety.EntryExists(markerPath) &&
            (Directory.Exists(markerPath) || CreationPathSafety.IsReparsePoint(markerPath) ||
             CreationOwnershipMarker.TryRead(stagingPath) is null))
            throw new IOException("The creation staging ownership marker is invalid.");
        var files = Directory.EnumerateFiles(stagingPath)
            .Where(path => !Path.GetFileName(path).Equals(
                CreationOwnershipMarker.FileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var directories = Directory.EnumerateDirectories(stagingPath).ToArray();
        if (files.Length > 0 || directories.Length != 1) return;
        var nested = directories[0];
        foreach (var entry in Directory.EnumerateFileSystemEntries(nested))
        {
            var target = Path.Combine(stagingPath, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("The official server-pack root contains a case-colliding destination.");
            if (Directory.Exists(entry)) Directory.Move(entry, target);
            else File.Move(entry, target);
        }
        Directory.Delete(nested);
    }

    private static ServerEcosystem ToEcosystemFromLoader(string loader) => loader.Trim().ToLowerInvariant() switch
    {
        "fabric" => ServerEcosystem.Fabric,
        "quilt" => ServerEcosystem.Quilt,
        "forge" => ServerEcosystem.Forge,
        "neoforge" => ServerEcosystem.NeoForge,
        _ => ServerEcosystem.Custom
    };

    private static string ResolveJava(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var full = Path.GetFullPath(requested);
            if (!File.Exists(full))
                throw new FileNotFoundException("The selected Java executable was not found.", full);
            if (Path.GetFileName(full).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select java.exe, not javaw.exe, so console capture remains available.");
            return full;
        }
        return "java";
    }

    private static void ValidateStaging(string stagingPath, string launchPath, bool usesArgumentFile)
    {
        EnsureChildPath(stagingPath, launchPath);
        if (!File.Exists(launchPath) || new FileInfo(launchPath).Length == 0)
            throw new InvalidDataException(usesArgumentFile
                ? "The staged loader argument file is missing or empty."
                : "The staged server JAR is missing or empty.");
        if (!File.Exists(Path.Combine(stagingPath, "server.properties")))
            throw new InvalidDataException("server.properties was not created.");
        if (File.ReadAllText(Path.Combine(stagingPath, "eula.txt")).Trim() != "eula=true")
            throw new InvalidDataException("EULA state was not written after explicit agreement.");
    }

    private static ServerEcosystem ToEcosystem(InstallSourceType sourceType) => sourceType switch
    {
        InstallSourceType.Vanilla => ServerEcosystem.Vanilla,
        InstallSourceType.Paper => ServerEcosystem.Paper,
        InstallSourceType.Purpur => ServerEcosystem.Purpur,
        InstallSourceType.Fabric => ServerEcosystem.Fabric,
        InstallSourceType.Quilt => ServerEcosystem.Quilt,
        InstallSourceType.Forge => ServerEcosystem.Forge,
        InstallSourceType.NeoForge => ServerEcosystem.NeoForge,
        _ => ServerEcosystem.Custom
    };

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(source);
        var destinationRoot = Path.GetFullPath(destination);
        var pending = new Queue<string>();
        pending.Enqueue(sourceRoot);
        var fileCount = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Reparse points are not copied into managed instances: {child}");
                var childTarget = Path.GetFullPath(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, child)));
                EnsureChildPath(destinationRoot, childTarget);
                Directory.CreateDirectory(childTarget);
                pending.Enqueue(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++fileCount > ServerImportInspectionService.MaximumEntries)
                    throw new InvalidDataException($"The server folder exceeds the {ServerImportInspectionService.MaximumEntries:N0}-file managed-copy limit.");
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Reparse points are not copied into managed instances: {file}");
                totalBytes = checked(totalBytes + info.Length);
                if (totalBytes > ServerImportInspectionService.MaximumExpandedBytes)
                    throw new InvalidDataException("The server folder exceeds the 16 GB managed-copy limit.");
                var target = Path.GetFullPath(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file)));
                EnsureChildPath(destinationRoot, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                if (output.Length != info.Length)
                    throw new IOException($"The source changed while ChunkPilot copied {Path.GetRelativePath(sourceRoot, file)}.");
            }
        }
    }


    /// <summary>
    /// Path containment, delegated to the one shared implementation so creation and extraction cannot
    /// disagree about what "inside" means.
    /// </summary>
    private static void EnsureChildPath(string root, string candidate) =>
        CreationPathSafety.EnsureWithin(root, candidate);

    private static string Sha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string MakeSafeDownloadName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(name.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "server-package" : result;
    }

    private static async Task AppendLogAsync(string logPath, string line, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}",
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static void Report(
        IProgress<InstallProgress>? progress,
        Guid operationId,
        InstallState state,
        CreationStage stage,
        string step,
        double percent,
        long bytes,
        long? total,
        double speed,
        string detail,
        string logPath) =>
        progress?.Report(new InstallProgress
        {
            OperationId = operationId,
            State = state,
            Stage = stage,
            CurrentStep = step,
            OverallPercent = Math.Clamp(percent, 0, 100),
            BytesDownloaded = bytes,
            TotalBytes = total,
            BytesPerSecond = speed,
            Detail = detail,
            StagingLogPath = logPath
        });

    private sealed record StagedPayload(
        string FileName,
        string MinecraftVersion,
        string Build,
        string SourceUrl,
        string Sha256,
        bool UsesArgumentFile,
        ServerEcosystem Ecosystem,
        string InstallerVersion);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
