using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

#pragma warning disable CA1861 // Inline fixture arrays keep each synthetic provider response locally readable.

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeProviderTests
{
    private const string Sha1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Cdn = "https://mediafilez.forgecdn.net/files/222/fixture-server.zip";

    [Fact]
    public async Task Installed_identity_hydrates_only_the_exact_record_without_mutating_the_saved_source()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/123" => Json(ProjectObject()),
            "/v1/mods/123/files/111" => Json(ClientFile(111, 123, 222)),
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeUpdateProvider(api);
        var source = new UpdateSource
        {
            Provider = UpdateProvider.CurseForge, ProjectId = "123", InstalledVersionId = "111"
        };
        var hydrated = await provider.GetInstalledIdentityAsync(source);
        Assert.Equal("Fixture Pack", hydrated.ProjectName);
        Assert.Equal("Fixture 1.0", hydrated.InstalledVersionName);
        Assert.Empty(source.ProjectName);
        Assert.Empty(source.InstalledVersionName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exact_author_linked_additional_server_pack_does_not_require_optional_server_flag_or_reverse_id(bool reversePresent)
    {
        var client = JsonNode.Parse(ClientFile(111, 123, 0))!;
        client["data"]!["serverPackFileId"] = null;
        client["data"]!["alternateFileId"] = 222;
        var server = JsonNode.Parse(ServerFile(222, 123, Cdn, "fixture-serverpack.zip"))!;
        server["data"]!["isServerPack"] = false;
        if (reversePresent) server["data"]!["parentProjectFileId"] = 111;
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/123" => Json(ProjectObject()),
            "/v1/mods/123/files/111" => Json(client.ToJsonString()),
            "/v1/mods/123/files/222" => Json(server.ToJsonString()),
            _ => throw new InvalidOperationException("No unrelated inventory or nearby ID may be requested.")
        });
        using var provider = Provider(handler);
        var item = await provider.ResolveProjectAsync("123", "111");
        var release = Assert.Single(item!.Versions);
        Assert.Equal("111", release.ClientFileId);
        Assert.Equal("222", release.ServerPackFileId);
        Assert.Equal(CurseForgeInstallRoute.OfficialServerPack, release.CurseForgeInstallRoute);
        Assert.True(release.HasServerPackage);
        Assert.False(release.CanGenerateServerCandidate);
    }

    [Fact]
    public async Task Additional_server_file_attached_to_another_client_is_rejected()
    {
        var client = JsonNode.Parse(ClientFile(111, 123, 0))!;
        client["data"]!["alternateFileId"] = 222;
        var server = JsonNode.Parse(ServerFile(222, 123, Cdn, "fixture-serverpack.zip"))!;
        server["data"]!["parentProjectFileId"] = 999;
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/123" => Json(ProjectObject()),
            "/v1/mods/123/files/111" => Json(client.ToJsonString()),
            "/v1/mods/123/files/222" => Json(server.ToJsonString()),
            _ => throw new InvalidOperationException()
        });
        using var provider = Provider(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolveProjectAsync("123", "111"));
    }

    [Fact]
    public async Task Unavailable_linked_official_pack_never_silently_becomes_generated()
    {
        var server = JsonNode.Parse(ServerFile(222, 123, Cdn, "fixture-serverpack.zip"))!;
        server["data"]!["isAvailable"] = false;
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/123" => Json(ProjectObject()),
            "/v1/mods/123/files/111" => Json(ClientFile(111, 123, 222)),
            "/v1/mods/123/files/222" => Json(server.ToJsonString()),
            _ => throw new InvalidOperationException()
        });
        using var provider = Provider(handler);
        var item = await provider.ResolveProjectAsync("123", "111");
        var release = Assert.Single(item!.Versions);
        Assert.Equal(CurseForgeInstallRoute.Unavailable, release.CurseForgeInstallRoute);
        Assert.False(release.CanGenerateServerCandidate);
        Assert.False(release.HasServerPackage);
    }

    [Fact]
    public async Task Resource_pack_exact_file_does_not_require_loader_tags_but_never_enters_mod_browser()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/55" => Json("""{"data":{"id":55,"gameId":432,"classId":12,"isAvailable":true,"allowModDistribution":true}}"""),
            "/v1/mods/55/files/333" => Json("""{"data":{"id":333,"modId":55,"fileName":"resource.zip","isAvailable":true,"gameVersions":["1.21.1"],"fileLength":123,"downloadUrl":"https://mediafilez.forgecdn.net/files/333/resource.zip","hashes":[{"algo":1,"value":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}}"""),
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        var mods = new CurseForgePluginProvider(api);
        Assert.Null(await mods.ResolveReleaseAsync("55", "1.21.1", "NeoForge", "333"));
        var packFile = await mods.ResolvePackFileAsync("55", "1.21.1", "NeoForge", "333", true);
        Assert.NotNull(packFile);
        Assert.Equal(CurseForgeGeneratedContentKind.ResourcePack, packFile.ContentKind);
        Assert.Equal("333", packFile.Release.VersionId);
        Assert.Null(await mods.ResolvePackFileAsync("55", "1.20.1", "NeoForge", "333", true));
    }

    [Fact]
    public async Task Search_is_shallow_and_exact_resolution_preserves_client_and_server_file_identity()
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

        var page = await provider.BrowsePageAsync(new CatalogQuery
        {
            Provider = CatalogProvider.CurseForge,
            Search = "fixture",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            Index = 50,
            Limit = 20
        });

        var summary = Assert.Single(page.Items);
        Assert.False(summary.ServerPathChecked);
        Assert.Empty(summary.Versions);
        Assert.Equal(1, handler.Count);
        Assert.DoesNotContain(handler.Uris, uri => uri.AbsolutePath.Contains("/files/", StringComparison.Ordinal));

        var item = await provider.ResolveProjectAsync("123", "111");

        Assert.NotNull(item);
        Assert.True(item.ServerPathChecked);
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
    public async Task CurseForge_file_game_versions_establish_loader_family_but_not_an_exact_loader_version()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/123" => Json(ProjectObject()),
            "/v1/mods/123/files/111" => Json(ClientFile(111, 123, 222)),
            "/v1/mods/123/files/222" => Json(ServerFile(222, 123, Cdn, "fixture-server.zip")),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var provider = Provider(handler);

        var item = await provider.ResolveProjectAsync("123", "111");

        Assert.NotNull(item);
        var release = Assert.Single(item.Versions);
        Assert.Equal("NeoForge", release.Loader);
        Assert.Empty(release.LoaderVersion);
        Assert.Equal(CatalogReleasePreflightState.Required, release.CreationPreflightState);
        Assert.Contains("exact client manifest", release.CreationPreflightDetail,
            StringComparison.OrdinalIgnoreCase);
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
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/123" => Json(ProjectObject()),
            "/v1/mods/123/files/111" => Json(ClientFile(111, 123, 222)),
            "/v1/mods/123/files/222" => Json(ServerFile(222, 999, Cdn, "fixture-server.zip")),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var provider = Provider(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolveProjectAsync("123", "111"));
    }

    [Theory]
    [InlineData("distribution-false")]
    [InlineData("distribution-null")]
    [InlineData("distribution-missing")]
    [InlineData("unavailable")]
    [InlineData("availability-null")]
    [InlineData("availability-missing")]
    public async Task Distribution_unconfirmed_restricted_or_unavailable_projects_never_become_results(string state)
    {
        var restricted = state switch
        {
            "distribution-false" => SearchProject().Replace("\"allowModDistribution\":true",
                "\"allowModDistribution\":false", StringComparison.Ordinal),
            "distribution-null" => SearchProject().Replace("\"allowModDistribution\":true",
                "\"allowModDistribution\":null", StringComparison.Ordinal),
            "distribution-missing" => SearchProject().Replace(",\"allowModDistribution\":true",
                "", StringComparison.Ordinal),
            "unavailable" => SearchProject().Replace("\"isAvailable\":true",
                "\"isAvailable\":false", StringComparison.Ordinal),
            "availability-null" => SearchProject().Replace("\"isAvailable\":true",
                "\"isAvailable\":null", StringComparison.Ordinal),
            "availability-missing" => SearchProject().Replace(",\"isAvailable\":true",
                "", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        var handler = new Handler(_ => Json(restricted));
        using var provider = Provider(handler);

        Assert.Empty(await provider.BrowseAsync(new CatalogQuery { Provider = CatalogProvider.CurseForge }));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("missing")]
    public async Task Search_summary_does_not_inspect_per_file_availability(string state)
    {
        var response = JsonNode.Parse(SearchProject())!.AsObject();
        var file = response["data"]!.AsArray()[0]!["latestFiles"]!.AsArray()[0]!.AsObject();
        if (state == "missing") file.Remove("isAvailable");
        else file["isAvailable"] = state == "false" ? false : null;
        var handler = new Handler(_ => Json(response.ToJsonString()));
        using var provider = Provider(handler);

        var item = Assert.Single(await provider.BrowseAsync(
            new CatalogQuery { Provider = CatalogProvider.CurseForge }));

        Assert.Empty(item.Versions);
        Assert.False(item.ServerPathChecked);
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
    public async Task Pagination_uses_the_provider_cursor_even_when_distribution_policy_underfills_the_page()
    {
        var body = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { id = 900, slug = "blocked", name = "Blocked", isAvailable = true, allowModDistribution = false },
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
            },
            pagination = new { index = 0, pageSize = 2, resultCount = 2, totalCount = 4 }
        });
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/files/222", StringComparison.Ordinal)
            ? Json(ServerFile(222, 123, Cdn, "fixture-server.zip"))
            : Json(body));
        using var provider = Provider(handler);

        var page = await provider.BrowsePageAsync(new CatalogQuery
        {
            Provider = CatalogProvider.CurseForge,
            Limit = 2
        });

        Assert.Single(page.Items);
        Assert.Equal(2, page.NextIndex);
        Assert.True(page.HasMore);
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(1, handler.Count);

        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-cf-pagination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new GuidedCatalogService(new AppDataPaths(root), [provider]);
            var result = await service.BrowseDetailedAsync(new CatalogQuery
            {
                Provider = CatalogProvider.CurseForge,
                Limit = 2
            });
            Assert.Equal(2, result.NextIndex);
            Assert.True(result.HasMore);
            Assert.Equal(4, result.TotalCount);
            Assert.Single(result.Items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Pagination_advances_three_raw_pages_without_duplicates_or_file_requests()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("/v1/mods/search", request.RequestUri!.AbsolutePath);
            var index = int.Parse(request.RequestUri.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2))
                .Single(part => part[0].Equals("index", StringComparison.Ordinal))[1]);
            var data = Enumerable.Range(index, 50).Select(value => new
            {
                id = 10_000 + value,
                slug = $"pack-{value}",
                name = $"Pack {value:D3}",
                summary = "Fixture pack",
                isAvailable = true,
                allowModDistribution = true,
                authors = new[] { new { name = "Fixture" } },
                categories = Array.Empty<object>(),
                downloadCount = 150 - value,
                dateModified = "2026-08-01T00:00:00Z"
            });
            return Json(JsonSerializer.Serialize(new
            {
                data,
                pagination = new { index, pageSize = 50, resultCount = 50, totalCount = 150 }
            }));
        });
        using var provider = Provider(handler);
        var query = new CatalogQuery { Provider = CatalogProvider.CurseForge, Limit = 50 };
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < 150;)
        {
            var page = await provider.BrowsePageAsync(query with { Index = index });
            Assert.Equal(150, page.TotalCount);
            Assert.Equal(index < 100, page.HasMore);
            Assert.Equal(50, page.Items.Count);
            Assert.All(page.Items, item => Assert.False(item.ServerPathChecked));
            foreach (var item in page.Items) Assert.True(ids.Add(item.ProjectId));
            index = page.NextIndex;
        }

        Assert.Equal(150, ids.Count);
        Assert.Equal(3, handler.Count);
        Assert.DoesNotContain(handler.Uris, uri => uri.AbsolutePath.Contains("/files", StringComparison.Ordinal));
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
        return new CurseForgeApiClient(secrets, handler);
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
