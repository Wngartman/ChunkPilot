using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class ManagedLoaderCatalogService
{
    public const string OrnitheMetaRoot = "https://meta.ornithemc.net/v3/versions";

    private async Task<ManagedLoaderBuildCatalog> RefreshOrnitheBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var canonical = OrnitheHistoricalVersionPolicy.Canonical(minecraftVersion);
        if (!OrnitheHistoricalVersionPolicy.IsExactTarget(canonical))
            return ManagedLoaderBuildCatalog.Unavailable(
                ManagedLoaderPlatform.Ornithe,
                canonical,
                "ChunkPilot has not established a typed headless Ornithe contract for this exact Minecraft version.");

        var providerVersion = OrnitheHistoricalVersionPolicy.ProviderVersion(canonical);
        var generation = await StableOrnitheGenerationAsync(cancellationToken).ConfigureAwait(false);
        var requirement = OrnitheHistoricalVersionPolicy.ServerArtifact(canonical);
        var builds = new List<ManagedLoaderBuild>();
        foreach (var family in Enum.GetValues<OrnitheLoaderFamily>())
        {
            using var document = await GetJsonAsync(
                OrnitheLoadersUrl(generation, family, providerVersion), cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Ornithe's {family} loader response was not a list.");
            var parsed = document.RootElement.EnumerateArray()
                .Select((item, index) => ParseOrnitheBuild(
                    item, canonical, providerVersion, generation, family, requirement, index))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            var firstStable = parsed.FirstOrDefault(item => item.Channel == ManagedLoaderChannel.Stable);
            builds.AddRange(parsed.Select(item => firstStable is not null &&
                                                  item.LoaderVersion.Equals(firstStable.LoaderVersion,
                                                      StringComparison.OrdinalIgnoreCase)
                ? item with { ProviderRecommended = true }
                : item));
        }

        var strategy = ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Ornithe);
        return new ManagedLoaderBuildCatalog
        {
            Platform = ManagedLoaderPlatform.Ornithe,
            MinecraftVersion = canonical,
            Builds = builds
                .OrderBy(item => item.OrnitheLoaderFamily)
                .ThenByDescending(item => item.ProviderRecommended)
                .ThenByDescending(item => ComparableVersion(item.LoaderVersion))
                .ThenByDescending(item => item.LoaderVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true,
            UnavailableDetail = builds.Count == 0
                ? "Ornithe publishes no compatible Fabric or Quilt loader for this exact historical version."
                : "",
            CreationUnavailableDetail = strategy.CreationUnavailableReason
        };
    }

    public async Task<OrnitheHeadlessServerProfile> GetOrnitheHeadlessProfileAsync(
        string minecraftVersion,
        OrnitheLoaderFamily loaderFamily,
        string loaderVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateVersion(loaderVersion);
        var canonical = OrnitheHistoricalVersionPolicy.Canonical(minecraftVersion);
        var providerVersion = OrnitheHistoricalVersionPolicy.ProviderVersion(canonical);
        var generation = await StableOrnitheGenerationAsync(cancellationToken).ConfigureAwait(false);
        var url = OrnitheProfileUrl(generation, loaderFamily, providerVersion, loaderVersion);
        var (document, sha256) = await GetJsonWithSha256Async(url, cancellationToken).ConfigureAwait(false);
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ornithe's exact headless server profile was not an object.");
            var profileId = ReadString(root, "id");
            var mainClass = ReadString(root, "mainClass");
            var inheritsFrom = ReadString(root, "inheritsFrom");
            if (!SafeProfileId().IsMatch(profileId) || !SafeJavaClass().IsMatch(mainClass) ||
                !inheritsFrom.Equals(providerVersion + "-vanilla", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Ornithe's headless profile identity did not match the requested version.");

            var jvm = ReadArgumentArray(root, "jvm");
            var game = ReadArgumentArray(root, "game");
            var libraries = ReadOrnitheLibraries(root);
            if (libraries.Count == 0)
                throw new InvalidDataException("Ornithe's headless profile contained no server libraries.");
            return new OrnitheHeadlessServerProfile
            {
                ProfileId = profileId,
                MinecraftVersion = canonical,
                ProviderMinecraftVersion = providerVersion,
                IntermediaryGeneration = generation,
                LoaderFamily = loaderFamily,
                LoaderVersion = loaderVersion,
                MainClass = mainClass,
                JvmArguments = jvm,
                GameArguments = game,
                Libraries = libraries,
                MetadataUrl = url,
                MetadataSha256 = sha256,
                RetrievedUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private async Task<int> StableOrnitheGenerationAsync(CancellationToken cancellationToken)
    {
        using var generations = await GetJsonAsync(OrnitheGenerationUrl, cancellationToken).ConfigureAwait(false);
        if (!generations.RootElement.TryGetProperty("stableIntermediaryGeneration", out var stableValue) ||
            !stableValue.TryGetInt32(out var generation) || generation <= 0)
            throw new InvalidDataException("Ornithe did not identify a stable intermediary generation.");
        return generation;
    }

    private static ManagedLoaderBuild? ParseOrnitheBuild(
        JsonElement item,
        string canonicalMinecraftVersion,
        string providerMinecraftVersion,
        int generation,
        OrnitheLoaderFamily family,
        HistoricalMinecraftServerArtifactRequirement requirement,
        int index)
    {
        if (!item.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("intermediary", out var intermediary) ||
            intermediary.ValueKind != JsonValueKind.Object)
            return null;
        var loaderVersion = ReadString(loader, "version");
        var intermediaryVersion = ReadString(intermediary, "version");
        if (!SafeVersion().IsMatch(loaderVersion) ||
            !intermediaryVersion.Equals(providerMinecraftVersion, StringComparison.OrdinalIgnoreCase))
            return null;
        var stable = loader.TryGetProperty("stable", out var stableValue) &&
                     stableValue.ValueKind == JsonValueKind.True;
        var java = 8;
        if (item.TryGetProperty("launcherMeta", out var launcher) &&
            launcher.ValueKind == JsonValueKind.Object &&
            launcher.TryGetProperty("min_java_version", out var minimumJava) &&
            minimumJava.TryGetInt32(out var declaredJava) && declaredJava >= 8)
            java = declaredJava;
        var profileUrl = OrnitheProfileUrl(generation, family, providerMinecraftVersion, loaderVersion);
        return new ManagedLoaderBuild
        {
            Platform = ManagedLoaderPlatform.Ornithe,
            MinecraftVersion = canonicalMinecraftVersion,
            ProviderMinecraftVersion = providerMinecraftVersion,
            LoaderVersion = loaderVersion,
            InstallerVersion = $"ornithe-gen{generation}-{family.ToString().ToLowerInvariant()}",
            Channel = stable ? ManagedLoaderChannel.Stable : LoaderChannel(loaderVersion),
            ProviderLatest = index == 0,
            HeadlessProfileUrl = profileUrl,
            OrnitheLoaderFamily = family,
            IntermediaryGeneration = generation,
            MinecraftServerArtifact = requirement,
            RequiredJavaMajor = java,
            Provenance =
                $"Official Ornithe Meta v3 generation {generation} {family} loader and exact headless server-profile endpoints",
            SupportReason = requirement.IsAutomaticallyAcquirable
                ? "Exact official Ornithe loader identity with an official Mojang server artifact; runtime certification and Agent activation remain required."
                : "Exact official Ornithe loader identity; a user-owned Minecraft server JAR must be inspected and rehashed before runtime certification.",
            UnavailableReason = requirement.Reason,
            Certification = new MinecraftVersionCertification
            {
                Level = MinecraftVersionCertificationLevel.MetadataValidated,
                OfficialVersionRecord = true,
                OfficialServerArtifact = requirement.IsAutomaticallyAcquirable,
                ArtifactIntegrityMetadata = requirement.IsAutomaticallyAcquirable,
                JavaResolved = java >= 8,
                LaunchProfileResolved = true,
                Evidence =
                [
                    $"Official Ornithe Meta v3 generation {generation} {family} loader identity.",
                    requirement.Reason
                ],
                Limitations = requirement.IsAutomaticallyAcquirable
                    ? ["The exact Ornithe headless profile has not yet passed ChunkPilot runtime certification."]
                    : [requirement.Reason]
            }
        };
    }

    private static IReadOnlyList<string> ReadArgumentArray(JsonElement root, string kind)
    {
        if (!root.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(kind, out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            var argument = value.GetString() ?? "";
            if (argument.Length is > 0 and <= 1_024 && !argument.Contains('\0')) result.Add(argument);
        }
        return result;
    }

    private static IReadOnlyList<OrnitheHeadlessLibrary> ReadOrnitheLibraries(JsonElement root)
    {
        if (!root.TryGetProperty("libraries", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<OrnitheHeadlessLibrary>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) continue;
            var coordinate = ReadString(value, "name");
            var repository = ReadString(value, "url");
            if (!SafeMavenCoordinate().IsMatch(coordinate) ||
                !Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !AllowedOrnitheMavenHosts.Contains(uri.Host))
                throw new InvalidDataException("Ornithe's headless profile referenced an unapproved library source.");
            long? size = value.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) &&
                         bytes > 0 ? bytes : null;
            result.Add(new OrnitheHeadlessLibrary
            {
                MavenCoordinate = coordinate,
                RepositoryUrl = repository.EndsWith('/') ? repository : repository + "/",
                Sha1 = NormalizeHash(ReadString(value, "sha1"), 40),
                Sha256 = NormalizeHash(ReadString(value, "sha256"), 64),
                SizeBytes = size
            });
        }
        return result;
    }

    private async Task<(JsonDocument Document, string Sha256)> GetJsonWithSha256Async(
        string url,
        CancellationToken cancellationToken)
    {
        const int maximumMetadataBytes = 4_194_304;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > maximumMetadataBytes)
            throw new InvalidDataException("Ornithe's headless profile exceeded the bounded metadata size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > maximumMetadataBytes)
                throw new InvalidDataException("Ornithe's headless profile exceeded the bounded metadata size.");
            destination.Write(buffer, 0, read);
        }
        var bytes = destination.ToArray();
        if (bytes.Length == 0)
            throw new InvalidDataException("Ornithe's headless profile was empty.");
        return (JsonDocument.Parse(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static string OrnitheLoadersUrl(
        int generation,
        OrnitheLoaderFamily family,
        string providerMinecraftVersion) =>
        $"{OrnitheMetaRoot}/gen{generation}/{OrnitheFamilyPath(family)}/" +
        Uri.EscapeDataString(providerMinecraftVersion);

    private static string OrnitheProfileUrl(
        int generation,
        OrnitheLoaderFamily family,
        string providerMinecraftVersion,
        string loaderVersion) =>
        $"{OrnitheLoadersUrl(generation, family, providerMinecraftVersion)}/" +
        $"{Uri.EscapeDataString(loaderVersion)}/server/json";

    private static string OrnitheFamilyPath(OrnitheLoaderFamily family) => family switch
    {
        OrnitheLoaderFamily.Fabric => "fabric-loader",
        OrnitheLoaderFamily.Quilt => "quilt-loader",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown Ornithe loader family.")
    };

    private static readonly HashSet<string> AllowedOrnitheMavenHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "maven.fabricmc.net",
        "maven.quiltmc.org",
        "maven.ornithemc.net"
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeProfileId();

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$.]{1,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeJavaClass();

    [GeneratedRegex("^[A-Za-z0-9_.-]+:[A-Za-z0-9_.-]+:[A-Za-z0-9_.+\u002D]+(?::[A-Za-z0-9_.-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeMavenCoordinate();
}

public static class OrnitheHeadlessMaterializationPlanner
{
    public static OrnitheHeadlessMaterializationPlan Create(
        ManagedLoaderBuild build,
        OrnitheHeadlessServerProfile profile,
        string userSuppliedArtifactToken = "")
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(profile);
        if (!build.HasHeadlessProfileContract || build.MinecraftServerArtifact is not { } serverArtifact ||
            build.OrnitheLoaderFamily != profile.LoaderFamily ||
            build.IntermediaryGeneration != profile.IntermediaryGeneration ||
            !build.MinecraftVersion.Equals(profile.MinecraftVersion, StringComparison.OrdinalIgnoreCase) ||
            !build.ProviderMinecraftVersion.Equals(profile.ProviderMinecraftVersion, StringComparison.OrdinalIgnoreCase) ||
            !build.LoaderVersion.Equals(profile.LoaderVersion, StringComparison.OrdinalIgnoreCase) ||
            !build.HeadlessProfileUrl.Equals(profile.MetadataUrl, StringComparison.Ordinal))
            throw new InvalidDataException("The exact Ornithe build and headless profile identities do not match.");
        var expectedServerArtifact = OrnitheHistoricalVersionPolicy.ServerArtifact(build.MinecraftVersion);
        if (serverArtifact.Source != expectedServerArtifact.Source ||
            !serverArtifact.ProviderMinecraftVersion.Equals(expectedServerArtifact.ProviderMinecraftVersion,
                StringComparison.OrdinalIgnoreCase) ||
            !serverArtifact.OfficialUrl.Equals(expectedServerArtifact.OfficialUrl, StringComparison.Ordinal) ||
            !serverArtifact.OfficialSha1.Equals(expectedServerArtifact.OfficialSha1,
                StringComparison.OrdinalIgnoreCase) ||
            serverArtifact.OfficialSizeBytes != expectedServerArtifact.OfficialSizeBytes)
            throw new InvalidDataException("The Minecraft server artifact requirement did not match ChunkPilot's exact historical policy.");
        if (profile.MetadataSha256.Length != 64 || !profile.MetadataSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The exact Ornithe profile metadata hash is missing.");
        if (serverArtifact.Source == HistoricalMinecraftServerArtifactSource.UserSupplied &&
            string.IsNullOrWhiteSpace(userSuppliedArtifactToken))
            throw new InvalidOperationException(
                "This historical version requires a short-lived native server-artifact token before materialization.");

        var libraries = profile.Libraries.Select(ToArtifact).ToArray();
        var classPath = MinecraftServerClassPath.Concat(libraries.Select(item => item.RelativePath)).ToArray();
        var gameArguments = profile.GameArguments
            .Where(argument => !argument.Equals("nogui", StringComparison.OrdinalIgnoreCase))
            .Concat(HeadlessNoguiArgument)
            .ToArray();
        return new OrnitheHeadlessMaterializationPlan
        {
            Build = build,
            Profile = profile,
            MinecraftServerArtifact = serverArtifact,
            Libraries = libraries,
            ClassPath = classPath,
            JvmArguments = profile.JvmArguments,
            GameArguments = gameArguments,
            MainClass = profile.MainClass,
            UserSuppliedArtifactToken = userSuppliedArtifactToken,
            EvidenceSummary = serverArtifact.IsAutomaticallyAcquirable
                ? "Official Ornithe profile plus exact official Mojang server artifact; every library still requires provider hash or official Maven sidecar verification."
                : "Official Ornithe profile plus a native-token-bound user-owned server artifact; the Agent must rehash the input before staging."
        };
    }

    private static HeadlessMaterializationArtifact ToArtifact(OrnitheHeadlessLibrary library)
    {
        var parts = library.MavenCoordinate.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(part => string.IsNullOrWhiteSpace(part)))
            throw new InvalidDataException("The Ornithe profile contained an unsupported Maven coordinate.");
        var fileName = $"{parts[1]}-{parts[2]}" + (parts.Length == 4 ? $"-{parts[3]}" : "") + ".jar";
        var relative = string.Join('/', parts[0].Split('.').Concat([parts[1], parts[2], fileName]));
        var repository = new Uri(library.RepositoryUrl, UriKind.Absolute);
        if (!AllowedMaterializationHosts.Contains(repository.Host))
            throw new InvalidDataException("The Ornithe library did not use an approved official Maven origin.");
        var download = new Uri(repository, relative);
        if (!repository.Host.Equals(download.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Ornithe library path escaped its official repository origin.");
        return new HeadlessMaterializationArtifact
        {
            Identity = library.MavenCoordinate,
            DownloadUrl = download.AbsoluteUri,
            RelativePath = "libraries/" + relative,
            Sha1 = library.Sha1,
            Sha256 = library.Sha256,
            SizeBytes = library.SizeBytes,
            IntegrityRequirement = library.Sha256.Length == 64
                ? HeadlessArtifactIntegrityRequirement.ProviderSha256
                : library.Sha1.Length == 40
                    ? HeadlessArtifactIntegrityRequirement.ProviderSha1
                    : HeadlessArtifactIntegrityRequirement.OfficialMavenSidecar
        };
    }

    private static readonly string[] HeadlessNoguiArgument = ["nogui"];
    private static readonly string[] MinecraftServerClassPath = ["minecraft-server.jar"];
    private static readonly HashSet<string> AllowedMaterializationHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "maven.fabricmc.net",
        "maven.quiltmc.org",
        "maven.ornithemc.net"
    };
}
