using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ModrinthPackFormatTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-mrpack-unit-" + Guid.NewGuid().ToString("N"));

    public ModrinthPackFormatTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Reader_applies_standard_server_environment_defaults_and_trusted_origins()
    {
        var required = PackFile("mods/required.jar", "required", server: null,
            "https://cdn.modrinth.com/data/project/versions/version/required.jar");
        var optional = PackFile("mods/optional.jar", "optional", "optional",
            "https://github.com/example/project/releases/download/v1/optional.jar");
        var unsupported = PackFile("mods/client.jar", "client", "unsupported",
            "https://raw.githubusercontent.com/example/project/main/client.jar");
        var packPath = CreatePack(Index(required, optional, unsupported));

        var pack = await new ModrinthPackReader().ReadAsync(packPath);

        Assert.Equal(1, pack.Manifest.FormatVersion);
        Assert.Equal("minecraft", pack.Manifest.Game);
        Assert.Equal("1.21.1", pack.Manifest.Dependencies["minecraft"]);
        Assert.Equal(ModrinthPackEnvironmentSupport.Required, pack.Manifest.Files[0].ServerEnvironment);
        Assert.Equal(ModrinthPackEnvironmentSupport.Optional, pack.Manifest.Files[1].ServerEnvironment);
        Assert.Equal(ModrinthPackEnvironmentSupport.Unsupported, pack.Manifest.Files[2].ServerEnvironment);
        Assert.Equal(ModrinthPackDownloadOrigin.ModrinthCdn, pack.Manifest.Files[0].Downloads[0].Origin);
        Assert.Equal(ModrinthPackDownloadOrigin.GitHub, pack.Manifest.Files[1].Downloads[0].Origin);
        Assert.Equal(ModrinthPackDownloadOrigin.GitHubRaw, pack.Manifest.Files[2].Downloads[0].Origin);
    }

    [Fact]
    public async Task Materializer_downloads_server_files_then_common_and_server_overrides_and_skips_directories()
    {
        var indexed = PackFile("config/value.txt", "manifest", server: null);
        var required = PackFile("mods/required.jar", "required", "required");
        var optional = PackFile("mods/optional.jar", "optional", "optional");
        var unsupported = PackFile("mods/client.jar", "client", "unsupported");
        var packPath = CreatePack(Index(indexed, required, optional, unsupported),
            ("overrides/config/", null),
            ("overrides/config/value.txt", Bytes("common")),
            ("server-overrides/config/value.txt", Bytes("server")),
            ("client-overrides/config/value.txt", Bytes("client")),
            ("overrides/empty/", null));
        var source = new DictionaryDownloadSource([indexed, required, optional, unsupported]);
        var destination = Path.Combine(root, "materialized");

        var result = await new ModrinthServerPackMaterializer().MaterializeAsync(
            packPath, destination, source, new ModrinthPackMaterializationOptions
            {
                IncludeOptionalServerFiles = false,
                MaximumConcurrentDownloads = 2
            });

        Assert.Equal("server", File.ReadAllText(Path.Combine(destination, "config", "value.txt")));
        Assert.Equal("required", File.ReadAllText(Path.Combine(destination, "mods", "required.jar")));
        Assert.False(File.Exists(Path.Combine(destination, "mods", "optional.jar")));
        Assert.False(File.Exists(Path.Combine(destination, "mods", "client.jar")));
        Assert.False(File.Exists(Path.Combine(destination, "empty")));
        Assert.False(Directory.Exists(Path.Combine(destination, "empty")));
        Assert.Contains("mods/optional.jar", result.SkippedOptionalFiles);
        Assert.Contains("mods/client.jar", result.SkippedUnsupportedFiles);
        Assert.Equal(ModrinthPackSourceLayer.ServerOverride,
            result.Files.Single(file => file.RelativePath == "config/value.txt").SourceLayer);
        Assert.DoesNotContain(source.Requests, uri => uri.AbsoluteUri.Contains("optional.jar", StringComparison.Ordinal));
        Assert.DoesNotContain(source.Requests, uri => uri.AbsoluteUri.Contains("client.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Materializer_reports_exact_file_and_byte_progress_without_invented_work()
    {
        var first = PackFile("mods/first.jar", "first", server: null);
        var second = PackFile("mods/second.jar", "second", server: null);
        var packPath = CreatePack(Index(first, second),
            ("server-overrides/config/value.txt", Bytes("override")));
        var updates = new List<ModrinthMaterializationProgress>();
        var destination = Path.Combine(root, "progress-materialized");

        await new ModrinthServerPackMaterializer().MaterializeAsync(
            packPath,
            destination,
            new DictionaryDownloadSource([first, second]),
            new ModrinthPackMaterializationOptions { MaximumConcurrentDownloads = 2 },
            new CaptureProgress<ModrinthMaterializationProgress>(updates.Add),
            CancellationToken.None);

        Assert.NotEmpty(updates);
        Assert.Equal(0, updates[0].CompletedFiles);
        var completed = updates[^1];
        Assert.Equal(3, completed.TotalFiles);
        Assert.Equal(completed.TotalFiles, completed.CompletedFiles);
        Assert.Equal(completed.TotalBytes, completed.CompletedBytes);
        Assert.Contains("config/value.txt", completed.CurrentFile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.jar")]
    [InlineData("mods/../escape.jar")]
    [InlineData("mods\\escape.jar")]
    [InlineData("C:/escape.jar")]
    [InlineData("mods/CON.txt")]
    [InlineData("mods/file. ")]
    [InlineData(".chunkpilot/update-source.json")]
    public async Task Reader_rejects_unsafe_windows_and_internal_paths(string path)
    {
        var packPath = CreatePack(Index(PackFile(path, "content", server: null)));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ModrinthPackReader().ReadAsync(packPath));
    }

    [Fact]
    public async Task Reader_rejects_case_equivalent_destinations_across_layers()
    {
        var packPath = CreatePack(Index(PackFile("Config/value.txt", "content", server: null)),
            ("server-overrides/config/value.txt", Bytes("replacement")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ModrinthPackReader().ReadAsync(packPath));
        Assert.Contains("Case-equivalent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reader_enforces_bounded_override_sizes_before_materialization()
    {
        var packPath = CreatePack(Index(), ("overrides/config/value.txt", Bytes("four")));
        var reader = new ModrinthPackReader(new ModrinthPackLimits
        {
            MaximumOverrideFileBytes = 3,
            MaximumOverrideBytes = 3
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(packPath));
        Assert.Contains("size limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://cdn.modrinth.com/data/project/file.jar")]
    [InlineData("https://evil.example/file.jar")]
    [InlineData("https://user@cdn.modrinth.com/file.jar")]
    public async Task Reader_rejects_untrusted_download_origins(string url)
    {
        var packPath = CreatePack(Index(PackFile("mods/file.jar", "content", server: null, url)));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ModrinthPackReader().ReadAsync(packPath));
    }

    [Fact]
    public async Task Materializer_rejects_bad_hash_and_never_promotes_or_leaves_staging()
    {
        var expected = PackFile("mods/file.jar", "expected", server: null);
        var packPath = CreatePack(Index(expected));
        var source = new DictionaryDownloadSource(new Dictionary<string, byte[]>
        {
            [expected.Downloads[0]] = Bytes("tampered")
        });
        var destination = Path.Combine(root, "failed");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModrinthServerPackMaterializer().MaterializeAsync(packPath, destination, source));

        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(root, "failed.mrpack-staging-*"));
    }

    [Fact]
    public async Task Http_source_follows_only_approved_public_https_redirects()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "github.com"
            ? new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://objects.githubusercontent.com/release/file.jar") }
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes("verified")) });
        using var source = new ModrinthPackHttpDownloadSource(handler,
            static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        await using var stream = await source.OpenReadAsync(
            new Uri("https://github.com/example/project/releases/download/v1/file.jar"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("verified", await reader.ReadToEndAsync());
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Http_source_rejects_foreign_redirects_and_private_resolution_before_request()
    {
        var redirect = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.example/file.jar") }
        });
        using (var source = new ModrinthPackHttpDownloadSource(redirect,
                   static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => source.OpenReadAsync(
                new Uri("https://cdn.modrinth.com/data/project/file.jar")));
        }
        Assert.Equal(1, redirect.RequestCount);

        var unreachable = new StubHandler(_ => throw new InvalidOperationException("Network must not be called."));
        using (var source = new ModrinthPackHttpDownloadSource(unreachable,
                   static (_, _) => Task.FromResult(new[] { IPAddress.Loopback })))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => source.OpenReadAsync(
                new Uri("https://cdn.modrinth.com/data/project/file.jar")));
        }
        Assert.Equal(0, unreachable.RequestCount);
    }

    [Fact]
    public void CurseForge_credential_and_distribution_failures_are_explicitly_unavailable()
    {
        var missingKey = PackArtifactAccess.CredentialRequired(
            UpdateProvider.CurseForge, "A user-supplied CurseForge API key is required.");
        var disabled = PackArtifactAccess.DistributionUnavailable(
            UpdateProvider.CurseForge, "The publisher disabled third-party distribution.");

        Assert.False(missingKey.IsAvailable);
        Assert.Equal(PackArtifactAccessState.CredentialRequired, missingKey.State);
        Assert.Null(missingKey.DownloadUri);
        Assert.False(disabled.IsAvailable);
        Assert.Equal(PackArtifactAccessState.DistributionUnavailable, disabled.State);
        Assert.Null(disabled.DownloadUri);
    }

    [Fact]
    public async Task Inspection_reports_exact_loader_java_and_server_file_counts_without_materializing()
    {
        var required = PackFile("mods/required.jar", "required", "required");
        var optional = PackFile("mods/optional.jar", "optional", "optional");
        var clientOnly = PackFile("mods/client.jar", "client", "unsupported");
        var packPath = CreatePack(Index(required, optional, clientOnly));

        var result = await new ModrinthPackServerService().InspectAsync(packPath);

        Assert.True(result.CanCreate);
        Assert.Equal("Fixture Pack", result.Name);
        Assert.Equal("1.21.1", result.MinecraftVersion);
        Assert.Equal("NeoForge", result.Loader);
        Assert.Equal("21.1.233", result.LoaderVersion);
        Assert.Equal(21, result.RequiredJavaMajor);
        Assert.Equal(1, result.RequiredServerFiles);
        Assert.Equal(1, result.OptionalServerFiles);
        Assert.Equal(1, result.ExcludedClientFiles);
        Assert.Equal(128, result.ArchiveSha512.Length);
        Assert.Equal(new FileInfo(packPath).Length, result.ArchiveSizeBytes);
    }

    [Fact]
    public void Creation_plan_requires_exact_trusted_remote_identity_and_integrity()
    {
        var valid = new ModpackCreationPlan
        {
            SourceKind = ModpackCreationSource.Modrinth,
            Source = "https://cdn.modrinth.com/data/project/versions/release/pack.mrpack",
            Provider = UpdateProvider.Modrinth,
            ProjectId = "project",
            ProjectName = "Fixture Pack",
            VersionId = "release",
            VersionName = "1.0",
            MinecraftVersion = "1.21.1",
            RequiredJavaMajor = 21,
            ExpectedSizeBytes = 1_000,
            ExpectedSha1 = new string('a', 40),
            ExpectedSha512 = new string('b', 128),
            ServerName = "Fixture",
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            },
            MinimumRamMb = 2_048,
            MaximumRamMb = 4_096
        };

        Assert.Empty(valid.Problems());
        var invalid = valid with
        {
            Source = "https://example.invalid/pack.mrpack",
            ExpectedSha512 = "",
            VersionId = ""
        };
        Assert.Contains(invalid.Problems(), problem => problem.Contains("identity", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(invalid.Problems(), problem => problem.Contains("trusted Modrinth CDN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(invalid.Problems(), problem => problem.Contains("integrity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Local_modpack_plan_requires_the_inspected_archive_identity()
    {
        var plan = new ModpackCreationPlan
        {
            SourceKind = ModpackCreationSource.LocalMrpack,
            Source = "selected.mrpack",
            Provider = UpdateProvider.LocalPackageHistory,
            ProjectName = "Local pack",
            VersionId = "1.0",
            VersionName = "1.0",
            MinecraftVersion = "1.21.1",
            RequiredJavaMajor = 21,
            ServerName = "Local pack server",
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            }
        };

        Assert.Contains(plan.Problems(), problem =>
            problem.Contains("archive identity", StringComparison.OrdinalIgnoreCase));
        Assert.Empty((plan with { ExpectedSha512 = new string('a', 128), ExpectedSizeBytes = 42 }).Problems());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private string CreatePack(string index, params (string Name, byte[]? Content)[] entries)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mrpack");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "modrinth.index.json", Bytes(index));
        foreach (var entry in entries)
            WriteEntry(archive, entry.Name, entry.Content);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[]? content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        if (content is null)
            return;
        using var output = entry.Open();
        output.Write(content);
    }

    private static string Index(params FileFixture[] files)
    {
        var serializedFiles = files.Select(file =>
        {
            var result = new Dictionary<string, object?>
            {
                ["path"] = file.Path,
                ["fileSize"] = file.Content.LongLength,
                ["hashes"] = new Dictionary<string, string>
                {
                    ["sha1"] = Hash(file.Content, SHA1.HashData),
                    ["sha512"] = Hash(file.Content, SHA512.HashData)
                },
                ["downloads"] = file.Downloads
            };
            if (file.ServerEnvironment is not null)
            {
                result["env"] = new Dictionary<string, string>
                {
                    ["client"] = "required",
                    ["server"] = file.ServerEnvironment
                };
            }
            return result;
        }).ToArray();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = "fixture-v1",
            ["name"] = "Fixture Pack",
            ["summary"] = "A bounded fixture",
            ["dependencies"] = new Dictionary<string, string>
            {
                ["minecraft"] = "1.21.1",
                ["neoforge"] = "21.1.233"
            },
            ["files"] = serializedFiles
        });
    }

    private static FileFixture PackFile(
        string path,
        string content,
        string? server,
        params string[] downloads) => new(
        path,
        Bytes(content),
        server,
        downloads.Length == 0
            ? [$"https://cdn.modrinth.com/data/fixture/{Uri.EscapeDataString(path)}"]
            : downloads);

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Hash(byte[] bytes, Func<byte[], byte[]> hash) =>
        Convert.ToHexString(hash(bytes)).ToLowerInvariant();

    private sealed record FileFixture(
        string Path,
        byte[] Content,
        string? ServerEnvironment,
        string[] Downloads);

    private sealed class DictionaryDownloadSource : IModrinthPackDownloadSource
    {
        private readonly IReadOnlyDictionary<string, byte[]> content;

        public DictionaryDownloadSource(IEnumerable<FileFixture> files)
            : this(files.ToDictionary(file => file.Downloads[0], file => file.Content, StringComparer.Ordinal))
        {
        }

        public DictionaryDownloadSource(IReadOnlyDictionary<string, byte[]> content) => this.content = content;

        public ConcurrentBag<Uri> Requests { get; } = [];

        public Task<Stream> OpenReadAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(uri);
            return Task.FromResult<Stream>(new MemoryStream(content[uri.AbsoluteUri], writable: false));
        }
    }

    private sealed class CaptureProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }
}
