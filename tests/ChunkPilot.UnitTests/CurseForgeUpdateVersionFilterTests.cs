using System.Net;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeUpdateVersionFilterTests
{
    private const string Sha1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Historical_game_version_is_filtered_before_the_first_fifty_provider_files()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/123/files" => request.RequestUri.Query.Contains("gameVersion=1.21.1", StringComparison.Ordinal)
                ? Json(new { data = new[] { File(111, "1.21.1", "Fabric", serverId: 222) } })
                : Json(new
                {
                    data = Enumerable.Range(1000, 50).Select(id => File(id, "26.1.2", "Fabric")).ToArray(),
                    pagination = new { index = 0, pageSize = 50, resultCount = 50, totalCount = 78 }
                }),
            "/v1/mods/123/files/222" => Json(new { data = File(222, "1.21.1", "Fabric", parentId: 111) }),
            _ => throw new InvalidOperationException("Only the bounded filtered page and its exact server link are allowed.")
        });
        using var api = Api(handler);
        using var provider = new CurseForgeUpdateProvider(api);

        var release = Assert.Single(await provider.GetVersionsAsync(Source(), new UpdatePreferences()));

        Assert.Equal("111", release.VersionId);
        Assert.Equal("222", release.ProviderFileId);
        Assert.Equal("1.21.1", release.MinecraftVersion);
        Assert.Equal("Fabric", release.Loader);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("?pageSize=50&index=0&gameVersion=1.21.1", handler.Requests[0].Query);
    }

    [Fact]
    public async Task Missing_game_version_preserves_the_existing_bounded_unfiltered_fallback()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/v1/mods/123/files", request.RequestUri!.AbsolutePath);
            Assert.Equal("?pageSize=50&index=0", request.RequestUri.Query);
            return Json(new { data = new[] { File(111, "1.21.1", "Fabric") } });
        });
        using var api = Api(handler);
        using var provider = new CurseForgeUpdateProvider(api);

        var release = Assert.Single(await provider.GetVersionsAsync(
            Source() with { MinecraftVersion = "" }, new UpdatePreferences()));

        Assert.Equal("111", release.VersionId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Exact_game_version_is_uri_encoded_without_injecting_other_query_parameters()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("?pageSize=50&index=0&gameVersion=1.21.1%26index%3D50", request.RequestUri!.Query);
            return Json(new { data = Array.Empty<object>() });
        });
        using var api = Api(handler);
        using var provider = new CurseForgeUpdateProvider(api);

        Assert.Empty(await provider.GetVersionsAsync(
            Source() with { MinecraftVersion = "1.21.1&index=50" }, new UpdatePreferences()));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("1.21.5", "Fabric", 1)]
    [InlineData("1.21.1", "Forge", 1)]
    [InlineData("1.21.1", "Fabric", 2)]
    [InlineData("1.21.1", "Fabric", 3)]
    public async Task Provider_filter_never_replaces_local_game_loader_and_channel_validation(
        string minecraftVersion, string loader, int releaseType)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/v1/mods/123/files", request.RequestUri!.AbsolutePath);
            return Json(new { data = new[] { File(111, minecraftVersion, loader, releaseType, serverId: 222) } });
        });
        using var api = Api(handler);
        using var provider = new CurseForgeUpdateProvider(api);

        Assert.Empty(await provider.GetVersionsAsync(Source(), new UpdatePreferences()));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(2, true, false)]
    [InlineData(3, false, true)]
    public async Task Explicit_release_channel_preferences_are_preserved(int releaseType, bool includeBeta, bool includeAlpha)
    {
        using var handler = new Handler(_ => Json(new { data = new[] { File(111, "1.21.1", "Fabric", releaseType) } }));
        using var api = Api(handler);
        using var provider = new CurseForgeUpdateProvider(api);

        Assert.Single(await provider.GetVersionsAsync(Source(), new UpdatePreferences
        {
            IncludeBeta = includeBeta, IncludeAlpha = includeAlpha
        }));
    }

    [Fact]
    public async Task Filtered_inventory_does_not_allow_a_server_file_from_another_project()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/123/files" => Json(new { data = new[] { File(111, "1.21.1", "Fabric", serverId: 222) } }),
            "/v1/mods/123/files/222" => Json(new { data = File(222, "1.21.1", "Fabric", parentId: 111, projectId: 999) }),
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeUpdateProvider(api);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetVersionsAsync(Source(), new UpdatePreferences()));
    }

    private static UpdateSource Source() => new()
    {
        Provider = UpdateProvider.CurseForge, ProjectId = "123", MinecraftVersion = "1.21.1",
        Loader = "Fabric", InstalledVersionId = "100"
    };

    private static object File(int id, string minecraftVersion, string loader, int releaseType = 1,
        int serverId = 0, int parentId = 0, int projectId = 123) => new
        {
            id, modId = projectId, fileName = $"fixture-{id}.zip", displayName = $"Fixture {id}",
            fileDate = "2025-02-19T23:22:28Z", releaseType, isAvailable = true,
            gameVersions = new[] { minecraftVersion, loader }, serverPackFileId = serverId,
            parentProjectFileId = parentId, isServerPack = parentId > 0,
            downloadUrl = $"https://edge.forgecdn.net/files/0/{id}/fixture-{id}.zip", fileLength = 10,
            hashes = new[] { new { algo = 1, value = Sha1 } }
        };

    private static CurseForgeApiClient Api(HttpMessageHandler handler) => new(new SyntheticSecrets(), handler);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class SyntheticSecrets : ISecretStore
    {
        public bool Contains(string name) => name == CurseForgeUpdateProvider.ApiKeyName;
        public string? GetSecret(string name) => Contains(name) ? "fixture-approved-key" : null;
        public void SetSecret(string name, string value) => throw new NotSupportedException();
        public void Delete(string name) => throw new NotSupportedException();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
