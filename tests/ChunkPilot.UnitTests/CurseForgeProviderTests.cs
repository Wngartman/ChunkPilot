using System.Net;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

#pragma warning disable CA1861 // Inline fixture arrays keep each synthetic provider response locally readable.

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeProviderTests
{
    private const string Sha1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Cdn = "https://mediafilez.forgecdn.net/files/222/fixture-server.zip";

    [Fact]
    public async Task Search_preserves_client_and_official_server_file_identity_with_exact_filters_and_pagination()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/files/222", StringComparison.Ordinal)
            ? Json(ServerFile(222, 123, Cdn, "fixture-server.zip"))
            : Json(SearchProject()));
        using var provider = Provider(handler);

        var item = Assert.Single(await provider.BrowseAsync(new CatalogQuery
        {
            Provider = CatalogProvider.CurseForge,
            Search = "fixture",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            Index = 50,
            Limit = 20
        }));

        var release = Assert.Single(item.Versions);
        Assert.Equal("111", release.VersionId);
        Assert.Equal("111", release.ClientFileId);
        Assert.Equal("222", release.ServerPackFileId);
        Assert.Equal(Cdn, release.DownloadUrl);
        Assert.Equal(Sha1, release.Sha1);
        Assert.True(release.HasServerPackage);
        Assert.Contains(handler.Uris, uri => uri.Query.Contains("index=50", StringComparison.Ordinal));
        Assert.Contains(handler.Uris, uri => uri.Query.Contains("gameVersion=1.21.1", StringComparison.Ordinal));
        Assert.Contains(handler.Uris, uri => uri.Query.Contains("modLoaderType=6", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slug_and_exact_file_link_resolution_verifies_project_and_keeps_exact_release()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/search" => Json(SearchProject()),
            "/v1/mods/123" => Json(ProjectObject()),
            "/v1/mods/123/files/111" => Json(ClientFile(111, 123, 222)),
            "/v1/mods/123/files/222" => Json(ServerFile(222, 123, Cdn, "fixture-server.zip")),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var provider = Provider(handler);

        var item = await provider.ResolveProjectAsync("fixture-pack", "111");

        Assert.NotNull(item);
        Assert.Equal("fixture-pack", item.Slug);
        var release = Assert.Single(item.Versions);
        Assert.Equal("111", release.VersionId);
        Assert.Equal("222", release.ServerPackFileId);
        Assert.Contains(handler.Uris, uri => uri.Query.Contains("slug=fixture-pack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Contradictory_server_pack_relationship_is_blocked()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/files/222", StringComparison.Ordinal)
            ? Json(ServerFile(222, 999, Cdn, "fixture-server.zip"))
            : Json(SearchProject()));
        using var provider = Provider(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.BrowseAsync(new CatalogQuery
        {
            Provider = CatalogProvider.CurseForge,
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge"
        }));
    }

    [Fact]
    public async Task Distribution_restricted_or_unavailable_projects_never_become_results()
    {
        var restricted = SearchProject().Replace("\"allowModDistribution\":true",
            "\"allowModDistribution\":false", StringComparison.Ordinal);
        var handler = new Handler(_ => Json(restricted));
        using var provider = Provider(handler);

        Assert.Empty(await provider.BrowseAsync(new CatalogQuery { Provider = CatalogProvider.CurseForge }));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Category_filter_is_resolved_through_official_inventory_and_sent_as_exact_id()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/categories" => Json("""{"data":[{"id":612,"name":"Technology","slug":"technology"}]}"""),
            "/v1/mods/search" => Json(SearchProject()),
            "/v1/mods/123/files/222" => Json(ServerFile(222, 123, Cdn, "fixture-server.zip")),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var provider = Provider(handler);

        _ = await provider.BrowseAsync(new CatalogQuery
        {
            Provider = CatalogProvider.CurseForge,
            Category = "technology",
            Limit = 20
        });

        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == "/v1/categories" &&
                                            uri.Query.Contains("classId=4471", StringComparison.Ordinal));
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath == "/v1/mods/search" &&
                                            uri.Query.Contains("categoryId=612", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CurseForge_api_data_is_never_written_to_the_persistent_catalog_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-cf-no-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/files/222", StringComparison.Ordinal)
                ? Json(ServerFile(222, 123, Cdn, "fixture-server.zip"))
                : Json(SearchProject()));
            using var provider = Provider(handler);
            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
            var service = new GuidedCatalogService(paths, [provider]);
            var query = new CatalogQuery { Provider = CatalogProvider.CurseForge, Search = "fixture" };

            var result = await service.BrowseDetailedAsync(query);
            var cache = await service.BrowseCacheAsync(query);

            Assert.Equal(CatalogLoadState.Ready, result.State);
            Assert.Equal(CatalogLoadState.Empty, cache.State);
            Assert.Contains("not stored", cache.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(paths.CatalogCache) &&
                         Directory.EnumerateFiles(paths.CatalogCache, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Mod_provider_resolves_exact_file_and_typed_dependency_relations_without_claiming_server_side()
    {
        var modUrl = "https://mediafilez.forgecdn.net/files/333/fixture-mod.jar";
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/search" => Json("""{"data":[{"id":55,"slug":"fixture-mod","name":"Fixture Mod","summary":"Test","isAvailable":true,"allowModDistribution":true,"authors":[{"name":"Author"}],"downloadCount":9}]}"""),
            "/v1/mods/55" => Json("""{"data":{"id":55,"gameId":432,"classId":6,"isAvailable":true,"allowModDistribution":true}}"""),
            "/v1/mods/55/files/333" => Json(JsonSerializer.Serialize(new
            {
                data = new
                {
                    id = 333, modId = 55, fileName = "fixture-mod.jar", displayName = "Fixture Mod 1.0",
                    fileDate = "2026-08-01T00:00:00Z", releaseType = 1, isAvailable = true,
                    gameVersions = new[] { "1.21.1", "NeoForge" }, downloadUrl = modUrl, fileLength = 12,
                    hashes = new[] { new { algo = 1, value = Sha1 } },
                    dependencies = new[]
                    {
                        new { modId = 77, relationType = 3 }, new { modId = 88, relationType = 2 },
                        new { modId = 99, relationType = 5 }
                    }
                }
            })),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var api = Api(handler);
        var provider = new CurseForgePluginProvider(api);

        var project = Assert.Single(await provider.SearchAsync(new PluginCatalogQuery
        {
            Kind = ManagedAddonKind.Mod,
            Search = "fixture",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge"
        }));
        var release = await provider.ResolveReleaseAsync("55", "1.21.1", "NeoForge", "333");

        Assert.Equal("unknown", project.ServerSide);
        Assert.NotNull(release);
        Assert.Equal(Sha1, release.Sha1);
        Assert.Contains(release.Dependencies, dependency => dependency.ProjectId == "77" && dependency.Type == "required");
        Assert.Contains(release.Dependencies, dependency => dependency.ProjectId == "88" && dependency.Type == "optional");
        Assert.Contains(release.Dependencies, dependency => dependency.ProjectId == "99" && dependency.Type == "incompatible");
    }

    private static CurseForgeCatalogProvider Provider(Handler handler) =>
        new(Api(handler));

    private static CurseForgeApiClient Api(Handler handler)
    {
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-approved-key");
        return new CurseForgeApiClient(secrets, new HttpClient(handler));
    }

    private static string SearchProject() => JsonSerializer.Serialize(new
    {
        data = new[]
        {
            new
            {
                id = 123, slug = "fixture-pack", name = "Fixture Pack", summary = "A fixture.",
                isAvailable = true, allowModDistribution = true, authors = new[] { new { name = "Author" } },
                categories = Array.Empty<object>(), downloadCount = 10, dateModified = "2026-08-01T00:00:00Z",
                latestFiles = new[]
                {
                    new
                    {
                        id = 111, modId = 123, fileName = "client.zip", displayName = "Fixture 1.0",
                        fileDate = "2026-08-01T00:00:00Z", releaseType = 1, isAvailable = true,
                        gameVersions = new[] { "1.21.1", "NeoForge" }, serverPackFileId = 222,
                        downloadUrl = "https://mediafilez.forgecdn.net/files/111/client.zip", fileLength = 10,
                        hashes = new[] { new { algo = 1, value = Sha1 } }
                    }
                }
            }
        }
    });

    private static string ProjectObject() => JsonSerializer.Serialize(new
    {
        data = new
        {
            id = 123, slug = "fixture-pack", name = "Fixture Pack", summary = "A fixture.",
            isAvailable = true, allowModDistribution = true, authors = new[] { new { name = "Author" } },
            categories = Array.Empty<object>(), downloadCount = 10, dateModified = "2026-08-01T00:00:00Z"
        }
    });

    private static string ClientFile(long id, long projectId, long serverId) => JsonSerializer.Serialize(new
    {
        data = new
        {
            id, modId = projectId, fileName = "client.zip", displayName = "Fixture 1.0",
            fileDate = "2026-08-01T00:00:00Z", releaseType = 1, isAvailable = true,
            gameVersions = new[] { "1.21.1", "NeoForge" }, serverPackFileId = serverId,
            downloadUrl = "https://mediafilez.forgecdn.net/files/111/client.zip", fileLength = 10,
            hashes = new[] { new { algo = 1, value = Sha1 } }
        }
    });

    private static string ServerFile(long id, long projectId, string url, string name) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                id, modId = projectId, fileName = name, displayName = "Server pack",
                fileDate = "2026-08-01T00:00:00Z", releaseType = 1, isAvailable = true,
                gameVersions = new[] { "1.21.1", "NeoForge" }, downloadUrl = url, fileLength = 12,
                hashes = new[] { new { algo = 1, value = Sha1 } }
            }
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public void SetSecret(string name, string value) => values[name] = value;
        public string? GetSecret(string name) => values.GetValueOrDefault(name);
        public bool Contains(string name) => values.ContainsKey(name);
        public void Delete(string name) => values.Remove(name);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        private readonly List<Uri> uris = [];
        private int count;
        public int Count => count;
        public IReadOnlyList<Uri> Uris => uris;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            lock (uris) uris.Add(request.RequestUri!);
            return Task.FromResult(response(request));
        }
    }
}
#pragma warning restore CA1861
