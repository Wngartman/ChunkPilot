using System.Security.Cryptography;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record PluginInstallResult(PluginRelease Release, JarInstallReceipt Receipt);
public sealed record PluginInstallPlanResult(
    PluginInstallPlan Plan,
    IReadOnlyList<PluginInstallResult> Installed);

public sealed class PluginManagementService
{
    private readonly PluginProviderRegistry providers;
    private readonly JarInventoryService jars;
    private readonly AppDataPaths paths;
    private readonly HttpClient http;
    private readonly CurseForgeApiClient? curseForge;

    public PluginManagementService(
        PluginProviderRegistry providers,
        JarInventoryService jars,
        AppDataPaths paths,
        HttpClient? httpClient = null,
        CurseForgeApiClient? curseForge = null)
    {
        this.providers = providers;
        this.jars = jars;
        this.paths = paths;
        this.curseForge = curseForge;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public IReadOnlyList<PluginProviderStatus> ProviderStatuses => providers.Statuses;

    public Task<IReadOnlyList<PluginProject>> SearchAsync(
        ServerDefinition server, string search, int limit, CancellationToken cancellationToken = default) =>
        SearchAsync(server, search, limit, PluginProviderKind.Modrinth, cancellationToken);

    public Task<IReadOnlyList<PluginProject>> SearchAsync(
        ServerDefinition server, string search, int limit, PluginProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        var kind = RequireManagedAddonServer(server);
        if (kind == ManagedAddonKind.Plugin && provider == PluginProviderKind.CurseForge)
            throw new InvalidOperationException("CurseForge Paper/Bukkit plugin discovery is not enabled in this phase.");
        return providers.Get(provider).SearchAsync(new PluginCatalogQuery
        {
            Kind = kind,
            Search = search,
            MinecraftVersion = server.MinecraftVersion,
            Loader = ProviderLoader(server),
            Limit = limit
        }, cancellationToken);
    }

    public Task<PluginRelease?> ResolveAsync(
        ServerDefinition server, string projectId, string? versionId = null,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(server, projectId, versionId, PluginProviderKind.Modrinth, cancellationToken);

    public Task<PluginRelease?> ResolveAsync(
        ServerDefinition server, string projectId, string? versionId,
        PluginProviderKind provider, CancellationToken cancellationToken = default)
    {
        _ = RequireManagedAddonServer(server);
        if (server.Ecosystem is ServerEcosystem.Paper or ServerEcosystem.Purpur or
            ServerEcosystem.Spigot or ServerEcosystem.Bukkit && provider == PluginProviderKind.CurseForge)
            throw new InvalidOperationException("CurseForge plugin installation is deliberately unsupported.");
        return providers.Get(provider).ResolveReleaseAsync(
            projectId, server.MinecraftVersion,
            ProviderLoader(server),
            versionId, cancellationToken);
    }

    public async Task<PluginRelease> InstallAsync(
        ServerDefinition server,
        string projectId,
        string versionId,
        CancellationToken cancellationToken = default) =>
        (await InstallWithReceiptAsync(server, projectId, versionId, null, cancellationToken).ConfigureAwait(false)).Release;

    public async Task<PluginInstallResult> InstallWithReceiptAsync(
        ServerDefinition server,
        string projectId,
        string versionId,
        IProgress<ManagedContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await InstallWithReceiptAsync(server, projectId, versionId, progress,
            PluginProviderKind.Modrinth, cancellationToken).ConfigureAwait(false);

    public async Task<PluginInstallResult> InstallWithReceiptAsync(
        ServerDefinition server,
        string projectId,
        string versionId,
        IProgress<ManagedContentProgress>? progress,
        PluginProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        var kind = RequireManagedAddonServer(server);
        Report(progress, ManagedContentOperationStage.ResolvingDependencies,
            "Resolving the exact compatible release and its dependencies.");
        var release = await ResolveAsync(server, projectId, versionId, provider, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{provider} does not publish that exact {kind.ToString().ToLowerInvariant()} release for Minecraft {server.MinecraftVersion} and {ProviderLoader(server)}.");
        if (release.Kind != kind)
            throw new InvalidDataException("The provider release type does not match this server's add-on capability.");
        if (release.ClientRequirement.Equals("ClientOnly", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This release is client-only and cannot be installed on a dedicated server.");
        var inventory = jars.Inventory(server);
        var unresolvedDependencies = RequiredDependenciesNotProven(inventory, release);
        if (unresolvedDependencies.Count > 0)
            throw new InvalidOperationException(
                $"Install the required add-on dependencies first: {string.Join(", ", unresolvedDependencies)}. " +
                "ChunkPilot will not install an add-on when its required dependencies cannot be proven from the current JAR inventory.");
        return await InstallResolvedAsync(server, release, inventory, cancellationToken, progress).ConfigureAwait(false);
    }

    public async Task<PluginInstallPlan> PlanAsync(
        ServerDefinition server,
        string projectId,
        string versionId,
        CancellationToken cancellationToken = default) =>
        await PlanAsync(server, projectId, versionId, PluginProviderKind.Modrinth, cancellationToken)
            .ConfigureAwait(false);

    public async Task<PluginInstallPlan> PlanAsync(
        ServerDefinition server,
        string projectId,
        string versionId,
        PluginProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        _ = RequireManagedAddonServer(server);
        var ordered = new List<PluginRelease>();
        var problems = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inventory = jars.Inventory(server);

        async Task VisitAsync(string dependencyProjectId, string? dependencyVersionId, int depth)
        {
            if (depth > 16 || ordered.Count >= 64)
            {
                problems.Add("The required dependency graph exceeds ChunkPilot's bounded safety limit.");
                return;
            }
            if (string.IsNullOrWhiteSpace(dependencyProjectId))
            {
                problems.Add("A required dependency has no provider project identity.");
                return;
            }
            var key = dependencyProjectId + "@" + (dependencyVersionId ?? "latest-compatible");
            if (visited.Contains(key)) return;
            if (!visiting.Add(key))
            {
                problems.Add($"Dependency cycle detected at {dependencyProjectId}.");
                return;
            }
            var release = await ResolveAsync(server, dependencyProjectId, dependencyVersionId,
                provider, cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                problems.Add($"No exact compatible provider release could be resolved for {dependencyProjectId}.");
                visiting.Remove(key);
                return;
            }
            if (release.ClientRequirement.Equals("ClientOnly", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{release.VersionName} is client-only and cannot be installed on this dedicated server.");
            foreach (var incompatible in release.Dependencies.Where(item =>
                         item.Type.Equals("incompatible", StringComparison.OrdinalIgnoreCase)))
            {
                if (inventory.Any(entry => DependencyMatches(entry, incompatible)))
                    problems.Add($"{release.VersionName} is incompatible with installed add-on {DependencyLabel(incompatible)}.");
            }
            foreach (var dependency in release.Dependencies.Where(item =>
                         item.Type.Equals("required", StringComparison.OrdinalIgnoreCase)))
            {
                if (inventory.Any(entry => DependencyMatches(entry, dependency))) continue;
                if (string.IsNullOrWhiteSpace(dependency.ProjectId))
                {
                    problems.Add($"{release.VersionName} requires {DependencyLabel(dependency)}, but the provider did not supply a project identity.");
                    continue;
                }
                await VisitAsync(dependency.ProjectId,
                    string.IsNullOrWhiteSpace(dependency.VersionId) ? null : dependency.VersionId, depth + 1)
                    .ConfigureAwait(false);
            }
            visiting.Remove(key);
            visited.Add(key);
            if (!inventory.Any(entry => entry.Provider == release.Provider &&
                    entry.ProviderProjectId.Equals(release.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                    entry.ProviderVersionId.Equals(release.VersionId, StringComparison.OrdinalIgnoreCase)))
                ordered.Add(release);
        }

        await VisitAsync(projectId, versionId, 0).ConfigureAwait(false);
        return new PluginInstallPlan
        {
            Releases = ordered,
            Problems = problems.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public async Task<PluginInstallPlanResult> InstallPlanWithReceiptsAsync(
        ServerDefinition server,
        string projectId,
        string versionId,
        IProgress<ManagedContentProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await InstallPlanWithReceiptsAsync(server, projectId, versionId, progress,
            PluginProviderKind.Modrinth, cancellationToken).ConfigureAwait(false);

    public async Task<PluginInstallPlanResult> InstallPlanWithReceiptsAsync(
        ServerDefinition server,
        string projectId,
        string versionId,
        IProgress<ManagedContentProgress>? progress,
        PluginProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        Report(progress, ManagedContentOperationStage.ResolvingDependencies,
            "Resolving the bounded dependency plan.");
        var plan = await PlanAsync(server, projectId, versionId, provider, cancellationToken).ConfigureAwait(false);
        if (!plan.CanInstall)
            throw new InvalidOperationException(plan.Problems.Count > 0
                ? string.Join(" ", plan.Problems)
                : "Every exact release in this dependency plan is already installed.");
        EnsureDestinationOwnership(server, plan.Releases, jars.Inventory(server));
        var installed = new List<PluginInstallResult>();
        try
        {
            foreach (var release in plan.Releases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inventory = jars.Inventory(server);
                var unresolved = RequiredDependenciesNotProven(inventory, release);
                if (unresolved.Count > 0)
                    throw new InvalidOperationException(
                        $"The verified dependency order could not prove: {string.Join(", ", unresolved)}.");
                installed.Add(await InstallResolvedAsync(server, release, inventory, cancellationToken, progress)
                    .ConfigureAwait(false));
            }
            return new PluginInstallPlanResult(plan, installed);
        }
        catch (Exception installFailure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var result in installed.AsEnumerable().Reverse())
            {
                try { jars.RollbackInstall(server, result.Receipt); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    rollbackFailures.Add(exception);
                }
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException(
                    "The add-on plan failed and one or more installed files could not be rolled back. Recovery evidence was preserved.",
                    new[] { installFailure }.Concat(rollbackFailures));
            throw;
        }
    }

    /// <summary>
    /// Installs the exact dependency-first CurseForge plan previously retained by the Agent's
    /// one-time authorization registry. Provider metadata is deliberately not resolved again;
    /// compatibility, inventory, dependency order, and download evidence are rechecked locally.
    /// </summary>
    public async Task<PluginInstallPlanResult> InstallExactPlanWithReceiptsAsync(
        ServerDefinition server,
        string rootProjectId,
        string rootVersionId,
        PluginInstallPlan exactPlan,
        IProgress<ManagedContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, ManagedContentOperationStage.ResolvingDependencies,
            "Revalidating the exact reviewed CurseForge dependency plan.");
        var plan = ValidateExactCurseForgePlan(server, rootProjectId, rootVersionId, exactPlan);
        var installed = new List<PluginInstallResult>();
        try
        {
            foreach (var release in plan.Releases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inventory = jars.Inventory(server);
                if (inventory.Any(entry => ExactReleaseMatches(entry, release)))
                    continue;
                var incompatible = release.Dependencies
                    .Where(dependency => dependency.Type.Equals("incompatible", StringComparison.OrdinalIgnoreCase))
                    .Where(dependency => inventory.Any(entry => ExactDependencyMatches(entry, dependency)))
                    .Select(DependencyLabel)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (incompatible.Length > 0)
                    throw new InvalidOperationException(
                        $"The reviewed plan is incompatible with installed add-on(s): {string.Join(", ", incompatible)}.");
                var unresolved = RequiredDependenciesNotProvenExact(inventory, release);
                if (unresolved.Count > 0)
                    throw new InvalidOperationException(
                        $"The reviewed dependency order could not prove: {string.Join(", ", unresolved)}.");
                installed.Add(await InstallResolvedAsync(server, release, inventory, cancellationToken, progress)
                    .ConfigureAwait(false));
            }
            return new PluginInstallPlanResult(plan, installed);
        }
        catch (Exception installFailure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var result in installed.AsEnumerable().Reverse())
            {
                try { jars.RollbackInstall(server, result.Receipt); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    rollbackFailures.Add(exception);
                }
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException(
                    "The exact reviewed add-on plan failed and one or more installed files could not be rolled back. Recovery evidence was preserved.",
                    new[] { installFailure }.Concat(rollbackFailures));
            throw;
        }
    }

    public void RollbackPlan(ServerDefinition server, PluginInstallPlanResult result)
    {
        foreach (var installed in result.Installed.Reverse())
            jars.RollbackInstall(server, installed.Receipt);
    }

    private async Task<PluginInstallResult> InstallResolvedAsync(
        ServerDefinition server,
        PluginRelease release,
        IReadOnlyList<ModPluginEntry> inventory,
        CancellationToken cancellationToken,
        IProgress<ManagedContentProgress>? progress)
    {
        EnsureDestinationOwnership(server, [release], inventory);
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            (release.Provider == PluginProviderKind.Modrinth
                ? !uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
                : release.Provider != PluginProviderKind.CurseForge ||
                  !CurseForgeApiClient.IsApprovedDownloadUri(uri)))
            throw new InvalidDataException("The provider returned an untrusted add-on download location.");
        if (!release.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
            release.SizeBytes is <= 0 or > JarInventoryService.MaximumJarBytes)
            throw new InvalidDataException("The provider did not return a bounded Java archive.");

        var staging = Path.Combine(paths.Staging, "addons", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var temporary = Path.Combine(staging, "download.partial");
        try
        {
            Report(progress, ManagedContentOperationStage.Downloading,
                $"Downloading {release.FileName}.", 0, 0, release.SizeBytes);
            using var response = release.Provider == PluginProviderKind.CurseForge
                ? curseForge is not null
                    ? await curseForge.SendDownloadAsync(uri, cancellationToken).ConfigureAwait(false)
                    : throw new InvalidOperationException("The native CurseForge download boundary is unavailable.")
                : await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (release.Provider == PluginProviderKind.CurseForge
                ? curseForge?.IsApprovedDownloadResponse(response, uri) != true
                : response.RequestMessage?.RequestUri is not { } finalUri || finalUri.Scheme != Uri.UriSchemeHttps ||
                  !finalUri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The add-on download left its trusted provider CDN.");
            if (response.Content.Headers.ContentLength is { } length && length != release.SizeBytes)
                throw new InvalidDataException("The add-on download size does not match provider metadata.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                long lastReported = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > release.SizeBytes || total > JarInventoryService.MaximumJarBytes)
                        throw new InvalidDataException("The add-on download exceeded its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (total == release.SizeBytes || total - lastReported >= 256 * 1024)
                    {
                        lastReported = total;
                        Report(progress, ManagedContentOperationStage.Downloading,
                            $"Downloading {release.FileName}.",
                            release.SizeBytes > 0 ? total * 100d / release.SizeBytes : null,
                            total, release.SizeBytes);
                    }
                }
                if (total != release.SizeBytes)
                    throw new InvalidDataException("The add-on download ended before its declared size.");
            }
            Report(progress, ManagedContentOperationStage.Verifying,
                release.Provider == PluginProviderKind.CurseForge
                    ? "Verifying the provider SHA-1 hash."
                    : "Verifying the provider SHA-512 hash.", 100, release.SizeBytes, release.SizeBytes);
            await using (var verify = File.OpenRead(temporary))
            {
                var actual = release.Provider == PluginProviderKind.CurseForge
#pragma warning disable CA5350 // CurseForge currently publishes SHA-1/MD5; SHA-1 is provider identity evidence, and a local SHA-256 baseline is also recorded.
                    ? Convert.ToHexString(await SHA1.HashDataAsync(verify, cancellationToken).ConfigureAwait(false))
#pragma warning restore CA5350
                    : Convert.ToHexString(await SHA512.HashDataAsync(verify, cancellationToken).ConfigureAwait(false));
                var expected = release.Provider == PluginProviderKind.CurseForge ? release.Sha1 : release.Sha512;
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The add-on download did not match the provider hash.");
            }
            Report(progress, ManagedContentOperationStage.InspectingMetadata,
                "Inspecting the verified add-on metadata without executing it.");
            var stagedJar = Path.Combine(staging, SafeFileName(release.FileName));
            File.Move(temporary, stagedJar);
            var currentInventory = jars.Inventory(server);
            EnsureDestinationOwnership(server, [release], currentInventory);
            var existing = currentInventory.FirstOrDefault(entry =>
                entry.Provider == release.Provider &&
                entry.ProviderProjectId.Equals(release.ProjectId, StringComparison.OrdinalIgnoreCase));
            Report(progress, ManagedContentOperationStage.Staging,
                "Staging the verified add-on for reversible activation.");
            Report(progress, ManagedContentOperationStage.Installing,
                "Installing the verified add-on through the Agent-owned transaction.");
            var receipt = await jars.InstallReviewedProviderWithReceiptAsync(
                server, stagedJar, release, existing, cancellationToken).ConfigureAwait(false);
            Report(progress, ManagedContentOperationStage.PendingRestart,
                "The verified add-on is installed and awaits authoritative restart/load verification.");
            return new PluginInstallResult(release, receipt);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Report(
        IProgress<ManagedContentProgress>? progress,
        ManagedContentOperationStage stage,
        string message,
        double? percent = null,
        long? bytesTransferred = null,
        long? totalBytes = null) => progress?.Report(new ManagedContentProgress
        {
            Stage = stage,
            Message = message,
            Percent = percent,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes
        });

    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value);
        if (name.Length is 0 or > 180 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The provider returned an invalid plugin filename.");
        return name;
    }

    private static IReadOnlyList<string> RequiredDependenciesNotProven(
        IReadOnlyList<ModPluginEntry> inventory,
        PluginRelease release)
    {
        return release.Dependencies
            .Where(dependency => dependency.Type.Equals("required", StringComparison.OrdinalIgnoreCase))
            .Where(dependency => !inventory.Any(entry => DependencyMatches(entry, dependency)))
            .Select(DependencyLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(JarInventoryService.MaximumDependencies)
            .ToArray();
    }

    private PluginInstallPlan ValidateExactCurseForgePlan(
        ServerDefinition server,
        string rootProjectId,
        string rootVersionId,
        PluginInstallPlan exactPlan)
    {
        ArgumentNullException.ThrowIfNull(exactPlan);
        var kind = RequireManagedAddonServer(server);
        if (kind != ManagedAddonKind.Mod || !PositiveProviderId(rootProjectId) ||
            !PositiveProviderId(rootVersionId) || !exactPlan.CanInstall ||
            exactPlan.Releases.Count > 64 || exactPlan.Authorization is not null)
            throw new InvalidDataException("The authorized CurseForge dependency plan is incomplete or out of scope.");

        var expectedLoader = ProviderLoader(server);
        var releases = new PluginRelease[exactPlan.Releases.Count];
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        var destinationFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < releases.Length; index++)
        {
            var release = exactPlan.Releases[index] ?? throw new InvalidDataException(
                "The authorized CurseForge dependency plan contains an empty release.");
            if (release.Provider != PluginProviderKind.CurseForge || release.Kind != kind ||
                !PositiveProviderId(release.ProjectId) || !PositiveProviderId(release.VersionId) ||
                !projectIds.Add(release.ProjectId) ||
                !release.MinecraftVersion.Equals(server.MinecraftVersion, StringComparison.OrdinalIgnoreCase) ||
                !release.Loader.Equals(expectedLoader, StringComparison.OrdinalIgnoreCase) ||
                release.ClientRequirement.Equals("ClientOnly", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri) ||
                !CurseForgeApiClient.IsApprovedDownloadUri(uri) ||
                release.FileName.Length is <= 4 or > 180 ||
                !Path.GetFileName(release.FileName).Equals(release.FileName, StringComparison.Ordinal) ||
                release.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !release.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                release.SizeBytes is <= 0 or > JarInventoryService.MaximumJarBytes ||
                release.Sha1.Length != 40 || !release.Sha1.All(Uri.IsHexDigit) ||
                release.Sha512.Length != 0 || release.Dependencies.Count > 128)
                throw new InvalidDataException(
                    "The authorized CurseForge dependency plan contradicts the server or provider evidence.");
            if (!destinationFileNames.Add(release.FileName))
                throw new InvalidDataException(
                    "The authorized CurseForge dependency plan assigns the same destination JAR filename to multiple releases.");

            var dependencies = new PluginDependency[release.Dependencies.Count];
            for (var dependencyIndex = 0; dependencyIndex < dependencies.Length; dependencyIndex++)
            {
                var dependency = release.Dependencies[dependencyIndex] ?? throw new InvalidDataException(
                    "The authorized CurseForge dependency plan contains an empty dependency.");
                if (!PositiveProviderId(dependency.ProjectId) || dependency.VersionId.Length != 0 ||
                    dependency.FileName.Length != 0 || dependency.Type is not
                        ("required" or "optional" or "embedded" or "tool" or "incompatible"))
                    throw new InvalidDataException(
                        "The authorized CurseForge dependency plan contains unsupported dependency evidence.");
                dependencies[dependencyIndex] = dependency with { };
            }
            releases[index] = release with { Dependencies = dependencies };
        }

        var root = releases[^1];
        if (!root.ProjectId.Equals(rootProjectId, StringComparison.Ordinal) ||
            !root.VersionId.Equals(rootVersionId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The authorized CurseForge dependency plan does not end with the exact reviewed root release.");

        var releasesByProject = releases.ToDictionary(release => release.ProjectId, StringComparer.Ordinal);
        var reachableProjects = new HashSet<string>(StringComparer.Ordinal) { root.ProjectId };
        var pendingProjects = new Stack<string>();
        pendingProjects.Push(root.ProjectId);
        while (pendingProjects.TryPop(out var projectId))
        {
            foreach (var required in releasesByProject[projectId].Dependencies.Where(dependency =>
                         dependency.Type.Equals("required", StringComparison.OrdinalIgnoreCase)))
            {
                if (releasesByProject.ContainsKey(required.ProjectId) && reachableProjects.Add(required.ProjectId))
                    pendingProjects.Push(required.ProjectId);
            }
        }
        if (reachableProjects.Count != releases.Length)
            throw new InvalidDataException(
                "The authorized CurseForge dependency plan contains a release outside the root dependency closure.");

        var inventory = jars.Inventory(server);
        EnsureDestinationOwnership(server, releases, inventory);
        var priorReleases = new List<PluginRelease>();
        foreach (var release in releases)
        {
            foreach (var incompatible in release.Dependencies.Where(dependency =>
                         dependency.Type.Equals("incompatible", StringComparison.OrdinalIgnoreCase)))
            {
                if (inventory.Any(entry => ExactDependencyMatches(entry, incompatible)) ||
                    releases.Any(candidate => !candidate.ProjectId.Equals(release.ProjectId, StringComparison.Ordinal) &&
                        ExactDependencyMatches(candidate, incompatible)))
                    throw new InvalidOperationException(
                        $"The reviewed plan conflicts with {DependencyLabel(incompatible)}.");
            }
            foreach (var required in release.Dependencies.Where(dependency =>
                         dependency.Type.Equals("required", StringComparison.OrdinalIgnoreCase)))
            {
                if (inventory.Any(entry => ExactDependencyMatches(entry, required)) ||
                    priorReleases.Any(candidate => ExactDependencyMatches(candidate, required)))
                    continue;
                throw new InvalidOperationException(
                    $"The reviewed plan does not place required dependency {DependencyLabel(required)} before {release.FileName}.");
            }
            priorReleases.Add(release);
        }

        return new PluginInstallPlan { Releases = releases, Problems = [], Authorization = null };
    }

    /// <summary>
    /// Proves every provider target is either vacant or an authoritative update of that same
    /// provider project. This runs for the whole exact plan before its first download and again
    /// immediately before each activation, so a filename alone never grants replacement rights.
    /// </summary>
    private static void EnsureDestinationOwnership(
        ServerDefinition server,
        IReadOnlyList<PluginRelease> releases,
        IReadOnlyList<ModPluginEntry> inventory)
    {
        foreach (var release in releases)
        {
            var collisions = inventory.Where(entry =>
                    entry.FileName.Equals(release.FileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (collisions.Length > 1 || collisions.Any(entry =>
                    entry.Provider != release.Provider ||
                    !entry.ProviderProjectId.Equals(release.ProjectId, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    $"The destination JAR filename '{release.FileName}' is already owned by a local or different-project add-on. Nothing was changed.");

        }
    }

    private static IReadOnlyList<string> RequiredDependenciesNotProvenExact(
        IReadOnlyList<ModPluginEntry> inventory,
        PluginRelease release) => release.Dependencies
        .Where(dependency => dependency.Type.Equals("required", StringComparison.OrdinalIgnoreCase))
        .Where(dependency => !inventory.Any(entry => ExactDependencyMatches(entry, dependency)))
        .Select(DependencyLabel)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(JarInventoryService.MaximumDependencies)
        .ToArray();

    private static bool ExactReleaseMatches(ModPluginEntry entry, PluginRelease release) =>
        entry.Provider == release.Provider &&
        entry.ProviderProjectId.Equals(release.ProjectId, StringComparison.Ordinal) &&
        entry.ProviderVersionId.Equals(release.VersionId, StringComparison.Ordinal);

    private static bool ExactDependencyMatches(ModPluginEntry entry, PluginDependency dependency)
    {
        if (!entry.ProviderProjectId.Equals(dependency.ProjectId, StringComparison.Ordinal) &&
            !entry.Id.Equals(dependency.ProjectId, StringComparison.Ordinal))
            return false;
        return dependency.VersionId.Length == 0 ||
               entry.ProviderVersionId.Equals(dependency.VersionId, StringComparison.Ordinal);
    }

    private static bool ExactDependencyMatches(PluginRelease release, PluginDependency dependency)
    {
        if (!release.ProjectId.Equals(dependency.ProjectId, StringComparison.Ordinal) &&
            !release.FileName.Equals(Path.GetFileName(dependency.FileName), StringComparison.OrdinalIgnoreCase))
            return false;
        return dependency.VersionId.Length == 0 || release.VersionId.Equals(dependency.VersionId, StringComparison.Ordinal);
    }

    private static bool PositiveProviderId(string value) =>
        value.Length is > 0 and <= 80 && long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0 &&
        parsed.ToString(System.Globalization.CultureInfo.InvariantCulture).Equals(value, StringComparison.Ordinal);

    private static bool DependencyMatches(ModPluginEntry entry, PluginDependency dependency)
    {
        if (!string.IsNullOrWhiteSpace(dependency.ProjectId) &&
            (entry.Id.Equals(dependency.ProjectId, StringComparison.OrdinalIgnoreCase) ||
             entry.ProviderProjectId.Equals(dependency.ProjectId, StringComparison.OrdinalIgnoreCase)))
            return true;
        return !string.IsNullOrWhiteSpace(dependency.FileName) &&
               entry.FileName.Equals(Path.GetFileName(dependency.FileName), StringComparison.OrdinalIgnoreCase);
    }

    private static string DependencyLabel(PluginDependency dependency)
    {
        if (!string.IsNullOrWhiteSpace(dependency.FileName))
            return Path.GetFileName(dependency.FileName);
        if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
            return dependency.ProjectId;
        if (!string.IsNullOrWhiteSpace(dependency.VersionId))
            return dependency.VersionId;
        return "an unnamed required dependency";
    }

    private static ManagedAddonKind RequireManagedAddonServer(ServerDefinition server)
    {
        var kind = server.Ecosystem switch
        {
            ServerEcosystem.Paper or ServerEcosystem.Purpur or ServerEcosystem.Spigot or ServerEcosystem.Bukkit =>
                ManagedAddonKind.Plugin,
            ServerEcosystem.Fabric or ServerEcosystem.Quilt or ServerEcosystem.Forge or ServerEcosystem.NeoForge =>
                ManagedAddonKind.Mod,
            _ => throw new InvalidOperationException(
                "Add-on management is available only for confirmed Paper, Fabric, or NeoForge servers.")
        };
        if (string.IsNullOrWhiteSpace(server.MinecraftVersion) || server.MinecraftVersion == "Unknown")
            throw new InvalidOperationException("The server Minecraft version is unknown, so exact add-on compatibility cannot be resolved.");
        return kind;
    }

    private static string ProviderLoader(ServerDefinition server) => server.Ecosystem switch
    {
        ServerEcosystem.Purpur => "purpur",
        ServerEcosystem.Fabric => "fabric",
        ServerEcosystem.Quilt => "quilt",
        ServerEcosystem.Forge => "forge",
        ServerEcosystem.NeoForge => "neoforge",
        _ => "paper"
    };
}
