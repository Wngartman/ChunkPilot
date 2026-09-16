using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

/// <summary>Native metadata inspection with explicit bounded archive-only checks; never emits credentials/URLs.</summary>
internal static class CurseForgeMetadataInspectionCommand
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var verifyNativeSetup = arguments.Contains("--verify-native-credential-setup", StringComparer.Ordinal);
        if (verifyNativeSetup && (arguments.Contains("--verify-generated-plan", StringComparer.Ordinal) ||
                                  arguments.Contains("--verify-official-archives", StringComparer.Ordinal)))
        {
            Console.Error.WriteLine("Native credential verification cannot be combined with payload download options.");
            return 64;
        }
        var project = ReadId(arguments, "--project");
        var fileId = ReadId(arguments, "--file");
        var ledgerIndex = Array.IndexOf(arguments, "--ledger");
        var ledgerPath = ledgerIndex >= 0 && ledgerIndex + 1 < arguments.Length
            ? arguments[ledgerIndex + 1]
            : Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "modpack-controls", "curseforge-payload-ledger.json");
        var budget = new CurseForgePayloadBudget(ledgerPath);
        var operationId = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-cf-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
            paths.EnsureCreated();
            var secrets = new DpapiSecretStore(paths);
            using var api = new CurseForgeApiClient(secrets);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if (arguments.Contains("--require-application-service", StringComparer.Ordinal))
            {
                if (verifyNativeSetup)
                    throw new ArgumentException("A no-key service check cannot also verify personal credential setup.");
                CurseForgeCredentialEnvironment.ClearFromCurrentProcess();
                await KeylessCurseForgeCertification.VerifyAsync(api, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                var provisioned = await new CurseForgeCredentialProvisioner(secrets)
                    .ProvisionFromEnvironmentAsync(api, timeout.Token).ConfigureAwait(false);
                if (!provisioned.Imported) return 69;
            }
            if (verifyNativeSetup)
            {
                // A separate client makes the production setup service perform fresh official API
                // validation; it cannot reuse the provisioner's authentication response cache.
                using var setupApi = new CurseForgeApiClient(secrets);
                var verification = await VerifyNativeCredentialSetupAsync(secrets, setupApi, timeout.Token)
                    .ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = verification.Success ? "VERIFIED_NATIVE_CREDENTIAL_SERVICE_ONLY" : "BLOCKED_NATIVE_CREDENTIAL_SERVICE",
                    verification.SessionChecks, verification.StoredCredentialReadable,
                    validationOnly = true, payloadBytes = 0, javaLaunched = false,
                    worldCreated = false, nativeDialogExercised = false
                }));
                if (!verification.Success) return 2;
            }
            using var projectDocument = await api.GetJsonAsync($"/v1/mods/{project}", timeout.Token)
                .ConfigureAwait(false);
            var projectData = projectDocument.RootElement.GetProperty("data");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                projectId = project, gameId = Value(projectData, "gameId"),
                classId = Value(projectData, "classId"),
                available = Value(projectData, "isAvailable"),
                distributionAllowed = Value(projectData, "allowModDistribution")
            }));
            using var exact = await api.GetJsonAsync($"/v1/mods/{project}/files/{fileId}", timeout.Token)
                .ConfigureAwait(false);
            WriteFile(exact.RootElement.GetProperty("data"), "exact");
            if (arguments.Contains("--verify-generated-plan", StringComparer.Ordinal))
            {
                using var provider = new CurseForgeCatalogProvider(api);
                var item = await provider.ResolveProjectAsync(project, fileId, timeout.Token).ConfigureAwait(false);
                var release = item?.Versions.SingleOrDefault(version => version.ClientFileId == fileId)
                    ?? throw new InvalidDataException("The exact client release could not be resolved.");
                if (release.CurseForgeInstallRoute != CurseForgeInstallRoute.GeneratedCandidate ||
                    release.ClientSizeBytes is not > 0 or > 256L * 1024 * 1024)
                    throw new InvalidDataException("This exact selection is not a bounded generated-candidate route.");
                await budget.ReserveAsync(operationId, "client-manifest", project, fileId,
                    release.ClientSizeBytes.Value, release.ClientSha1, timeout.Token).ConfigureAwait(false);
                var preflight = await new CurseForgeModpackPreflightService(paths, api).InspectAsync(
                    new CurseForgeModpackPreflightRequest(Guid.NewGuid(), project, fileId, ""), timeout.Token).ConfigureAwait(false);
                await budget.CompleteAsync(operationId, "client-manifest", preflight.ClientSizeBytes,
                    preflight.ClientSha256, "VerifiedClientArchive", true, timeout.Token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = preflight.State == CatalogReleasePreflightState.Ready ? "VERIFIED_PLAN_ONLY" : "BLOCKED_PLAN",
                    projectId = project, clientFileId = fileId, downloadedBytes = release.ClientSizeBytes,
                    preflight.MinecraftVersion, preflight.Loader, preflight.LoaderVersion,
                    preflight.Detail, requiredFiles = preflight.GeneratedPackPlan?.RequiredFiles.Count,
                    resourcePacks = preflight.GeneratedPackPlan?.RequiredFiles.Count(file => file.ContentKind == CurseForgeGeneratedContentKind.ResourcePack),
                    preflight.GeneratedPackPlan?.TotalResolvedBytes, preflight.GeneratedPackPlan?.Digest,
                    javaLaunched = false, worldCreated = false
                }));
                return preflight.State == CatalogReleasePreflightState.Ready ? 0 : 2;
            }
            if (arguments.Contains("--verify-official-archives", StringComparer.Ordinal))
            {
                using var provider = new CurseForgeCatalogProvider(api);
                var item = await provider.ResolveProjectAsync(project, fileId, timeout.Token).ConfigureAwait(false);
                var release = item?.Versions.SingleOrDefault(version => version.ClientFileId == fileId)
                    ?? throw new InvalidDataException("The exact client release could not be resolved.");
                if (!release.HasServerPackage || release.SizeBytes is not > 0 || release.ClientSizeBytes is not > 0 ||
                    checked(release.SizeBytes.Value + release.ClientSizeBytes.Value) > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("The exact official release is unavailable or exceeds the 2 GiB download budget.");
                await budget.ReserveBatchAsync(
                [
                    new(operationId, "client-manifest", project, fileId, release.ClientSizeBytes.Value, release.ClientSha1),
                    new(operationId, "official-archive", project, release.ServerPackFileId, release.SizeBytes.Value, release.Sha1)
                ], timeout.Token).ConfigureAwait(false);
                var preflight = await new CurseForgeModpackPreflightService(paths, api).InspectAsync(
                    new CurseForgeModpackPreflightRequest(Guid.NewGuid(), project, fileId, release.ServerPackFileId), timeout.Token)
                    .ConfigureAwait(false);
                await budget.CompleteAsync(operationId, "client-manifest", preflight.ClientSizeBytes,
                    preflight.ClientSha256, "VerifiedClientArchive", true, timeout.Token).ConfigureAwait(false);
                if (preflight.State != CatalogReleasePreflightState.Ready)
                    throw new InvalidDataException("Exact client manifest preflight failed.");
                var archive = Path.Combine(root, "official-server.zip");
                var sha256 = await VerifyDownloadAsync(api, release, archive, timeout.Token).ConfigureAwait(false);
                await budget.CompleteAsync(operationId, "official-archive", release.SizeBytes.Value,
                    sha256, "VerifiedOfficialArchive", true, timeout.Token).ConfigureAwait(false);
                var inspection = await new ServerImportInspectionService().InspectFileAsync(archive, timeout.Token)
                    .ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "VERIFIED_ARCHIVES_ONLY", projectId = project, clientFileId = fileId,
                    serverFileId = release.ServerPackFileId,
                    downloadedBytes = checked(release.SizeBytes.Value + release.ClientSizeBytes.Value),
                    preflight.MinecraftVersion, preflight.Loader, preflight.LoaderVersion,
                    serverSha256 = sha256, inspection.FileCount, inspection.ExpandedSizeBytes,
                    inspection.CanInstall, inspection.Limitation, javaLaunched = false, worldCreated = false
                }));
                return 0;
            }
            if (!arguments.Contains("--inventory", StringComparer.Ordinal)) return 0;
            // Inspect only metadata. Never select a nearby file by inventory position.
            for (var index = 0; index < 500; index += 50)
            {
                using var inventory = await api.GetJsonAsync(
                    $"/v1/mods/{project}/files?pageSize=50&index={index}", timeout.Token).ConfigureAwait(false);
                var rows = inventory.RootElement.GetProperty("data");
                foreach (var file in rows.EnumerateArray()) WriteFile(file, "inventory");
                if (rows.GetArrayLength() < 50) break;
            }
            return 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Metadata inspection failed: {exception.GetType().Name}: {SecretRedactor.Redact(exception.Message)}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    internal sealed record NativeCredentialSetupVerification(bool Success, int SessionChecks, bool StoredCredentialReadable);

    internal static async Task<NativeCredentialSetupVerification> VerifyNativeCredentialSetupAsync(
        ISecretStore secrets, CurseForgeApiClient api, CancellationToken cancellationToken)
    {
        // This method is called only with the diagnostic's disposable protected store. Never emit
        // the retrieved plaintext, protected transport value, HTTP header, or provider response.
        var protectedValue = CurseForgeCredentialTransport.ProtectForCurrentUser(
            secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName) ??
            throw new InvalidOperationException("The disposable credential could not be read."));
        secrets.Delete(CurseForgeUpdateProvider.ApiKeyName);
        if (secrets.Contains(CurseForgeUpdateProvider.ApiKeyName))
            throw new InvalidOperationException("The disposable credential was not cleared before setup verification.");
        var sessionChecks = 0;
        var sessionId = Guid.NewGuid();
        var expectedSession = sessionId;
        var result = await new CurseForgeCredentialSetupService(secrets, api).ConfigureAsync(protectedValue, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sessionId != expectedSession) throw new UnauthorizedAccessException("The diagnostic session ended.");
            sessionChecks++;
        }, cancellationToken).ConfigureAwait(false);
        var readable = secrets.Contains(CurseForgeUpdateProvider.ApiKeyName) &&
                       !string.IsNullOrEmpty(secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName));
        return new(result.Success && readable && sessionChecks == 2, sessionChecks, readable);
    }

    private static async Task<string> VerifyDownloadAsync(CurseForgeApiClient api, CatalogVersion release,
        string path, CancellationToken cancellationToken)
    {
        using var response = await api.SendDownloadAsync(new Uri(release.DownloadUrl), cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != release.SizeBytes)
            throw new InvalidDataException("The official archive changed size before verification.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
#pragma warning disable CA5350 // Provider SHA-1 is checked alongside a locally computed SHA-256.
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            copied = checked(copied + count);
            if (copied > release.SizeBytes) throw new InvalidDataException("Official archive exceeded its exact size.");
            sha1.AppendData(buffer, 0, count);
            sha256.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (copied != release.SizeBytes || !Convert.ToHexString(sha1.GetHashAndReset()).Equals(release.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The official archive did not match its exact provider integrity evidence.");
        return Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteFile(JsonElement file, string source) => Console.WriteLine(JsonSerializer.Serialize(new
    {
        source, id = Value(file, "id"), modId = Value(file, "modId"),
        parentProjectFileId = Value(file, "parentProjectFileId"),
        alternateFileId = Value(file, "alternateFileId"),
        serverPackFileId = Value(file, "serverPackFileId"),
        isServerPack = Value(file, "isServerPack"),
        fileName = Value(file, "fileName"), fileLength = Value(file, "fileLength"),
        gameVersions = file.TryGetProperty("gameVersions", out var versions) ? versions.Clone() : default,
        available = Value(file, "isAvailable")
    }));

    private static string Value(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) ? item.ToString() : "missing";

    private static string ReadId(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        if (index < 0 || index + 1 >= arguments.Length ||
            !long.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException($"{name} requires a positive exact ID.");
        return id.ToString(CultureInfo.InvariantCulture);
    }
}
