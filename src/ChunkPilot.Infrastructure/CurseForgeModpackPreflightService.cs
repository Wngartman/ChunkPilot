using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public interface ICurseForgeModpackPreflightService
{
    Task<CurseForgeModpackPreflightResult> InspectAsync(
        CurseForgeModpackPreflightRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Establishes the exact loader identity of one CurseForge modpack release from its verified
/// client manifest archive. API game-version labels are discovery hints only and are never used
/// as an exact loader version.
/// </summary>
public sealed class CurseForgeModpackPreflightService : ICurseForgeModpackPreflightService
{
    private const int MinecraftGameId = 432;
    private const int ModpackClassId = 4471;
    private const string StagingPrefix = "curseforge-preflight-";

    private readonly AppDataPaths paths;
    private readonly CurseForgeApiClient api;
    private readonly CurseForgePackManifestReader manifestReader;
    private readonly CurseForgeGeneratedPackPlanService generatedPlans;
    private readonly IStorageSpaceProbe storageSpace;

    public CurseForgeModpackPreflightService(
        AppDataPaths paths,
        CurseForgeApiClient api,
        CurseForgePackManifestReader? manifestReader = null,
        CurseForgeGeneratedPackPlanService? generatedPlans = null,
        IStorageSpaceProbe? storageSpace = null)
    {
        this.paths = paths;
        this.api = api;
        this.manifestReader = manifestReader ?? new CurseForgePackManifestReader();
        this.generatedPlans = generatedPlans ?? new CurseForgeGeneratedPackPlanService(api);
        this.storageSpace = storageSpace ?? SystemStorageSpaceProbe.Instance;
    }

    public async Task<CurseForgeModpackPreflightResult> InspectAsync(
        CurseForgeModpackPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty)
            throw new ArgumentException("CurseForge preflight requires an operation identity.", nameof(request));
        if (!PositiveId(request.ProjectId) || !PositiveId(request.ClientFileId) ||
            request.ExpectedServerPackFileId.Length > 0 && !PositiveId(request.ExpectedServerPackFileId))
            throw new ArgumentException("CurseForge preflight requires exact positive project and file identities.", nameof(request));
        if (!api.HasCredential)
            throw new InvalidOperationException(
                "CurseForge is unavailable because the approved local native credential is missing.");

        var stagingParent = Path.GetFullPath(paths.Staging);
        Directory.CreateDirectory(stagingParent);
        var stagingRoot = Path.Combine(stagingParent, StagingPrefix + request.OperationId.ToString("N"));
        if (Directory.Exists(stagingRoot) || File.Exists(stagingRoot))
            throw new IOException("The CurseForge preflight staging identity is already in use.");
        Directory.CreateDirectory(stagingRoot);
        var archivePath = Path.Combine(stagingRoot, "client-manifest.zip");

        try
        {
            var file = await ResolveExactClientFileAsync(request, cancellationToken).ConfigureAwait(false);
            StorageSpaceGuard.EnsureAvailable(
                storageSpace,
                [
                    new StorageSpaceRequirement(
                        stagingRoot,
                        "CurseForge client-manifest preflight staging",
                        StorageSpaceGuard.SaturatingAdd(
                            file.SizeBytes, StorageSpaceGuard.DownloadSafetyReserveBytes))
                ]);
            var localSha256 = await DownloadAndVerifyAsync(file, archivePath, cancellationToken)
                .ConfigureAwait(false);
            CurseForgePackManifest manifest;
            try
            {
                manifest = await manifestReader.ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                return Result(request, file, localSha256, CatalogReleasePreflightState.Unsupported,
                    $"This exact client archive cannot establish a supported server loader: {exception.Message}");
            }
            catch (JsonException exception)
            {
                return Result(request, file, localSha256, CatalogReleasePreflightState.Unsupported,
                    $"This exact client archive cannot establish a supported server loader: {exception.Message}");
            }

            var requiredJava = JavaRuntimePolicy.TryRequiredMajorForMinecraft(manifest.MinecraftVersion);
            if (requiredJava is null or <= 0)
            {
                return Result(request, file, localSha256, CatalogReleasePreflightState.Unsupported,
                    "This exact client manifest uses a Minecraft version with no supported managed Java requirement.");
            }

            var serverPack = request.ExpectedServerPackFileId.Length == 0
                ? null
                : await ResolveExactServerPackAsync(
                    request.ProjectId, request.ExpectedServerPackFileId, cancellationToken).ConfigureAwait(false);
            CurseForgeGeneratedPackPlan? generatedPlan = null;
            if (serverPack is null)
            {
                try
                {
                    generatedPlan = await generatedPlans.ResolveAsync(manifest, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidDataException exception)
                {
                    return Result(request, file, localSha256, CatalogReleasePreflightState.Unsupported,
                        $"This exact client archive cannot produce a safe generated server candidate: {exception.Message}",
                        manifest, requiredJava.Value);
                }
            }
            return Result(request, file, localSha256, CatalogReleasePreflightState.Ready,
                $"Verified the exact client manifest: {manifest.Loader} {manifest.LoaderVersion} for Minecraft {manifest.MinecraftVersion}.",
                manifest, requiredJava.Value, serverPack, generatedPlan);
        }
        finally
        {
            DeleteOwnedStaging(stagingRoot, stagingParent, request.OperationId);
        }
    }

    private async Task<ExactClientFile> ResolveExactClientFileAsync(
        CurseForgeModpackPreflightRequest request,
        CancellationToken cancellationToken)
    {
        using (var projectDocument = await api.GetJsonAsync(
                   $"/v1/mods/{Uri.EscapeDataString(request.ProjectId)}", cancellationToken).ConfigureAwait(false))
        {
            var project = RequireObject(projectDocument.RootElement, "data", "project");
            if (!Text(project, "id").Equals(request.ProjectId, StringComparison.Ordinal) ||
                Number(project, "gameId") != MinecraftGameId ||
                Number(project, "classId") != ModpackClassId ||
                !True(project, "isAvailable") || !True(project, "allowModDistribution"))
                throw new InvalidDataException("The exact CurseForge project is not an available distributable Minecraft modpack.");
        }

        using var fileDocument = await api.GetJsonAsync(
            $"/v1/mods/{Uri.EscapeDataString(request.ProjectId)}/files/{Uri.EscapeDataString(request.ClientFileId)}",
            cancellationToken).ConfigureAwait(false);
        var file = RequireObject(fileDocument.RootElement, "data", "client file");
        if (!Text(file, "id").Equals(request.ClientFileId, StringComparison.Ordinal) ||
            !Text(file, "modId").Equals(request.ProjectId, StringComparison.Ordinal) ||
            !True(file, "isAvailable"))
            throw new InvalidDataException("CurseForge returned a contradictory or unavailable client file identity.");

        var serverPackFileId = Number(file, "serverPackFileId")?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (!serverPackFileId.Equals(request.ExpectedServerPackFileId, StringComparison.Ordinal))
            throw new InvalidDataException("The exact CurseForge server-pack relationship changed. Refresh the provider release before continuing.");

        var fileName = Text(file, "fileName");
        var size = Number(file, "fileLength");
        var sha1 = CurseForgeCatalogProvider.Hash(file, 1).Trim().ToLowerInvariant();
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            size is null or <= 0 or > ServerImportInspectionService.MaximumCompressedBytes ||
            sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
            throw new InvalidDataException("The exact CurseForge client file lacks a bounded ZIP identity and provider SHA-1.");

        var downloadUrl = Text(file, "downloadUrl");
        if (downloadUrl.Length == 0)
        {
            using var downloadDocument = await api.GetJsonAsync(
                $"/v1/mods/{Uri.EscapeDataString(request.ProjectId)}/files/{Uri.EscapeDataString(request.ClientFileId)}/download-url",
                cancellationToken).ConfigureAwait(false);
            if (downloadDocument.RootElement.TryGetProperty("data", out var value) &&
                value.ValueKind == JsonValueKind.String)
                downloadUrl = value.GetString() ?? "";
        }
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
            !CurseForgeApiClient.IsApprovedDownloadUri(downloadUri))
            throw new InvalidDataException("The exact CurseForge client file has no approved CDN download.");

        return new ExactClientFile(request.ProjectId, request.ClientFileId, serverPackFileId,
            downloadUri, sha1, size.Value);
    }

    private async Task<ExactServerPack> ResolveExactServerPackAsync(
        string projectId,
        string serverPackFileId,
        CancellationToken cancellationToken)
    {
        using var fileDocument = await api.GetJsonAsync(
            $"/v1/mods/{Uri.EscapeDataString(projectId)}/files/{Uri.EscapeDataString(serverPackFileId)}",
            cancellationToken).ConfigureAwait(false);
        var file = RequireObject(fileDocument.RootElement, "data", "server-pack file");
        if (!Text(file, "id").Equals(serverPackFileId, StringComparison.Ordinal) ||
            !Text(file, "modId").Equals(projectId, StringComparison.Ordinal) ||
            !True(file, "isAvailable"))
            throw new InvalidDataException("CurseForge returned a contradictory or unavailable server-pack file identity.");

        var fileName = Text(file, "fileName");
        var size = Number(file, "fileLength");
        var sha1 = CurseForgeCatalogProvider.Hash(file, 1).Trim().ToLowerInvariant();
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            size is null or <= 0 or > ServerImportInspectionService.MaximumCompressedBytes ||
            sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
            throw new InvalidDataException(
                "The exact CurseForge server-pack file lacks a bounded ZIP identity and provider SHA-1.");

        var downloadUrl = Text(file, "downloadUrl");
        if (downloadUrl.Length == 0)
        {
            using var downloadDocument = await api.GetJsonAsync(
                $"/v1/mods/{Uri.EscapeDataString(projectId)}/files/{Uri.EscapeDataString(serverPackFileId)}/download-url",
                cancellationToken).ConfigureAwait(false);
            if (downloadDocument.RootElement.TryGetProperty("data", out var value) &&
                value.ValueKind == JsonValueKind.String)
                downloadUrl = value.GetString() ?? "";
        }
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
            !CurseForgeApiClient.IsApprovedDownloadUri(downloadUri))
            throw new InvalidDataException("The exact CurseForge server-pack file has no approved CDN download.");

        return new ExactServerPack(downloadUri, sha1, size.Value);
    }

    private async Task<string> DownloadAndVerifyAsync(
        ExactClientFile file,
        string archivePath,
        CancellationToken cancellationToken)
    {
        using var response = await api.SendDownloadAsync(file.DownloadUri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } declared && declared != file.SizeBytes)
            throw new InvalidDataException("The CurseForge client archive size changed before preflight.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
#pragma warning disable CA5350 // CurseForge publishes SHA-1 as provider identity; a local SHA-256 is also computed below.
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            received = checked(received + read);
            if (received > file.SizeBytes || received > ServerImportInspectionService.MaximumCompressedBytes)
                throw new InvalidDataException("The CurseForge client archive exceeded its verified size.");
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (received != file.SizeBytes)
            throw new InvalidDataException("The CurseForge client archive ended before its verified size.");
        var actualSha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
        if (!actualSha1.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The CurseForge client archive failed provider SHA-1 verification.");
        return Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
    }

    private static CurseForgeModpackPreflightResult Result(
        CurseForgeModpackPreflightRequest request,
        ExactClientFile file,
        string localSha256,
        CatalogReleasePreflightState state,
        string detail,
        CurseForgePackManifest? manifest = null,
        int requiredJavaMajor = 0,
        ExactServerPack? serverPack = null,
        CurseForgeGeneratedPackPlan? generatedPlan = null) => new()
        {
            OperationId = request.OperationId,
            ProjectId = request.ProjectId,
            ClientFileId = request.ClientFileId,
            ServerPackFileId = file.ServerPackFileId,
            State = state,
            Detail = detail,
            MinecraftVersion = manifest?.MinecraftVersion ?? "",
            Loader = manifest?.Loader.ToString() ?? "",
            LoaderVersion = manifest?.LoaderVersion ?? "",
            RequiredJavaMajor = requiredJavaMajor,
            ClientDownloadUrl = file.DownloadUri.AbsoluteUri,
            ClientSha1 = file.Sha1,
            ClientSha256 = localSha256,
            ClientSizeBytes = file.SizeBytes,
            ServerPackDownloadUrl = serverPack?.DownloadUri.AbsoluteUri ?? "",
            ServerPackSha1 = serverPack?.Sha1 ?? "",
            ServerPackSizeBytes = serverPack?.SizeBytes,
            GeneratedPackPlan = generatedPlan
        };

    private static void DeleteOwnedStaging(string stagingRoot, string stagingParent, Guid operationId)
    {
        var expected = Path.Combine(Path.GetFullPath(stagingParent), StagingPrefix + operationId.ToString("N"));
        var actual = Path.GetFullPath(stagingRoot);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(actual)!.Equals(Path.GetFullPath(stagingParent), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CurseForge preflight refused to clean an unowned staging path.");
        if (Directory.Exists(actual)) Directory.Delete(actual, recursive: true);
    }

    private static bool PositiveId(string value) =>
        long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0;

    private static JsonElement RequireObject(JsonElement root, string property, string label)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"CurseForge returned a malformed {label} response.");
        return value;
    }

    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? result.ToString() : "";

    private static long? Number(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.Number &&
        result.TryGetInt64(out var number) ? number : null;

    private static bool True(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.True;

    private sealed record ExactClientFile(
        string ProjectId,
        string ClientFileId,
        string ServerPackFileId,
        Uri DownloadUri,
        string Sha1,
        long SizeBytes);

    private sealed record ExactServerPack(Uri DownloadUri, string Sha1, long SizeBytes);
}
