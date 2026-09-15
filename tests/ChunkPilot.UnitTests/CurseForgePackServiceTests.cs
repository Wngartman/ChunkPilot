using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgePackServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unversioned_dependency_uses_later_manifest_pin_instead_of_latest(bool required)
    {
        var handler = new FixtureHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/mods/10" => Json(Project(10)),
            "/v1/mods/20" => Json(Project(20)),
            "/v1/mods/10/files/100" => Json(ApiFile(10, 100, "first.jar", [1],
                [new { modId = 20, relationType = 3 }])),
            "/v1/mods/20/files/200" => Json(ApiFile(20, 200, "pinned.jar", [2], [])),
            _ => throw new InvalidOperationException("Do not resolve today's latest when the manifest pins an exact file.")
        });
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
        using var api = new CurseForgeApiClient(secrets, handler);
        var manifest = new CurseForgePackManifest("Fixture", "1", "Fixture", "1.20.1",
            InstallSourceType.Fabric, "0.15.11", "overrides",
            [new(10, 100, true), new(20, 200, required)]);
        var plan = await new CurseForgeGeneratedPackPlanService(api).ResolveAsync(manifest);
        var dependency = Assert.Single(plan.RequiredFiles.Where(file => file.ProjectId == "20"));
        Assert.Equal("200", dependency.FileId);
        Assert.Contains(dependency.RequiredBy, evidence => evidence.Relation == CurseForgeGeneratedFileRelation.RequiredDependency);
        Assert.Equal(required, dependency.RequiredBy.Any(evidence => evidence.Relation == CurseForgeGeneratedFileRelation.ManifestRequired));
        Assert.Empty(plan.OptionalExclusions);
    }

    [Fact]
    public void Generated_evidence_recomputes_size_from_the_installed_local_file()
    {
        var root = TempRoot();
        try
        {
            var relativePath = "mods/local-size.jar";
            var installedPath = Path.Combine(root, "mods", "local-size.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
            File.WriteAllBytes(installedPath, [1, 2, 3]);
            var materialized = new CurseForgeMaterializedFile(10, 100, relativePath,
                new string('a', 40), new string('b', 64), 999, true, "server");

            var evidence = CurseForgePackService.CreateInstalledFileEvidence(root, materialized);

            Assert.Equal(3, evidence.SizeBytes);
            Assert.Equal(relativePath, evidence.RelativePath);
            Assert.Equal(materialized.LocalSha256, evidence.LocalSha256);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reader_requires_exact_primary_loader_and_project_file_inventory()
    {
        var root = TempRoot();
        try
        {
            var archive = Pack(root, Manifest([
                new { projectID = 10, fileID = 100, required = true },
                new { projectID = 30, fileID = 300, required = false }
            ]), new Dictionary<string, byte[]> { ["overrides/config/fixture.toml"] = "safe"u8.ToArray() });

            var manifest = await new CurseForgePackManifestReader().ReadAsync(archive);

            Assert.Equal("Fixture Pack", manifest.Name);
            Assert.Equal("1.20.1", manifest.MinecraftVersion);
            Assert.Equal(InstallSourceType.Fabric, manifest.Loader);
            Assert.Equal("0.15.11", manifest.LoaderVersion);
            Assert.Equal(2, manifest.Files.Count);
            Assert.False(manifest.Files[1].Required);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reader_rejects_missing_primary_loader_duplicate_project_and_oversized_manifest()
    {
        var root = TempRoot();
        try
        {
            var noPrimary = Pack(root, Manifest([new { projectID = 10, fileID = 100, required = true }],
                primary: false), name: "no-primary.zip");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePackManifestReader().ReadAsync(noPrimary));

            var duplicate = Pack(root, Manifest([
                new { projectID = 10, fileID = 100, required = true },
                new { projectID = 10, fileID = 101, required = true }
            ]), name: "duplicate.zip");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePackManifestReader().ReadAsync(duplicate));

            var huge = Path.Combine(root, "huge.zip");
            using (var archive = ZipFile.Open(huge, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
                await using var output = entry.Open();
                await output.WriteAsync(new byte[CurseForgePackManifestReader.MaximumManifestBytes + 1]);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePackManifestReader().ReadAsync(huge));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Generated_candidate_preserves_gameplay_scripts_and_skips_optional_and_os_launchers()
    {
        var root = TempRoot();
        try
        {
            var first = "first-mod"u8.ToArray();
            var second = "second-mod"u8.ToArray();
            var loader = "fabric-loader"u8.ToArray();
            var archive = Pack(root, Manifest([
                new { projectID = 10, fileID = 100, required = true },
                new { projectID = 30, fileID = 300, required = false }
            ]), new Dictionary<string, byte[]>
            {
                ["overrides/config/fixture.toml"] = "safe=true"u8.ToArray(),
                ["overrides/kubejs/server_scripts/recipes.js"] = "ServerEvents.recipes(event => {});"u8.ToArray(),
                ["overrides/scripts/recipes.zs"] = "// authored gameplay script"u8.ToArray(),
                ["overrides/start.ps1"] = "never copied"u8.ToArray()
            });
            var handler = new FixtureHandler(request => Response(request, first, second, loader));
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            using var http = new HttpClient(handler, disposeHandler: false);
            using var api = new CurseForgeApiClient(secrets, handler);
            var loaderService = new LoaderInstallationService(new LoaderMetadataService(http), http);
            var service = new CurseForgePackService(api, loaders: loaderService);
            var destination = Path.Combine(root, "candidate");
            Directory.CreateDirectory(destination);
            var java = Path.Combine(root, "java.exe");
            await File.WriteAllBytesAsync(java, [1]);

            var result = await service.MaterializeAndInstallAsync(archive, destination, java,
                Path.Combine(root, "loader.log"));

            Assert.Equal(2, result.MaterializedFiles.Count);
            Assert.Contains(result.SkippedOptionalProjects, value => value == "30/300");
            Assert.DoesNotContain(result.IgnoredOverridePaths, value => value.Equals("kubejs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.IgnoredOverridePaths, value => value.Equals("start.ps1", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(destination, "mods", "first.jar")));
            Assert.True(File.Exists(Path.Combine(destination, "mods", "second.jar")));
            Assert.True(File.Exists(Path.Combine(destination, "config", "fixture.toml")));
            Assert.Equal("ServerEvents.recipes(event => {});",
                await File.ReadAllTextAsync(Path.Combine(destination, "kubejs", "server_scripts", "recipes.js")));
            Assert.Equal("// authored gameplay script",
                await File.ReadAllTextAsync(Path.Combine(destination, "scripts", "recipes.zs")));
            Assert.True(File.Exists(Path.Combine(destination, "fabric-server-launch.jar")));
            var evidence = await File.ReadAllTextAsync(Path.Combine(destination, ".chunkpilot",
                "curseforge-pack-evidence.json"));
            Assert.Contains("localSha256", evidence, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-key", evidence, StringComparison.Ordinal);
            Assert.Equal(2, handler.CdnRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_generated_plan_materializes_exact_files_without_provider_metadata_reresolution(bool resourcePack)
    {
        var root = TempRoot();
        try
        {
            var required = resourcePack ? ResourceArchive() : "reviewed-mod"u8.ToArray();
            var name = resourcePack ? "reviewed.zip" : "reviewed.jar";
            var loader = "fabric-loader"u8.ToArray();
            var archive = Pack(root, Manifest([
                new { projectID = 10, fileID = 100, required = true },
                new { projectID = 30, fileID = 300, required = false }
            ]));
            var reviewed = CurseForgeGeneratedPackPlanService.Seal(new CurseForgeGeneratedPackPlan
            {
                MinecraftVersion = "1.20.1",
                Loader = "Fabric",
                LoaderVersion = "0.15.11",
                RequiredFiles =
                [
                    new CurseForgeGeneratedFilePlan
                    {
                        ProjectId = "10",
                        FileId = "100",
                        FileName = name,
                        ContentKind = resourcePack ? CurseForgeGeneratedContentKind.ResourcePack : CurseForgeGeneratedContentKind.Mod,
                        DownloadUrl = "https://mediafilez.forgecdn.net/files/100/reviewed.jar",
                        SizeBytes = required.LongLength,
                        ProviderSha1 = Sha1(required),
                        RequiredBy =
                        [
                            new CurseForgeGeneratedFileEvidence
                            {
                                Relation = CurseForgeGeneratedFileRelation.ManifestRequired,
                                RequestedFileId = "100"
                            }
                        ]
                    }
                ],
                OptionalExclusions =
                [
                    new CurseForgeGeneratedOptionalExclusion
                    {
                        ProjectId = "30",
                        FileId = "300",
                        Relation = CurseForgeGeneratedOptionalRelation.ManifestOptional
                    }
                ],
                TotalResolvedBytes = required.LongLength
            });
            var handler = new FixtureHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host.Equals(CurseForgeApiClient.ApiHost, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Reviewed materialization must not re-resolve provider metadata.");
                if (uri.Host.EndsWith("forgecdn.net", StringComparison.OrdinalIgnoreCase))
                    return Bytes(required);
                if (uri.AbsolutePath == "/v2/versions/loader/1.20.1")
                    return Json(new[] { new { loader = new { version = "0.15.11" } } });
                if (uri.AbsolutePath == "/v2/versions/installer")
                    return Json(new[] { new { version = "1.0.1", stable = true } });
                if (uri.AbsolutePath.EndsWith("/server/jar", StringComparison.Ordinal)) return Bytes(loader);
                throw new InvalidOperationException(uri.ToString());
            });
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            using var http = new HttpClient(handler, disposeHandler: false);
            using var api = new CurseForgeApiClient(secrets, handler);
            var service = new CurseForgePackService(api,
                loaders: new LoaderInstallationService(new LoaderMetadataService(http), http));
            var destination = Path.Combine(root, "reviewed-candidate");
            Directory.CreateDirectory(destination);
            var java = Path.Combine(root, "java.exe");
            await File.WriteAllBytesAsync(java, [1]);

            var result = await service.MaterializeAndInstallAsync(archive, destination, java,
                Path.Combine(root, "loader.log"), reviewed);

            Assert.Equal(0, handler.ApiRequests);
            Assert.Equal(1, handler.CdnRequests);
            Assert.Equal(100, Assert.Single(result.MaterializedFiles).FileId);
            var installed = Path.Combine(destination, resourcePack ? "resourcepacks" : "mods", name);
            Assert.Equal(required, await File.ReadAllBytesAsync(installed));
            if (resourcePack) Assert.False(File.Exists(Path.Combine(destination, "mods", name)));
            Assert.Contains("30/300", result.SkippedOptionalProjects);
            var evidence = await File.ReadAllTextAsync(Path.Combine(destination, ".chunkpilot",
                "curseforge-pack-evidence.json"));
            Assert.Contains(reviewed.Digest, evidence, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Resource_pack_container_validation_rejects_traversal_and_missing_metadata()
    {
        var root = TempRoot();
        try
        {
            var traversal = Path.Combine(root, "traversal.zip");
            File.WriteAllBytes(traversal, ResourceArchive("../escape.txt"));
            Assert.Throws<InvalidDataException>(() => ServerImportInspectionService.ValidateResourcePackArchive(traversal));
            var missing = Pack(root, "{}");
            Assert.Throws<InvalidDataException>(() => ServerImportInspectionService.ValidateResourcePackArchive(missing));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] ResourceArchive(string? additional = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = archive.CreateEntry("pack.mcmeta").Open())
                entry.Write("{\"pack\":{\"pack_format\":34,\"description\":\"fixture\"}}"u8);
            if (additional is not null)
            {
                using var entry = archive.CreateEntry(additional).Open();
                entry.WriteByte(1);
            }
        }
        return output.ToArray();
    }

    [Fact]
    public async Task Reviewed_generated_plan_digest_drift_fails_before_download_or_destination_write()
    {
        var root = TempRoot();
        try
        {
            var archive = Pack(root, Manifest([new { projectID = 10, fileID = 100, required = true }]));
            var original = ExactPlan("first.jar", 5, new string('a', 40));
            var drifted = original with
            {
                RequiredFiles = [original.RequiredFiles[0] with { FileName = "drifted.jar" }]
            };
            var handler = new FixtureHandler(_ => throw new InvalidOperationException("No request expected."));
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            using var api = new CurseForgeApiClient(secrets, handler);
            var destination = Path.Combine(root, "drift-candidate");
            Directory.CreateDirectory(destination);

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePackService(api).MaterializeAndInstallAsync(archive, destination,
                    Path.Combine(root, "java.exe"), Path.Combine(root, "loader.log"), drifted));

            Assert.Contains("digest", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.ApiRequests);
            Assert.Equal(0, handler.CdnRequests);
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Generated_candidate_blocks_known_incompatible_manifest_pair_without_downloading()
    {
        var root = TempRoot();
        try
        {
            var archive = Pack(root, Manifest([
                new { projectID = 10, fileID = 100, required = true },
                new { projectID = 40, fileID = 400, required = true }
            ]));
            var first = "first"u8.ToArray();
            var incompatible = "bad"u8.ToArray();
            var handler = new FixtureHandler(request =>
            {
                if (request.RequestUri!.Host.Equals(CurseForgeApiClient.ApiHost, StringComparison.OrdinalIgnoreCase))
                {
                    var path = request.RequestUri.AbsolutePath;
                    if (path == "/v1/mods/10") return Json(Project(10));
                    if (path == "/v1/mods/40") return Json(Project(40));
                    if (path == "/v1/mods/10/files/100") return Json(ApiFile(10, 100, "first.jar", first,
                        [new { modId = 40, relationType = 5 }]));
                    if (path == "/v1/mods/40/files/400") return Json(ApiFile(40, 400, "bad.jar", incompatible, []));
                }
                throw new InvalidOperationException(request.RequestUri.ToString());
            });
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            using var api = new CurseForgeApiClient(secrets, handler);
            var destination = Path.Combine(root, "candidate");
            Directory.CreateDirectory(destination);
            var java = Path.Combine(root, "java.exe");
            await File.WriteAllBytesAsync(java, [1]);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePackService(api).MaterializeAndInstallAsync(archive, destination, java,
                    Path.Combine(root, "loader.log")));

            Assert.Contains("incompatible", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.CdnRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unsafe_zip_and_hash_or_size_mismatch_never_leave_a_materialized_file()
    {
        var root = TempRoot();
        try
        {
            var unsafeArchive = Path.Combine(root, "unsafe.zip");
            using (var archive = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create))
            {
                Write(archive, "manifest.json", Encoding.UTF8.GetBytes(Manifest([
                    new { projectID = 10, fileID = 100, required = true }
                ])));
                Write(archive, "../escape.txt", "no"u8.ToArray());
            }
            var destination = Path.Combine(root, "unsafe-candidate");
            Directory.CreateDirectory(destination);
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            using var api = new CurseForgeApiClient(secrets, new FixtureHandler(_ =>
                throw new InvalidOperationException()));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePackService(api).MaterializeAndInstallAsync(unsafeArchive, destination,
                    Path.Combine(root, "java.exe"), Path.Combine(root, "loader.log")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));

            var archivePath = Pack(root, Manifest([new { projectID = 10, fileID = 100, required = true }]),
                name: "hash.zip");
            destination = Path.Combine(root, "hash-candidate");
            Directory.CreateDirectory(destination);
            var bytes = "actual"u8.ToArray();
            var handler = new FixtureHandler(request => request.RequestUri!.Host == CurseForgeApiClient.ApiHost
                ? request.RequestUri.AbsolutePath == "/v1/mods/10"
                    ? Json(Project(10))
                    : Json(ApiFile(10, 100, "bad.jar", bytes, [], sha1: new string('a', 40), size: bytes.Length + 1))
                : Bytes(bytes));
            using var mismatchApi = new CurseForgeApiClient(secrets, handler);
            await File.WriteAllBytesAsync(Path.Combine(root, "java.exe"), [1]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePackService(mismatchApi).MaterializeAndInstallAsync(archivePath, destination,
                    Path.Combine(root, "java.exe"), Path.Combine(root, "loader.log")));
            Assert.False(File.Exists(Path.Combine(destination, "mods", "bad.jar")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] first, byte[] second, byte[] loader)
    {
        var uri = request.RequestUri!;
        if (uri.Host.Equals(CurseForgeApiClient.ApiHost, StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath switch
            {
                "/v1/mods/10" => Json(Project(10)),
                "/v1/mods/20" => Json(Project(20)),
                "/v1/mods/10/files/100" => Json(ApiFile(10, 100, "first.jar", first,
                    [new { modId = 20, relationType = 3 }, new { modId = 30, relationType = 2 }])),
                "/v1/mods/20/files" => Json(new { data = new[] { FileData(20, 200, "second.jar", second,
                    [new { modId = 10, relationType = 3 }]) } }),
                _ => throw new InvalidOperationException(uri.ToString())
            };
        }
        if (uri.Host.EndsWith("forgecdn.net", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.EndsWith("first.jar", StringComparison.Ordinal) ? Bytes(first) : Bytes(second);
        if (uri.AbsolutePath == "/v2/versions/loader/1.20.1")
            return Json(new[] { new { loader = new { version = "0.15.11" } } });
        if (uri.AbsolutePath == "/v2/versions/installer")
            return Json(new[] { new { version = "1.0.1", stable = true } });
        if (uri.AbsolutePath.EndsWith("/server/jar", StringComparison.Ordinal)) return Bytes(loader);
        throw new InvalidOperationException(uri.ToString());
    }

    private static object ApiFile(long project, long file, string name, byte[] bytes, object[] dependencies,
        string? sha1 = null, long? size = null) => new
        {
            data = FileData(project, file, name, bytes, dependencies, sha1, size)
        };

    private static object FileData(long project, long file, string name, byte[] bytes, object[] dependencies,
        string? sha1 = null, long? size = null) => new
        {
            id = file,
            modId = project,
            fileName = name,
            displayName = name,
            fileDate = "2026-08-01T00:00:00Z",
            releaseType = 1,
            isAvailable = true,
            gameVersions = new[] { "1.20.1", "Fabric" },
            downloadUrl = $"https://mediafilez.forgecdn.net/files/{file}/{name}",
            fileLength = size ?? bytes.LongLength,
            hashes = new[] { new { algo = 1, value = sha1 ?? Sha1(bytes) } },
            dependencies
        };

    private static string Manifest(object[] files, bool primary = true) => JsonSerializer.Serialize(new
    {
        manifestType = "minecraftModpack",
        manifestVersion = 1,
        name = "Fixture Pack",
        version = "1.0.0",
        author = "Fixture",
        files,
        overrides = "overrides",
        minecraft = new
        {
            version = "1.20.1",
            modLoaders = new[] { new { id = "fabric-0.15.11", primary } }
        }
    });

    private static string Pack(string root, string manifest, IReadOnlyDictionary<string, byte[]>? files = null,
        string name = "pack.zip")
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "manifest.json", Encoding.UTF8.GetBytes(manifest));
        Write(archive, "overrides/.keep", [1]);
        foreach (var file in files ?? new Dictionary<string, byte[]>()) Write(archive, file.Key, file.Value);
        return path;
    }

    private static void Write(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body is string text ? text : JsonSerializer.Serialize(body), Encoding.UTF8,
            "application/json")
    };

    private static object Project(long id) => new
    {
        data = new { id, gameId = 432, classId = 6, isAvailable = true, allowModDistribution = true }
    };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static string Sha1(byte[] bytes)
    {
#pragma warning disable CA5350 // Fixture mirrors CurseForge's published provider digest.
        return Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    private static CurseForgeGeneratedPackPlan ExactPlan(string fileName, long size, string sha1) =>
        CurseForgeGeneratedPackPlanService.Seal(new CurseForgeGeneratedPackPlan
        {
            MinecraftVersion = "1.20.1",
            Loader = "Fabric",
            LoaderVersion = "0.15.11",
            RequiredFiles =
            [
                new CurseForgeGeneratedFilePlan
                {
                    ProjectId = "10",
                    FileId = "100",
                    FileName = fileName,
                    DownloadUrl = $"https://mediafilez.forgecdn.net/files/100/{fileName}",
                    SizeBytes = size,
                    ProviderSha1 = sha1,
                    RequiredBy =
                    [
                        new CurseForgeGeneratedFileEvidence
                        {
                            Relation = CurseForgeGeneratedFileRelation.ManifestRequired,
                            RequestedFileId = "100"
                        }
                    ]
                }
            ],
            TotalResolvedBytes = size
        });

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-cf-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public void SetSecret(string name, string value) => values[name] = value;
        public string? GetSecret(string name) => values.GetValueOrDefault(name);
        public bool Contains(string name) => values.ContainsKey(name);
        public void Delete(string name) => values.Remove(name);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        private int cdnRequests;
        private int apiRequests;
        public int CdnRequests => cdnRequests;
        public int ApiRequests => apiRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.EndsWith("forgecdn.net", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref cdnRequests);
            if (request.RequestUri.Host.Equals(CurseForgeApiClient.ApiHost, StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref apiRequests);
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }
}
