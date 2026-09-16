using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeCatalogMetadataReuseTests
{
    private const string FirstHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecondHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_linked_file_is_read_once_for_both_relationship_and_final_route(bool alternate)
    {
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(),
            "/v1/mods/123/files/111" => File(111, linked: 222, alternate: alternate),
            "/v1/mods/123/files/222" => File(222, server: true, parent: 111),
            _ => throw new InvalidOperationException("No unrelated metadata may be fetched.")
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);

        var item = await provider.ResolveProjectAsync("123", "111");

        var release = Assert.Single(item!.Versions);
        Assert.Equal(3, handler.Paths.Count);
        Assert.Single(handler.Paths, path => path == "/v1/mods/123/files/222");
        Assert.Equal("111", release.ClientFileId);
        Assert.Equal("222", release.ServerPackFileId);
        Assert.Equal(FirstHash, release.Sha1);
        Assert.Equal(CurseForgeInstallRoute.OfficialServerPack, release.CurseForgeInstallRoute);
        Assert.Equal(CatalogReleasePreflightState.Required, release.CreationPreflightState);
        Assert.Empty(release.LoaderVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inventory_metadata_is_reused_and_dedicated_server_rows_never_become_clients(bool alternate)
    {
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(),
            "/v1/mods/123/files" => Inventory(
                File(222, server: true), // No optional parent; still not a client release.
                File(111, linked: 222, alternate: alternate)),
            _ => throw new InvalidOperationException("The exact target is already present in this inventory.")
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);

        var item = await provider.ResolveProjectAsync("123", null);

        var release = Assert.Single(item!.Versions);
        Assert.Equal("111", release.ClientFileId);
        Assert.Equal("222", release.ServerPackFileId);
        Assert.Equal(CurseForgeInstallRoute.OfficialServerPack, release.CurseForgeInstallRoute);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task An_exact_server_archive_is_not_offered_as_a_generated_client_manifest()
    {
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(),
            "/v1/mods/123/files/222" => File(222, server: true),
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);
        var item = await provider.ResolveProjectAsync("123", "222");
        Assert.Empty(item!.Versions);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task A_new_operation_reads_changed_authoritative_hashes_instead_of_reusing_the_previous_snapshot()
    {
        var currentHash = FirstHash;
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(),
            "/v1/mods/123/files/111" => File(111, linked: 222, alternate: true),
            "/v1/mods/123/files/222" => File(222, server: true, hash: currentHash),
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);
        var first = await provider.ResolveProjectAsync("123", "111");
        currentHash = SecondHash;
        var second = await provider.ResolveProjectAsync("123", "111");
        Assert.Equal(FirstHash, Assert.Single(first!.Versions).Sha1);
        Assert.Equal(SecondHash, Assert.Single(second!.Versions).Sha1);
        Assert.Equal(6, handler.Paths.Count);
        Assert.Equal(2, handler.Paths.Count(path => path == "/v1/mods/123/files/222"));
    }

    [Theory]
    [InlineData("different-project")]
    [InlineData("missing-project")]
    [InlineData("different-file")]
    public async Task Exact_client_response_must_prove_both_requested_identities(string mismatch)
    {
        var wrong = File(mismatch == "different-file" ? 112 : 111);
        if (mismatch == "different-project") wrong["modId"] = 999;
        if (mismatch == "missing-project") wrong.Remove("modId");
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(),
            "/v1/mods/123/files/111" => wrong,
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolveProjectAsync("123", "111"));
        Assert.Equal(2, handler.Paths.Count);
    }

    [Theory]
    [InlineData("different-project")]
    [InlineData("different-parent")]
    [InlineData("conflicting-duplicate")]
    public async Task Reused_inventory_metadata_still_enforces_identity_and_exact_parent(string mismatch)
    {
        var server = File(222, server: true, parent: mismatch == "different-parent" ? 999 : 111);
        if (mismatch == "different-project") server["modId"] = 999;
        var inventory = Inventory(File(111, linked: 222, alternate: true), server);
        if (mismatch == "conflicting-duplicate") inventory.Add(File(222, server: true, hash: SecondHash));
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(),
            "/v1/mods/123/files" => inventory,
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ResolveProjectAsync("123", null));
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task Reused_unavailable_server_file_does_not_become_a_generated_fallback()
    {
        var server = File(222, server: true);
        server["isAvailable"] = false;
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(),
            "/v1/mods/123/files" => Inventory(File(111, linked: 222, alternate: true), server),
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);
        var item = await provider.ResolveProjectAsync("123", null);
        var release = Assert.Single(item!.Versions);
        Assert.False(release.CanGenerateServerCandidate);
        Assert.False(release.HasServerPackage);
        Assert.Equal(CurseForgeInstallRoute.Unavailable, release.CurseForgeInstallRoute);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task Changed_project_distribution_permission_is_rechecked_on_the_next_operation()
    {
        var permitted = true;
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(permitted),
            "/v1/mods/123/files/111" => File(111, linked: 222),
            "/v1/mods/123/files/222" => File(222, server: true),
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);
        Assert.NotNull(await provider.ResolveProjectAsync("123", "111"));
        permitted = false;
        Assert.Null(await provider.ResolveProjectAsync("123", "111"));
        Assert.Equal(4, handler.Paths.Count);
    }

    [Fact]
    public async Task A_failed_metadata_fetch_is_not_retained_for_a_later_operation()
    {
        var failing = true;
        var handler = new Handler(path => path switch
        {
            "/v1/mods/123" => Project(),
            "/v1/mods/123/files/111" => File(111, linked: 222, alternate: true),
            "/v1/mods/123/files/222" => failing
                ? throw new HttpRequestException("Synthetic metadata failure.") : File(222, server: true),
            _ => throw new InvalidOperationException()
        });
        using var api = Api(handler);
        using var provider = new CurseForgeCatalogProvider(api);
        await Assert.ThrowsAsync<CurseForgeApiException>(() => provider.ResolveProjectAsync("123", "111"));
        failing = false;
        var item = await provider.ResolveProjectAsync("123", "111");
        Assert.True(Assert.Single(item!.Versions).HasServerPackage);
        Assert.Equal(6, handler.Paths.Count);
    }

    private static JsonObject Project(bool permitted = true) => new()
    {
        ["id"] = 123, ["name"] = "Fixture pack", ["slug"] = "fixture-pack",
        ["isAvailable"] = true, ["allowModDistribution"] = permitted
    };

    private static JsonObject File(int id, int linked = 0, bool alternate = false, bool server = false,
        int parent = 0, string hash = FirstHash) => new()
    {
        ["id"] = id, ["modId"] = 123, ["fileName"] = server ? "fixture-serverpack.zip" : "fixture-client.zip",
        ["displayName"] = server ? "Fixture server pack" : "Fixture client release",
        ["isServerPack"] = server, ["isAvailable"] = true, ["fileLength"] = 123,
        ["fileDate"] = "2026-09-15T00:00:00Z", ["releaseType"] = 1,
        ["serverPackFileId"] = alternate ? 0 : linked, ["alternateFileId"] = alternate ? linked : 0,
        ["parentProjectFileId"] = parent,
        ["downloadUrl"] = $"https://mediafilez.forgecdn.net/files/1/{id}/fixture.zip",
        ["gameVersions"] = new JsonArray("1.21.1", "NeoForge"),
        ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = hash })
    };

    private static JsonArray Inventory(params JsonObject[] files) => new(files.Select(file => (JsonNode)file).ToArray());

    private static CurseForgeApiClient Api(Handler handler) => new(new FixtureSecrets(), handler);

    private sealed class FixtureSecrets : ISecretStore
    {
        public string? GetSecret(string name) => "fixture-approved-key";
        public bool Contains(string name) => true;
        public void SetSecret(string name, string value) => throw new NotSupportedException();
        public void Delete(string name) => throw new NotSupportedException();
    }

    private sealed class Handler(Func<string, JsonNode> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            // Clone so a later synthetic operation can return the same authoritative fixture afresh.
            var body = new JsonObject { ["data"] = respond(path).DeepClone() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }
}
