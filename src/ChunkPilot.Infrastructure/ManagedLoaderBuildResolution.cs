using System.Net;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class ManagedLoaderCatalogService
{
    private readonly SemaphoreSlim focusedBuildGate = new(1, 1);
    private const int MaximumChecksumBytes = 4_096;

    /// <summary>Permission to request metadata verification, never permission to create a server.</summary>
    public static bool CanResolveIntegrity(ManagedLoaderBuild build)
    {
        if (build.Platform is not (ManagedLoaderPlatform.Forge or ManagedLoaderPlatform.NeoForge) ||
            build.RequiredJavaMajor is null or < 8 || build.HasProviderIntegrity)
            return false;
        try
        {
            _ = OfficialInstallerUri(build);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves integrity for one inventoried installer without fetching every historical sidecar.
    /// A caller supplies identities only; the exact official Maven URI is reconstructed natively.
    /// </summary>
    public async Task<ManagedLoaderBuildCatalog> ResolveBuildAsync(
        ManagedLoaderPlatform platform,
        string minecraftVersion,
        string loaderVersion,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (platform is not (ManagedLoaderPlatform.Forge or ManagedLoaderPlatform.NeoForge))
            throw new ArgumentException("Focused installer verification is available for Forge and NeoForge.", nameof(platform));
        ValidateVersion(minecraftVersion);
        ValidateVersion(loaderVersion);
        await focusedBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await GetBuildsAsync(platform, minecraftVersion, forceRefresh, cancellationToken)
                .ConfigureAwait(false);
            var selected = FindBuild(catalog, platform, minecraftVersion, loaderVersion);
            if (selected is null && catalog.IsFromCache && !forceRefresh)
            {
                catalog = await GetBuildsAsync(platform, minecraftVersion, true, cancellationToken)
                    .ConfigureAwait(false);
                selected = FindBuild(catalog, platform, minecraftVersion, loaderVersion);
            }
            if (selected is null)
                throw new ArgumentException("The selected loader is not in the official catalog for this exact Minecraft version.", nameof(loaderVersion));

            var artifact = OfficialInstallerUri(selected);
            if (selected.HasProviderIntegrity && !forceRefresh) return catalog;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            ManagedLoaderBuild resolved;
            try
            {
                var sha256 = await ReadFocusedChecksumAsync(artifact + ".sha256", 64, deadline.Token)
                    .ConfigureAwait(false);
                var sha1 = sha256.Length == 0
                    ? await ReadFocusedChecksumAsync(artifact + ".sha1", 40, deadline.Token).ConfigureAwait(false)
                    : "";
                resolved = selected with
                {
                    ArtifactSha256 = sha256,
                    ArtifactSha1 = sha1,
                    SupportReason = sha256.Length > 0 || sha1.Length > 0
                        ? "This exact official installer has a provider checksum. Runtime compatibility is a separate check."
                        : "The official source publishes no readable checksum for this exact installer; creation remains unavailable."
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                 InvalidDataException or OperationCanceledException)
            {
                // A failed metadata refresh cannot invent integrity or erase an earlier verified hash.
                return catalog with
                {
                    IsStale = true,
                    UnavailableDetail = "The selected installer's official checksum could not be verified. " +
                        (exception is OperationCanceledException
                            ? "The verification request timed out."
                            : SecretRedactor.Redact(exception.Message))
                };
            }

            var updated = catalog with
            {
                Builds = catalog.Builds.Select(build => ReferenceEquals(build, selected) ? resolved : build).ToArray(),
                UnavailableDetail = resolved.HasProviderIntegrity ? "" : resolved.SupportReason
            };
            WriteCache(BuildCachePath(platform, minecraftVersion), updated);
            return updated;
        }
        finally
        {
            focusedBuildGate.Release();
        }
    }

    private static ManagedLoaderBuild? FindBuild(
        ManagedLoaderBuildCatalog catalog, ManagedLoaderPlatform platform, string minecraftVersion, string loaderVersion) =>
        catalog.Builds.SingleOrDefault(build => build.Platform == platform &&
            build.MinecraftVersion.Equals(minecraftVersion, StringComparison.Ordinal) &&
            build.LoaderVersion.Equals(loaderVersion, StringComparison.Ordinal));

    private static string OfficialInstallerUri(ManagedLoaderBuild build)
    {
        ValidateVersion(build.InstallerVersion);
        string url;
        if (build.Platform == ManagedLoaderPlatform.Forge &&
            string.Equals(MapForgeMinecraftVersion(build.InstallerVersion), build.MinecraftVersion, StringComparison.Ordinal) &&
            ForgeLoaderVersion(build.InstallerVersion, build.MinecraftVersion).Equals(build.LoaderVersion, StringComparison.Ordinal))
        {
            var coordinate = Uri.EscapeDataString(build.InstallerVersion);
            url = $"{ForgeMavenRoot}/{coordinate}/forge-{coordinate}-installer.jar";
        }
        else if (build.Platform == ManagedLoaderPlatform.NeoForge &&
                 build.InstallerVersion.Equals(build.LoaderVersion, StringComparison.Ordinal) &&
                 string.Equals(MapNeoForgeMinecraftVersion(build.LoaderVersion), build.MinecraftVersion, StringComparison.Ordinal))
        {
            var coordinate = Uri.EscapeDataString(build.InstallerVersion);
            url = $"{NeoForgeMavenRoot}/{coordinate}/neoforge-{coordinate}-installer.jar";
        }
        else
        {
            throw new InvalidDataException("The saved loader identity does not match its official Minecraft installer coordinate. Refresh the catalog.");
        }
        if (!url.Equals(build.ArtifactUrl, StringComparison.Ordinal))
            throw new InvalidDataException("The saved installer URL does not match its exact official Maven coordinate. Refresh the catalog.");
        return url;
    }

    private async Task<string> ReadFocusedChecksumAsync(string url, int hashLength, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return "";
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final &&
            !final.AbsoluteUri.Equals(url, StringComparison.Ordinal))
            throw new InvalidDataException("The official checksum request redirected away from its exact Maven coordinate.");
        if (response.Content.Headers.ContentLength is > MaximumChecksumBytes)
            throw new InvalidDataException("The official checksum response exceeded its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[MaximumChecksumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumChecksumBytes)
            throw new InvalidDataException("The official checksum response exceeded its size limit.");
        return NormalizeHash(Encoding.UTF8.GetString(bytes, 0, count), hashLength);
    }
}
