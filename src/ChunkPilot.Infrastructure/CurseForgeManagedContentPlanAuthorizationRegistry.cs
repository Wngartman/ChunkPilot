using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Holds an exact reviewed CurseForge dependency plan inside one Agent lifetime. Authorizations
/// are short-lived, single-use, server-bound, and never persist provider-controlled download data.
/// </summary>
public sealed class CurseForgeManagedContentPlanAuthorizationRegistry
{
    internal static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    private const int DefaultCapacity = 64;
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime;
    private readonly int capacity;
    private readonly Dictionary<Guid, Authorization> authorizations = [];
    private readonly Dictionary<PlanKey, Guid> authorizationsByKey = [];

    public CurseForgeManagedContentPlanAuthorizationRegistry(
        TimeProvider? clock = null,
        TimeSpan? lifetime = null,
        int capacity = DefaultCapacity)
    {
        this.clock = clock ?? TimeProvider.System;
        this.lifetime = lifetime ?? DefaultLifetime;
        this.capacity = capacity;
        if (this.lifetime <= TimeSpan.Zero || this.lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(
                nameof(lifetime), "Managed-content plan authorization must expire within one hour.");
        if (capacity is <= 0 or > 256)
            throw new ArgumentOutOfRangeException(
                nameof(capacity), "Managed-content plan authorization capacity must be between 1 and 256.");
    }

    public ManagedContentPlanAuthorization Register(
        Guid serverId,
        PluginProviderKind provider,
        string rootProjectId,
        string rootVersionId,
        PluginInstallPlan plan)
    {
        var key = PlanKey.Create(serverId, provider, rootProjectId, rootVersionId);
        var trustedPlan = ValidateAndClone(key, plan);
        var digestBytes = ComputeDigest(key, trustedPlan);
        Authorization authorization;

        lock (sync)
        {
            var issuedTimestamp = clock.GetTimestamp();
            RemoveExpired(issuedTimestamp);
            if (authorizationsByKey.TryGetValue(key, out var replacedId))
                Remove(replacedId);
            else if (authorizations.Count >= capacity)
                throw new InvalidOperationException(
                    "Too many unconsumed CurseForge dependency-plan reviews are active. Cancel an older review or let it expire and try again.");

            authorization = new Authorization(
                Guid.NewGuid(), key, trustedPlan, digestBytes, issuedTimestamp);
            authorizations.Add(authorization.AuthorizationId, authorization);
            authorizationsByKey.Add(key, authorization.AuthorizationId);
        }

        return authorization.PublicIdentity;
    }

    public PluginInstallPlan Consume(
        Guid serverId,
        PluginProviderKind provider,
        string rootProjectId,
        string rootVersionId,
        ManagedContentPlanAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var key = PlanKey.Create(serverId, provider, rootProjectId, rootVersionId);
        if (authorization.AuthorizationId == Guid.Empty || !IsSha256(authorization.Digest))
            throw new InvalidOperationException(
                "CurseForge dependency installation requires the exact reviewed plan authorization.");

        lock (sync)
        {
            RemoveExpired(clock.GetTimestamp());
            if (!authorizations.TryGetValue(authorization.AuthorizationId, out var stored))
                throw new InvalidOperationException(
                    "The exact CurseForge dependency-plan authorization is missing, expired, replaced, or was already used. Review the plan again.");
            if (stored.Key != key || !DigestMatches(stored.DigestBytes, authorization.Digest))
                throw new InvalidOperationException(
                    "The CurseForge dependency request no longer matches its exact reviewed server, release, and plan. Review the plan again.");

            var result = Clone(stored.Plan);
            if (!Remove(stored.AuthorizationId))
                throw new InvalidOperationException(
                    "The exact CurseForge dependency-plan authorization was already consumed.");
            return result;
        }
    }

    /// <summary>
    /// Revokes one exact outstanding review. Cancellation and selection replacement may safely
    /// race this idempotent operation.
    /// </summary>
    public bool Revoke(Guid authorizationId)
    {
        if (authorizationId == Guid.Empty) return false;
        lock (sync)
        {
            RemoveExpired(clock.GetTimestamp());
            return Remove(authorizationId);
        }
    }

    /// <summary>
    /// Revokes the current review for one exact selection without affecting another server or
    /// another root release.
    /// </summary>
    public bool Revoke(
        Guid serverId,
        PluginProviderKind provider,
        string rootProjectId,
        string rootVersionId)
    {
        var key = PlanKey.Create(serverId, provider, rootProjectId, rootVersionId);
        lock (sync)
        {
            RemoveExpired(clock.GetTimestamp());
            return authorizationsByKey.TryGetValue(key, out var authorizationId) && Remove(authorizationId);
        }
    }

    internal int OutstandingCount
    {
        get
        {
            lock (sync)
            {
                RemoveExpired(clock.GetTimestamp());
                return authorizations.Count;
            }
        }
    }

    private bool Remove(Guid authorizationId)
    {
        if (!authorizations.Remove(authorizationId, out var removed)) return false;
        if (authorizationsByKey.TryGetValue(removed.Key, out var current) && current == authorizationId)
            authorizationsByKey.Remove(removed.Key);
        return true;
    }

    private void RemoveExpired(long nowTimestamp)
    {
        foreach (var authorizationId in authorizations
                     .Where(pair => clock.GetElapsedTime(pair.Value.IssuedTimestamp, nowTimestamp) >= lifetime)
                     .Select(pair => pair.Key)
                     .ToArray())
            Remove(authorizationId);
    }

    private static PluginInstallPlan ValidateAndClone(PlanKey key, PluginInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanInstall || plan.Releases.Count > 64)
            throw new InvalidDataException(
                "Only a complete bounded CurseForge dependency plan can be authorized.");

        var releases = new PluginRelease[plan.Releases.Count];
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        var destinationFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < releases.Length; index++)
        {
            var release = plan.Releases[index] ?? throw new InvalidDataException(
                "The CurseForge dependency plan contains an empty release.");
            if (release.Provider != PluginProviderKind.CurseForge || release.Kind != ManagedAddonKind.Mod ||
                !PositiveProviderId(release.ProjectId) || !PositiveProviderId(release.VersionId) ||
                !projectIds.Add(release.ProjectId) ||
                !Bounded(release.VersionName, 256) || !Bounded(release.MinecraftVersion, 80, required: true) ||
                !Bounded(release.Loader, 40, required: true) || !Bounded(release.ReleaseChannel, 40, required: true) ||
                !Bounded(release.ServerSide, 40, required: true) || !Bounded(release.ClientSide, 40, required: true) ||
                !Bounded(release.ClientRequirement, 40, required: true) ||
                !ApprovedUrl(release.DownloadUrl) || !SafeJarName(release.FileName) ||
                release.SizeBytes is <= 0 or > JarInventoryService.MaximumJarBytes ||
                !IsSha1(release.Sha1) || release.Sha512.Length != 0 || release.Dependencies.Count > 128)
                throw new InvalidDataException(
                    "The CurseForge dependency plan contains incomplete or contradictory provider evidence.");
            if (!destinationFileNames.Add(release.FileName))
                throw new InvalidDataException(
                    "The CurseForge dependency plan assigns the same destination JAR filename to multiple releases.");

            var dependencies = new PluginDependency[release.Dependencies.Count];
            for (var dependencyIndex = 0; dependencyIndex < dependencies.Length; dependencyIndex++)
            {
                var dependency = release.Dependencies[dependencyIndex] ?? throw new InvalidDataException(
                    "The CurseForge dependency plan contains an empty dependency.");
                if (!PositiveProviderId(dependency.ProjectId) || dependency.VersionId.Length != 0 ||
                    dependency.FileName.Length != 0 || !SupportedRelation(dependency.Type))
                    throw new InvalidDataException(
                        "The CurseForge dependency plan contains an unsupported dependency identity.");
                dependencies[dependencyIndex] = dependency with { };
            }
            releases[index] = release with { Dependencies = dependencies };
        }

        var root = releases[^1];
        if (root.Provider != key.Provider || !root.ProjectId.Equals(key.RootProjectId, StringComparison.Ordinal) ||
            !root.VersionId.Equals(key.RootVersionId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The final CurseForge dependency-plan release does not match the reviewed root release.");

        return new PluginInstallPlan { Releases = releases, Problems = [], Authorization = null };
    }

    private static PluginInstallPlan Clone(PluginInstallPlan plan) => new()
    {
        Releases = plan.Releases.Select(release => release with
        {
            Dependencies = release.Dependencies.Select(dependency => dependency with { }).ToArray()
        }).ToArray(),
        Problems = plan.Problems.ToArray(),
        Authorization = null
    };

    private static byte[] ComputeDigest(PlanKey key, PluginInstallPlan plan)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "ChunkPilot.CurseForge.ManagedContentPlan.v1");
        Span<byte> guid = stackalloc byte[16];
        key.ServerId.TryWriteBytes(guid);
        Append(hash, guid);
        Append(hash, (int)key.Provider);
        Append(hash, key.RootProjectId);
        Append(hash, key.RootVersionId);
        Append(hash, plan.Releases.Count);
        foreach (var release in plan.Releases)
        {
            Append(hash, (int)release.Kind);
            Append(hash, (int)release.Provider);
            Append(hash, release.ProjectId);
            Append(hash, release.VersionId);
            Append(hash, release.VersionName);
            Append(hash, release.MinecraftVersion);
            Append(hash, release.Loader);
            Append(hash, release.ReleaseChannel);
            Append(hash, release.PublishedAt.UtcDateTime.Ticks);
            Append(hash, release.DownloadUrl);
            Append(hash, release.FileName);
            Append(hash, release.SizeBytes);
            Append(hash, release.Sha1);
            Append(hash, release.Sha512);
            Append(hash, release.ServerSide);
            Append(hash, release.ClientSide);
            Append(hash, release.ClientRequirement);
            Append(hash, release.Dependencies.Count);
            foreach (var dependency in release.Dependencies)
            {
                Append(hash, dependency.ProjectId);
                Append(hash, dependency.VersionId);
                Append(hash, dependency.FileName);
                Append(hash, dependency.Type);
            }
        }
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Append(hash, value.Length);
        hash.AppendData(value);
    }

    private static bool DigestMatches(byte[] expected, string supplied)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromHexString(supplied);
        }
        catch (FormatException)
        {
            return false;
        }
        try
        {
            return decoded.Length == SHA256.HashSizeInBytes &&
                   CryptographicOperations.FixedTimeEquals(expected, decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static bool ApprovedUrl(string value) =>
        value.Length <= 2_048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        CurseForgeApiClient.IsApprovedDownloadUri(uri);

    private static bool SafeJarName(string value) =>
        value.Length is > 4 and <= 180 && value.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(value).Equals(value, StringComparison.Ordinal) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool PositiveProviderId(string value) =>
        value.Length is > 0 and <= 80 && long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0 &&
        parsed.ToString(System.Globalization.CultureInfo.InvariantCulture).Equals(value, StringComparison.Ordinal);

    private static bool IsSha1(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Bounded(string value, int maximum, bool required = false) =>
        value.Length <= maximum && (!required || !string.IsNullOrWhiteSpace(value));
    private static bool SupportedRelation(string value) => value is
        "required" or "optional" or "embedded" or "tool" or "incompatible";

    private sealed record Authorization(
        Guid AuthorizationId,
        PlanKey Key,
        PluginInstallPlan Plan,
        byte[] DigestBytes,
        long IssuedTimestamp)
    {
        public ManagedContentPlanAuthorization PublicIdentity => new()
        {
            AuthorizationId = AuthorizationId,
            Digest = Convert.ToHexString(DigestBytes)
        };
    }

    private sealed record PlanKey(
        Guid ServerId,
        PluginProviderKind Provider,
        string RootProjectId,
        string RootVersionId)
    {
        public static PlanKey Create(
            Guid serverId,
            PluginProviderKind provider,
            string rootProjectId,
            string rootVersionId)
        {
            if (serverId == Guid.Empty || provider != PluginProviderKind.CurseForge ||
                !PositiveProviderId(rootProjectId) || !PositiveProviderId(rootVersionId))
                throw new ArgumentException(
                    "An exact server and CurseForge root project/file identity are required.");
            return new PlanKey(serverId, provider, rootProjectId, rootVersionId);
        }
    }
}
