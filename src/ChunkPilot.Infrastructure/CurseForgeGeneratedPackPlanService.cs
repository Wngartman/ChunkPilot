using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Resolves a generated CurseForge candidate once during preflight, then validates and seals the
/// complete provider file inventory. Materialization consumes this plan without consulting mutable
/// provider metadata again.
/// </summary>
public sealed class CurseForgeGeneratedPackPlanService
{
    public const int MaximumResolvedFiles = 2_000;
    public const int MaximumRelationshipEvidence = 10_000;
    public const long MaximumResolvedBytes = 8L * 1024 * 1024 * 1024;

    private readonly CurseForgePluginProvider mods;

    public CurseForgeGeneratedPackPlanService(
        CurseForgeApiClient api,
        CurseForgePluginProvider? mods = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        this.mods = mods ?? new CurseForgePluginProvider(api);
    }

    public async Task<CurseForgeGeneratedPackPlan> ResolveAsync(
        CurseForgePackManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var resolved = new Dictionary<string, ResolvedFile>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var optional = new List<CurseForgeGeneratedOptionalExclusion>();
        var incompatible = new List<(string Owner, string Target)>();
        var relationshipCount = 0;

        foreach (var entry in manifest.Files)
        {
            var projectId = CanonicalId(entry.ProjectId);
            var fileId = CanonicalId(entry.FileId);
            if (!entry.Required)
            {
                AddOptional(new CurseForgeGeneratedOptionalExclusion
                {
                    ProjectId = projectId,
                    FileId = fileId,
                    Relation = CurseForgeGeneratedOptionalRelation.ManifestOptional
                });
                continue;
            }

            await VisitAsync(projectId, fileId, new CurseForgeGeneratedFileEvidence
            {
                Relation = CurseForgeGeneratedFileRelation.ManifestRequired,
                RequestedFileId = fileId
            }).ConfigureAwait(false);
        }

        foreach (var relation in incompatible)
            if (resolved.ContainsKey(relation.Target))
                throw new InvalidDataException(
                    $"CurseForge project {relation.Owner} declares project {relation.Target} incompatible with this candidate.");

        optional.RemoveAll(exclusion =>
            resolved.TryGetValue(exclusion.ProjectId, out var included) &&
            (exclusion.FileId.Length == 0 ||
             included.Release.VersionId.Equals(exclusion.FileId, StringComparison.Ordinal)));

        var raw = new CurseForgeGeneratedPackPlan
        {
            MinecraftVersion = manifest.MinecraftVersion,
            Loader = manifest.Loader.ToString(),
            LoaderVersion = manifest.LoaderVersion,
            RequiredFiles = resolved.Values.Select(value => value.ToPlan()).ToArray(),
            OptionalExclusions = optional,
            TotalResolvedBytes = resolved.Values.Aggregate(0L,
                (total, value) => checked(total + value.Release.SizeBytes))
        };
        return NormalizeValidateAndClone(raw, manifest, requireDigest: false);

        async Task VisitAsync(
            string projectId,
            string? requestedFileId,
            CurseForgeGeneratedFileEvidence evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            relationshipCount = checked(relationshipCount + 1);
            if (relationshipCount > MaximumRelationshipEvidence)
                throw new InvalidDataException(
                    $"The CurseForge dependency graph exceeds {MaximumRelationshipEvidence:N0} exact relationships.");

            if (resolved.TryGetValue(projectId, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(requestedFileId) &&
                    !existing.Release.VersionId.Equals(requestedFileId, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"CurseForge project {projectId} requires contradictory exact file identities.");
                existing.AddEvidence(evidence);
                return;
            }
            if (resolved.Count >= MaximumResolvedFiles)
                throw new InvalidDataException(
                    $"The CurseForge dependency graph exceeds {MaximumResolvedFiles:N0} exact files.");
            if (!visiting.Add(projectId))
                return;
            try
            {
                var release = await mods.ResolveReleaseAsync(projectId, manifest.MinecraftVersion,
                    manifest.Loader.ToString(), requestedFileId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Required CurseForge project/file {projectId}/{requestedFileId ?? "compatible release"} is unavailable, restricted, or incompatible.");
                if (!release.ProjectId.Equals(projectId, StringComparison.Ordinal) ||
                    requestedFileId is not null &&
                    !release.VersionId.Equals(requestedFileId, StringComparison.Ordinal))
                    throw new InvalidDataException("CurseForge returned contradictory exact dependency identity.");

                var selected = new ResolvedFile(release);
                selected.AddEvidence(evidence);
                resolved.Add(projectId, selected);

                foreach (var dependency in release.Dependencies)
                {
                    switch (dependency.Type.ToLowerInvariant())
                    {
                        case "required":
                            await VisitAsync(dependency.ProjectId,
                                string.IsNullOrWhiteSpace(dependency.VersionId) ? null : dependency.VersionId,
                                new CurseForgeGeneratedFileEvidence
                                {
                                    Relation = CurseForgeGeneratedFileRelation.RequiredDependency,
                                    DeclaredByProjectId = projectId,
                                    RequestedFileId = dependency.VersionId
                                }).ConfigureAwait(false);
                            break;
                        case "optional":
                            AddOptional(new CurseForgeGeneratedOptionalExclusion
                            {
                                ProjectId = dependency.ProjectId,
                                FileId = dependency.VersionId,
                                Relation = CurseForgeGeneratedOptionalRelation.OptionalDependency,
                                DeclaredByProjectId = projectId
                            });
                            break;
                        case "incompatible":
                            incompatible.Add((projectId, dependency.ProjectId));
                            break;
                        case "embedded":
                        case "include":
                        case "tool":
                            break;
                        default:
                            throw new InvalidDataException(
                                $"CurseForge returned an unknown dependency relationship for project {projectId}.");
                    }
                }
            }
            finally
            {
                visiting.Remove(projectId);
            }
        }

        void AddOptional(CurseForgeGeneratedOptionalExclusion exclusion)
        {
            relationshipCount = checked(relationshipCount + 1);
            if (relationshipCount > MaximumRelationshipEvidence)
                throw new InvalidDataException(
                    $"The CurseForge dependency graph exceeds {MaximumRelationshipEvidence:N0} exact relationships.");
            optional.Add(exclusion);
        }
    }

    /// <summary>Validates the digest and returns collections that cannot be mutated by the caller.</summary>
    public static CurseForgeGeneratedPackPlan ValidateAndClone(
        CurseForgeGeneratedPackPlan plan,
        CurseForgePackManifest? manifest = null) =>
        NormalizeValidateAndClone(plan, manifest, requireDigest: true);

    internal static CurseForgeGeneratedPackPlan Seal(
        CurseForgeGeneratedPackPlan plan,
        CurseForgePackManifest? manifest = null) =>
        NormalizeValidateAndClone(plan, manifest, requireDigest: false);

    private static CurseForgeGeneratedPackPlan NormalizeValidateAndClone(
        CurseForgeGeneratedPackPlan plan,
        CurseForgePackManifest? manifest,
        bool requireDigest)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != CurseForgeGeneratedPackPlan.CurrentSchemaVersion ||
            InvalidText(plan.MinecraftVersion, 80) || InvalidText(plan.Loader, 40) ||
            InvalidText(plan.LoaderVersion, 120))
            throw new InvalidDataException("The generated CurseForge plan has an invalid platform identity.");
        if (plan.RequiredFiles.Count is <= 0 or > MaximumResolvedFiles)
            throw new InvalidDataException(
                $"The generated CurseForge plan must contain from 1 through {MaximumResolvedFiles:N0} exact files.");
        if (plan.OptionalExclusions.Count > MaximumRelationshipEvidence)
            throw new InvalidDataException("The generated CurseForge optional review exceeds its bounded relationship limit.");

        var projects = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedFiles = new List<CurseForgeGeneratedFilePlan>(plan.RequiredFiles.Count);
        long total = 0;
        var totalRelationships = 0;
        foreach (var file in plan.RequiredFiles)
        {
            ArgumentNullException.ThrowIfNull(file);
            var projectId = RequireCanonicalId(file.ProjectId, "project");
            var fileId = RequireCanonicalId(file.FileId, "file");
            if (!projects.Add(projectId))
                throw new InvalidDataException("The generated CurseForge plan selects multiple files for one project.");
            if (!SafeJarName(file.FileName) || !names.Add(file.FileName))
                throw new InvalidDataException("The generated CurseForge plan contains an unsafe or colliding JAR filename.");
            if (!Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out var uri) ||
                !CurseForgeApiClient.IsApprovedDownloadUri(uri) || file.DownloadUrl.Length > 2_048)
                throw new InvalidDataException("The generated CurseForge plan contains an unapproved download destination.");
            if (file.SizeBytes is <= 0 or > JarInventoryService.MaximumJarBytes || !Sha1(file.ProviderSha1))
                throw new InvalidDataException("The generated CurseForge plan contains incomplete provider integrity evidence.");
            if (file.RequiredBy.Count == 0)
                throw new InvalidDataException("Every generated CurseForge file requires exact inclusion evidence.");

            var evidenceKeys = new HashSet<string>(StringComparer.Ordinal);
            var normalizedEvidence = new List<CurseForgeGeneratedFileEvidence>(file.RequiredBy.Count);
            foreach (var evidence in file.RequiredBy)
            {
                ArgumentNullException.ThrowIfNull(evidence);
                totalRelationships = checked(totalRelationships + 1);
                if (totalRelationships > MaximumRelationshipEvidence)
                    throw new InvalidDataException(
                        $"The generated CurseForge plan exceeds {MaximumRelationshipEvidence:N0} exact relationships.");
                CurseForgeGeneratedFileEvidence normalized;
                switch (evidence.Relation)
                {
                    case CurseForgeGeneratedFileRelation.ManifestRequired:
                        if (evidence.DeclaredByProjectId.Length > 0 ||
                            !evidence.RequestedFileId.Equals(fileId, StringComparison.Ordinal))
                            throw new InvalidDataException("Manifest-required CurseForge evidence is contradictory.");
                        normalized = evidence with { DeclaredByProjectId = "", RequestedFileId = fileId };
                        break;
                    case CurseForgeGeneratedFileRelation.RequiredDependency:
                        var owner = RequireCanonicalId(evidence.DeclaredByProjectId, "dependency owner");
                        var requested = evidence.RequestedFileId.Length == 0
                            ? ""
                            : RequireCanonicalId(evidence.RequestedFileId, "dependency file");
                        if (requested.Length > 0 && !requested.Equals(fileId, StringComparison.Ordinal))
                            throw new InvalidDataException("Required CurseForge dependency evidence selects a different file.");
                        normalized = evidence with
                        {
                            DeclaredByProjectId = owner,
                            RequestedFileId = requested
                        };
                        break;
                    default:
                        throw new InvalidDataException("The generated CurseForge plan contains unknown inclusion evidence.");
                }
                var key = $"{(int)normalized.Relation}|{normalized.DeclaredByProjectId}|{normalized.RequestedFileId}";
                if (!evidenceKeys.Add(key)) continue;
                normalizedEvidence.Add(normalized);
            }

            total = checked(total + file.SizeBytes);
            if (total > MaximumResolvedBytes)
                throw new InvalidDataException("The generated CurseForge candidate exceeds the 8 GiB resolved-file limit.");
            normalizedFiles.Add(file with
            {
                ProjectId = projectId,
                FileId = fileId,
                DownloadUrl = uri.AbsoluteUri,
                ProviderSha1 = file.ProviderSha1.ToLowerInvariant(),
                RequiredBy = ReadOnly(normalizedEvidence
                    .OrderBy(value => value.Relation)
                    .ThenBy(value => NumericId(value.DeclaredByProjectId))
                    .ThenBy(value => NumericId(value.RequestedFileId))
                    .ToArray())
            });
        }
        if (plan.TotalResolvedBytes != total)
            throw new InvalidDataException("The generated CurseForge plan total does not match its exact file inventory.");
        foreach (var file in normalizedFiles)
            foreach (var evidence in file.RequiredBy.Where(value =>
                         value.Relation == CurseForgeGeneratedFileRelation.RequiredDependency))
                if (!projects.Contains(evidence.DeclaredByProjectId))
                    throw new InvalidDataException(
                        "Required CurseForge dependency evidence names a project outside the exact plan.");

        var optionalKeys = new HashSet<string>(StringComparer.Ordinal);
        var normalizedOptional = new List<CurseForgeGeneratedOptionalExclusion>(plan.OptionalExclusions.Count);
        foreach (var exclusion in plan.OptionalExclusions)
        {
            ArgumentNullException.ThrowIfNull(exclusion);
            totalRelationships = checked(totalRelationships + 1);
            if (totalRelationships > MaximumRelationshipEvidence)
                throw new InvalidDataException(
                    $"The generated CurseForge plan exceeds {MaximumRelationshipEvidence:N0} exact relationships.");
            var projectId = RequireCanonicalId(exclusion.ProjectId, "optional project");
            var fileId = exclusion.FileId.Length == 0
                ? ""
                : RequireCanonicalId(exclusion.FileId, "optional file");
            string owner;
            switch (exclusion.Relation)
            {
                case CurseForgeGeneratedOptionalRelation.ManifestOptional:
                    if (fileId.Length == 0 || exclusion.DeclaredByProjectId.Length > 0)
                        throw new InvalidDataException("Manifest-optional CurseForge evidence is contradictory.");
                    owner = "";
                    break;
                case CurseForgeGeneratedOptionalRelation.OptionalDependency:
                    owner = RequireCanonicalId(exclusion.DeclaredByProjectId, "optional dependency owner");
                    break;
                default:
                    throw new InvalidDataException("The generated CurseForge plan contains unknown optional evidence.");
            }
            var normalized = exclusion with
            {
                ProjectId = projectId,
                FileId = fileId,
                DeclaredByProjectId = owner
            };
            var key = $"{(int)normalized.Relation}|{projectId}|{fileId}|{owner}";
            if (!optionalKeys.Add(key)) continue;
            if (normalized.Relation == CurseForgeGeneratedOptionalRelation.OptionalDependency &&
                !projects.Contains(normalized.DeclaredByProjectId))
                throw new InvalidDataException(
                    "Optional CurseForge dependency evidence names a project outside the exact plan.");
            normalizedOptional.Add(normalized);
        }

        normalizedFiles = normalizedFiles
            .OrderBy(file => NumericId(file.ProjectId))
            .ThenBy(file => NumericId(file.FileId))
            .ToList();
        normalizedOptional = normalizedOptional
            .OrderBy(value => value.Relation)
            .ThenBy(value => NumericId(value.ProjectId))
            .ThenBy(value => NumericId(value.FileId))
            .ThenBy(value => NumericId(value.DeclaredByProjectId))
            .ToList();
        var summary = OptionalSummary(normalizedOptional);
        if ((requireDigest || plan.OptionalReviewSummary.Length > 0) &&
            !plan.OptionalReviewSummary.Equals(summary, StringComparison.Ordinal))
            throw new InvalidDataException("The generated CurseForge optional review summary is contradictory.");

        var normalizedPlan = plan with
        {
            RequiredFiles = ReadOnly(normalizedFiles.ToArray()),
            OptionalExclusions = ReadOnly(normalizedOptional.ToArray()),
            TotalResolvedBytes = total,
            OptionalReviewSummary = summary,
            Digest = ""
        };
        ValidateAgainstManifest(normalizedPlan, manifest);
        var digest = CalculateDigest(normalizedPlan);
        if (requireDigest && !plan.Digest.Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The generated CurseForge plan digest no longer matches its exact review.");
        return normalizedPlan with { Digest = digest };
    }

    private static void ValidateAgainstManifest(
        CurseForgeGeneratedPackPlan plan,
        CurseForgePackManifest? manifest)
    {
        if (manifest is null) return;
        if (!plan.MinecraftVersion.Equals(manifest.MinecraftVersion, StringComparison.Ordinal) ||
            !plan.Loader.Equals(manifest.Loader.ToString(), StringComparison.Ordinal) ||
            !plan.LoaderVersion.Equals(manifest.LoaderVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The generated CurseForge plan platform no longer matches the client manifest.");

        var required = manifest.Files.Where(file => file.Required)
            .Select(file => $"{file.ProjectId}/{file.FileId}").Order(StringComparer.Ordinal).ToArray();
        var planned = plan.RequiredFiles
            .Where(file => file.RequiredBy.Any(evidence =>
                evidence.Relation == CurseForgeGeneratedFileRelation.ManifestRequired))
            .Select(file => $"{file.ProjectId}/{file.FileId}").Order(StringComparer.Ordinal).ToArray();
        if (!required.SequenceEqual(planned, StringComparer.Ordinal))
            throw new InvalidDataException("The generated CurseForge plan changed the manifest-required file inventory.");

        var includedIdentities = plan.RequiredFiles
            .Select(file => $"{file.ProjectId}/{file.FileId}").ToHashSet(StringComparer.Ordinal);
        var optional = manifest.Files.Where(file => !file.Required)
            .Select(file => $"{file.ProjectId}/{file.FileId}").Order(StringComparer.Ordinal).ToArray();
        optional = optional.Where(identity => !includedIdentities.Contains(identity)).ToArray();
        var reviewedOptional = plan.OptionalExclusions
            .Where(value => value.Relation == CurseForgeGeneratedOptionalRelation.ManifestOptional)
            .Select(value => $"{value.ProjectId}/{value.FileId}").Order(StringComparer.Ordinal).ToArray();
        if (!optional.SequenceEqual(reviewedOptional, StringComparer.Ordinal))
            throw new InvalidDataException("The generated CurseForge plan changed the manifest-optional review.");
    }

    private static string CalculateDigest(CurseForgeGeneratedPackPlan plan)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt(hash, plan.SchemaVersion);
        AppendString(hash, plan.MinecraftVersion);
        AppendString(hash, plan.Loader);
        AppendString(hash, plan.LoaderVersion);
        AppendLong(hash, plan.TotalResolvedBytes);
        AppendString(hash, plan.OptionalReviewSummary);
        AppendInt(hash, plan.RequiredFiles.Count);
        foreach (var file in plan.RequiredFiles)
        {
            AppendString(hash, file.ProjectId);
            AppendString(hash, file.FileId);
            AppendString(hash, file.FileName);
            AppendString(hash, file.DownloadUrl);
            AppendLong(hash, file.SizeBytes);
            AppendString(hash, file.ProviderSha1);
            AppendInt(hash, file.RequiredBy.Count);
            foreach (var evidence in file.RequiredBy)
            {
                AppendInt(hash, (int)evidence.Relation);
                AppendString(hash, evidence.DeclaredByProjectId);
                AppendString(hash, evidence.RequestedFileId);
            }
        }
        AppendInt(hash, plan.OptionalExclusions.Count);
        foreach (var exclusion in plan.OptionalExclusions)
        {
            AppendString(hash, exclusion.ProjectId);
            AppendString(hash, exclusion.FileId);
            AppendInt(hash, (int)exclusion.Relation);
            AppendString(hash, exclusion.DeclaredByProjectId);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string OptionalSummary(IReadOnlyCollection<CurseForgeGeneratedOptionalExclusion> exclusions)
    {
        if (exclusions.Count == 0)
            return "No optional entries were excluded. No client-only exclusions were inferred.";
        var manifest = exclusions.Count(value =>
            value.Relation == CurseForgeGeneratedOptionalRelation.ManifestOptional);
        var dependencies = exclusions.Count - manifest;
        var manifestLabel = manifest == 1 ? "entry" : "entries";
        var dependencyLabel = dependencies == 1 ? "relationship" : "relationships";
        return $"Excluded {manifest} manifest {manifestLabel} and {dependencies} dependency {dependencyLabel} only because CurseForge marked them optional. No client-only exclusions were inferred.";
    }

    private static bool InvalidText(string value, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl);

    private static bool SafeJarName(string value) =>
        value.Length is > 0 and <= 180 &&
        value.Equals(Path.GetFileName(value), StringComparison.Ordinal) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.EndsWith(' ') && !value.EndsWith('.') &&
        value.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    private static bool Sha1(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string RequireCanonicalId(string value, string label)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0 || !value.Equals(parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new InvalidDataException($"The generated CurseForge plan contains an invalid {label} identity.");
        return value;
    }

    private static string CanonicalId(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static long NumericId(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : long.MaxValue;

    private static ReadOnlyCollection<T> ReadOnly<T>(T[] values) => Array.AsReadOnly(values);

    private static void AppendInt(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendLong(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private sealed class ResolvedFile(PluginRelease release)
    {
        private readonly List<CurseForgeGeneratedFileEvidence> evidence = [];
        public PluginRelease Release { get; } = release;

        public void AddEvidence(CurseForgeGeneratedFileEvidence value)
        {
            if (!evidence.Contains(value)) evidence.Add(value);
        }

        public CurseForgeGeneratedFilePlan ToPlan() => new()
        {
            ProjectId = Release.ProjectId,
            FileId = Release.VersionId,
            FileName = Release.FileName,
            DownloadUrl = Release.DownloadUrl,
            SizeBytes = Release.SizeBytes,
            ProviderSha1 = Release.Sha1,
            RequiredBy = evidence.ToArray()
        };
    }
}
