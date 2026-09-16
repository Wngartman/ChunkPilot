using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ManagedLoaderBuildResolutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-focused-loader-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ManagedLoaderPlatform.Forge)]
    [InlineData(ManagedLoaderPlatform.NeoForge)]
    public async Task Focused_verification_resolves_one_old_build_and_preserves_the_rest(ManagedLoaderPlatform platform)
    {
        var handler = Inventory(platform);
        var service = Service(handler);
        var catalog = await service.GetBuildsAsync(platform, "1.21.1", forceRefresh: true);
        var selected = catalog.Builds.Single(build => build.LoaderVersion == Loader(platform, 1));
        Assert.False(selected.IsSelectable);
        Assert.True(ManagedLoaderCatalogService.CanResolveIntegrity(selected));
        var requests = handler.Urls.Count;

        var resolved = await service.ResolveBuildAsync(platform, "1.21.1", selected.LoaderVersion);

        var exact = resolved.Builds.Single(build => build.LoaderVersion == selected.LoaderVersion);
        Assert.True(exact.IsSelectable);
        Assert.False(ManagedLoaderCatalogService.CanResolveIntegrity(exact));
        Assert.Equal(requests + 1, handler.Urls.Count);
        Assert.Equal(selected.ArtifactUrl + ".sha256", handler.Urls.Last());
        Assert.Equal(catalog.Builds.Where(build => build.LoaderVersion != selected.LoaderVersion)
                .Select(build => (build.LoaderVersion, build.InstallerVersion, build.ArtifactUrl, build.ArtifactSha256)),
            resolved.Builds.Where(build => build.LoaderVersion != selected.LoaderVersion)
                .Select(build => (build.LoaderVersion, build.InstallerVersion, build.ArtifactUrl, build.ArtifactSha256)));
        var reread = await service.ResolveBuildAsync(platform, "1.21.1", selected.LoaderVersion);
        Assert.True(reread.IsFromCache);
        Assert.Equal(requests + 1, handler.Urls.Count);
    }

    [Fact]
    public async Task Cold_focused_request_discovers_identity_before_fetching_its_checksum()
    {
        var handler = Inventory(ManagedLoaderPlatform.NeoForge);
        var catalog = await Service(handler).ResolveBuildAsync(ManagedLoaderPlatform.NeoForge, "1.21.1", "21.1.1");

        Assert.True(catalog.Builds.Single(build => build.LoaderVersion == "21.1.1").IsSelectable);
        Assert.Equal(ManagedLoaderCatalogService.NeoForgeVersionsApiUrl, handler.Urls.First());
        Assert.Equal(50, handler.Urls.Count); // One inventory, 48 newest checksums, one exact old checksum.
    }

    [Fact]
    public async Task Expired_cache_keeps_exact_identity_and_fetches_only_the_focused_checksum()
    {
        var build = Build(ManagedLoaderPlatform.Forge);
        WriteCache(build, DateTimeOffset.UtcNow.AddDays(-1));
        var handler = new Handler(_ => Text(new string('a', 64)));

        var catalog = await Service(handler).ResolveBuildAsync(build.Platform, build.MinecraftVersion, build.LoaderVersion);

        Assert.True(catalog.IsFromCache);
        Assert.True(catalog.IsStale);
        Assert.True(Assert.Single(catalog.Builds).IsSelectable);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Historical_SHA1_sidecar_is_used_only_when_SHA256_is_missing()
    {
        var build = Build(ManagedLoaderPlatform.Forge);
        WriteCache(build);
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Text(new string('b', 40) + "  forge-installer.jar\n"));

        var result = await Service(handler).ResolveBuildAsync(build.Platform, build.MinecraftVersion, build.LoaderVersion);

        var exact = Assert.Single(result.Builds);
        Assert.Equal(new string('b', 40), exact.ArtifactSha1);
        Assert.Empty(exact.ArtifactSha256);
        Assert.True(exact.IsSelectable);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("oversized")]
    [InlineData("redirect")]
    [InlineData("rate-limit")]
    public async Task Missing_or_untrusted_checksum_never_enables_creation(string failure)
    {
        var build = Build(ManagedLoaderPlatform.NeoForge);
        WriteCache(build);
        var handler = new Handler(_ => failure switch
        {
            "oversized" => Text(new string('a', 4_097)),
            "redirect" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('a', 64)),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://foreign.invalid/file.sha256")
            },
            "rate-limit" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await Service(handler).ResolveBuildAsync(build.Platform, build.MinecraftVersion, build.LoaderVersion);

        Assert.False(Assert.Single(result.Builds).IsSelectable);
        Assert.NotEmpty(result.UnavailableDetail);
        Assert.InRange(handler.Urls.Count, 1, 2);
    }

    [Fact]
    public async Task Cross_version_identity_and_arbitrary_cached_URL_are_rejected_before_checksum_IO()
    {
        var build = Build(ManagedLoaderPlatform.NeoForge);
        var handler = new Handler(_ => throw new InvalidOperationException("Must not contact a checksum URL."));
        var badUrl = build with { ArtifactUrl = "https://foreign.invalid/installer.jar" };
        WriteCache(badUrl);
        var service = Service(handler);
        Assert.False(ManagedLoaderCatalogService.CanResolveIntegrity(badUrl));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ResolveBuildAsync(build.Platform, build.MinecraftVersion, build.LoaderVersion));
        var wrongGame = build with { MinecraftVersion = "1.20.4" };
        WriteCache(wrongGame);
        Assert.False(ManagedLoaderCatalogService.CanResolveIntegrity(wrongGame));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ResolveBuildAsync(wrongGame.Platform, wrongGame.MinecraftVersion, wrongGame.LoaderVersion));
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task Unsafe_or_unsupported_request_is_rejected_before_catalog_IO()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Unexpected network request."));
        var service = Service(handler);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveBuildAsync(ManagedLoaderPlatform.Fabric, "1.21.1", "0.19.3"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveBuildAsync(ManagedLoaderPlatform.Forge, "../1.21.1", "52.1.1"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveBuildAsync(ManagedLoaderPlatform.Forge, "1.21.1", "https://foreign.invalid"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ResolveBuildAsync(ManagedLoaderPlatform.Forge, "1.21.1", "52.1.1", cancellationToken: cancellation.Token));
        Assert.Empty(handler.Urls);
    }

    private ManagedLoaderCatalogService Service(Handler handler) =>
        new(new AppDataPaths(root), new HttpClient(handler));

    private static string Loader(ManagedLoaderPlatform platform, int index) =>
        platform == ManagedLoaderPlatform.Forge ? $"52.1.{index}" : $"21.1.{index}";

    private static ManagedLoaderBuild Build(ManagedLoaderPlatform platform)
    {
        var loader = Loader(platform, 1);
        var coordinate = platform == ManagedLoaderPlatform.Forge ? "1.21.1-" + loader : loader;
        var artifact = platform == ManagedLoaderPlatform.Forge
            ? $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-installer.jar"
            : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{coordinate}/neoforge-{coordinate}-installer.jar";
        return new ManagedLoaderBuild
        {
            Platform = platform, MinecraftVersion = "1.21.1", LoaderVersion = loader,
            InstallerVersion = coordinate, ArtifactUrl = artifact, RequiredJavaMajor = 21
        };
    }

    private void WriteCache(ManagedLoaderBuild build, DateTimeOffset? retrieved = null)
    {
        var paths = new AppDataPaths(root);
        Directory.CreateDirectory(paths.CatalogCache);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(build.MinecraftVersion)))[..20].ToLowerInvariant();
        var path = Path.Combine(paths.CatalogCache, $"{build.Platform.ToString().ToLowerInvariant()}-builds-{hash}.json");
        var catalog = new ManagedLoaderBuildCatalog
        {
            Platform = build.Platform, MinecraftVersion = build.MinecraftVersion, Builds = [build],
            RetrievedUtc = retrieved ?? DateTimeOffset.UtcNow, ProviderAvailable = true
        };
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 2, catalog }, ProtocolJson.Options));
    }

    private static Handler Inventory(ManagedLoaderPlatform platform) => new(request =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url == ManagedLoaderCatalogService.ForgeMetadataUrl)
            return Text("<metadata><versioning><versions>" + string.Concat(Enumerable.Range(1, 50)
                .Select(index => $"<version>1.21.1-{Loader(platform, index)}</version>")) +
                "</versions></versioning></metadata>");
        if (url == ManagedLoaderCatalogService.ForgePromotionsUrl) return Text("{\"promos\":{}}");
        if (url == ManagedLoaderCatalogService.NeoForgeVersionsApiUrl)
            return Text(JsonSerializer.Serialize(new { versions = Enumerable.Range(1, 50).Select(index => Loader(platform, index)) }));
        return Text(new string('a', 64));
    });

    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8)
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
