using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using ChunkPilot.App;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.UnitTests;

public sealed class Release11Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-11-" + Guid.NewGuid().ToString("N"));

    public Release11Tests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("-jar server.jar", "-jar server.jar nogui")]
    [InlineData("-jar server.jar nogui", "-jar server.jar nogui")]
    [InlineData("-jar server.jar --nogui", "-jar server.jar --nogui")]
    [InlineData("-jar server.jar NOGUI", "-jar server.jar NOGUI")]
    public void Background_launch_injects_nogui_once(string input, string expected) =>
        Assert.Equal(expected, ServerLaunchPolicy.EnsureNoGui(input, ServerEcosystem.Forge));

    [Fact]
    public void Background_launch_does_not_modify_custom_process_arguments() =>
        Assert.Equal("normal", ServerLaunchPolicy.EnsureNoGui("normal", ServerEcosystem.Custom));

    [Theory]
    [InlineData("javaw.exe", "", true)]
    [InlineData("cmd.exe", "/c start \"\" java -jar server.jar", true)]
    [InlineData("java.exe", "-jar server.jar nogui", false)]
    public void Detached_launch_detection_is_explicit(string executable, string arguments, bool expected) =>
        Assert.Equal(expected, ServerLaunchPolicy.IsDetachedLaunch(executable, arguments));

    [Theory]
    [InlineData(" My Server ", "My-Server")]
    [InlineData("A:B/C", "A-B-C")]
    [InlineData("hello...world", "hello-world")]
    public void Managed_instance_names_are_safe(string input, string expected) =>
        Assert.Equal(expected, ManagedServerInstaller.MakeSafeInstanceName(input));

    [Fact]
    public async Task Managed_install_is_transactional_and_requires_deliberate_eula()
    {
        var paths = new AppDataPaths(Path.Combine(root, "appdata"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        var package = CreateServerZip("package.zip");
        var instances = Path.Combine(root, "instances");
        var java = Path.Combine(root, "java.exe");
        await File.WriteAllTextAsync(java, "");
        var installer = new ManagedServerInstaller(paths, store,
            new ServerDownloadCatalog(new HttpClient(new StubHandler(_ => throw new InvalidOperationException()))),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException())));
        var rejected = new ServerInstallRequest
        {
            SourceType = InstallSourceType.LocalZip,
            Source = package,
            ServerName = "Eula rejected",
            InstanceRoot = instances,
            JavaPath = java
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(rejected));
        Assert.False(Directory.Exists(Path.Combine(instances, "Eula-rejected")));
        Assert.False(File.Exists(Path.Combine(instances, "Eula-rejected", "eula.txt")));

        var request = rejected with
        {
            OperationId = Guid.NewGuid(),
            ServerName = "Accepted Server",
            EulaAccepted = true,
            EulaAcceptedAt = DateTimeOffset.Now
        };
        var result = await installer.InstallAsync(request);
        Assert.True(result.Definition.IsManaged);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, "server.jar")));
        Assert.Equal("eula=true", (await File.ReadAllTextAsync(Path.Combine(result.Definition.RootPath, "eula.txt"))).Trim());
        Assert.Empty(await store.GetInterruptedOperationsAsync());
        Assert.DoesNotContain(Directory.EnumerateDirectories(instances),
            path => Path.GetFileName(path).StartsWith(".chunkpilot-staging-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Managed_zip_extraction_rejects_traversal()
    {
        var zip = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntry("../escape.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedServerInstaller.ExtractZipSafeAsync(zip, Path.Combine(root, "extract")));
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
    }

    [Fact]
    public void Download_checksum_verification_rejects_mismatch()
    {
        var file = Path.Combine(root, "payload.jar");
        File.WriteAllText(file, "payload");
        Assert.Throws<InvalidDataException>(() => ManagedServerInstaller.VerifyHash(file, "", new string('0', 64)));
        var correct = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        ManagedServerInstaller.VerifyHash(file, "", correct);
    }

    [Fact]
    public async Task Paper_catalog_selects_only_stable_builds()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Contains("/builds", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Json("""
                [
                  {"id":99,"channel":"EXPERIMENTAL","downloads":{"server:default":{"name":"bad.jar","url":"https://example/bad","checksums":{"sha256":"bad"},"size":3}}},
                  {"id":42,"channel":"STABLE","downloads":{"server:default":{"name":"paper.jar","url":"https://example/stable","checksums":{"sha256":"abc"},"size":12}}}
                ]
                """);
        });
        var catalog = new ServerDownloadCatalog(new HttpClient(handler));
        var resolved = await catalog.ResolveAsync(InstallSourceType.Paper, "1.21.1", "");
        Assert.Equal("42", resolved.Build);
        Assert.Equal("https://example/stable", resolved.Url);
    }

    [Fact]
    public void Console_follow_pauses_counts_and_resumes()
    {
        var state = new ConsoleFollowState();
        Assert.True(state.OnLinesAdded(2));
        state.OnViewportChanged(false);
        Assert.False(state.OnLinesAdded(3));
        Assert.Equal(3, state.UnseenLineCount);
        state.JumpToLatest();
        Assert.True(state.IsFollowing);
        Assert.Equal(0, state.UnseenLineCount);
    }

    [Fact]
    public void Configuration_choices_and_ranges_are_constrained()
    {
        var errors = ServerPropertyValidation.Validate(new Dictionary<string, string>
        {
            ["gamemode"] = "builder",
            ["difficulty"] = "impossible",
            ["server-port"] = "70000",
            ["view-distance"] = "1"
        });
        Assert.Contains("gamemode", errors.Keys);
        Assert.Contains("difficulty", errors.Keys);
        Assert.Contains("server-port", errors.Keys);
        Assert.Contains("view-distance", errors.Keys);
    }

    [Fact]
    public async Task Server_icon_is_converted_to_exact_64_pixel_png_with_recovery()
    {
        var paths = new AppDataPaths(Path.Combine(root, "icon-data"));
        paths.EnsureCreated();
        var serverRoot = Path.Combine(root, "icon-server");
        Directory.CreateDirectory(serverRoot);
        var source = Path.Combine(root, "source.png");
        await File.WriteAllBytesAsync(source, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var existing = Path.Combine(serverRoot, "server-icon.png");
        File.Copy(source, existing);
        var service = new ServerIconService(paths);
        var installed = await service.ConvertAndInstallAsync(new ServerDefinition { RootPath = serverRoot }, source);
        using var stream = File.OpenRead(installed);
        using var image = await SixLabors.ImageSharp.Image.LoadAsync(stream);
        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
        Assert.NotEmpty(Directory.EnumerateFiles(paths.Recovery, "*server-icon.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Server_icon_uses_requested_crop_and_keeps_a_reusable_library_copy()
    {
        var paths = new AppDataPaths(Path.Combine(root, "cropped-icon-data"));
        paths.EnsureCreated();
        var serverRoot = Path.Combine(root, "cropped-icon-server");
        Directory.CreateDirectory(serverRoot);
        var source = Path.Combine(root, "wide-source.png");
        using (var original = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 2))
        {
            for (var y = 0; y < 2; y++)
            for (var x = 0; x < 4; x++)
                original[x, y] = x < 2
                    ? SixLabors.ImageSharp.Color.Red
                    : SixLabors.ImageSharp.Color.Blue;
            await using var sourceStream = File.Create(source);
            await original.SaveAsync(sourceStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        }

        var service = new ServerIconService(paths);
        var installed = await service.ConvertAndInstallAsync(
            new ServerDefinition { RootPath = serverRoot }, source, cropX: 1, cropY: 0, cropSize: 1);

        using var result = await SixLabors.ImageSharp.Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgba32>(installed);
        Assert.Equal(SixLabors.ImageSharp.Color.Blue.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>(), result[32, 32]);
        var saved = Assert.Single(service.ListLibrary());
        Assert.True(File.Exists(saved.Path));
        Assert.StartsWith(paths.ServerIcons, saved.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task World_switch_changes_only_properties_and_never_deletes_worlds()
    {
        var paths = new AppDataPaths(Path.Combine(root, "world-data"));
        paths.EnsureCreated();
        var serverRoot = Path.Combine(root, "world-server");
        Directory.CreateDirectory(serverRoot);
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"), "level-name=world\r\n");
        foreach (var name in new[] { "world", "creative" })
        {
            Directory.CreateDirectory(Path.Combine(serverRoot, name));
            await File.WriteAllTextAsync(Path.Combine(serverRoot, name, "level.dat"), name);
        }
        var manager = new WorldManager(paths, new SafeFileService(paths));
        Assert.Equal(2, manager.List(new ServerDefinition { RootPath = serverRoot }).Count);
        await manager.SwitchActiveAsync(new ServerDefinition { RootPath = serverRoot }, "creative", ServerState.Stopped);
        Assert.True(Directory.Exists(Path.Combine(serverRoot, "world")));
        Assert.True(Directory.Exists(Path.Combine(serverRoot, "creative")));
        Assert.Contains("level-name=creative", await File.ReadAllTextAsync(Path.Combine(serverRoot, "server.properties")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Clipboard_world_share_is_prepared_as_a_real_file_drop()
    {
        var zip = Path.Combine(root, "world-share.zip");
        File.WriteAllBytes(zip, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var files = ClipboardFileDropService.Prepare(zip);
        Assert.Equal(Path.GetFullPath(zip), Assert.Single(files.Cast<string>()));
    }

    [Fact]
    public async Task Whitelist_json_round_trips_and_commands_are_validated()
    {
        var paths = new AppDataPaths(Path.Combine(root, "whitelist-data"));
        paths.EnsureCreated();
        var serverRoot = Path.Combine(root, "whitelist-server");
        Directory.CreateDirectory(serverRoot);
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "whitelist.json"), "[]\r\n");
        var service = new WhitelistService(new SafeFileService(paths));
        var player = new WhitelistEntry { Name = "Test_Player", Uuid = Guid.NewGuid() };
        var server = new ServerDefinition { RootPath = serverRoot };
        await service.WriteAsync(server, [player]);
        var read = Assert.Single(await service.ReadAsync(server));
        Assert.Equal(player.Name, read.Name);
        Assert.Equal($"whitelist add {player.Name}", WhitelistService.AddCommand(player.Name));
        Assert.Throws<ArgumentException>(() => WhitelistService.AddCommand("not a player name"));
    }

    [Fact]
    public void Ram_recommendation_and_validation_reserve_host_memory()
    {
        var recommendation = RamRecommendationCalculator.Calculate(
            64L * 1024 * 1024 * 1024,
            48L * 1024 * 1024 * 1024,
            ServerEcosystem.Forge, 200, 0, 12, 8_192);
        Assert.InRange(recommendation.RecommendedMb, 3_072, recommendation.MaximumSafeMb);
        Assert.True(recommendation.MaximumSafeMb < 64 * 1024);
        Assert.Contains(RamRecommendationCalculator.Validate(8_192, 4_096, 64L * 1024 * 1024 * 1024, 4_096),
            warning => warning.Contains("Xms", StringComparison.Ordinal));
    }

    [Fact]
    public void Ram_argument_update_replaces_values_without_duplication()
    {
        var updated = RamArgumentService.UpdateArguments("-Xms2G -Xmx4G -jar server.jar nogui", 3_072, 6_144, " ");
        Assert.Equal(1, Count(updated, "-Xms"));
        Assert.Equal(1, Count(updated, "-Xmx"));
        Assert.Contains("-Xms3072M -Xmx6144M", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_v1_migration_preserves_server_rows_and_advances_schema()
    {
        var paths = new AppDataPaths(Path.Combine(root, "migration"));
        paths.EnsureCreated();
        var definition = new ServerDefinition { Name = "Preserved", RootPath = @"C:\external-server" };
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE servers (id TEXT PRIMARY KEY, json TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO servers(id,json,updated_utc) VALUES($id,$json,$updated);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$id", definition.Id.ToString("D"));
            command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(definition, ProtocolJson.Options));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await using (var store = new ChunkPilotStore(paths))
        {
            await store.InitializeAsync();
            Assert.Equal("Preserved", Assert.Single(await store.GetServersAsync()).Name);
        }
        await using (var check = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await check.OpenAsync();
            var version = check.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(6L, (long)(await version.ExecuteScalarAsync())!);
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Theme_resources_include_dark_popup_and_selected_item_templates()
    {
        // Retargeted from the former single Controls.xaml onto the rebuilt selection dictionary.
        // The guarantee is unchanged: drop-downs are themed dark, and the chosen item is marked.
        var selection = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "ChunkPilot.App", "Themes", "Controls", "Selection.xaml"));

        Assert.Contains("PART_Popup", selection, StringComparison.Ordinal);
        Assert.Contains("ComboBoxItem", selection, StringComparison.Ordinal);
        Assert.Contains("IsSelected", selection, StringComparison.Ordinal);
        Assert.Contains("AppSurfaceOverlay", selection, StringComparison.Ordinal);

        // Inspect brush values, not arbitrary prose: "whitelist" in a comment is not a white brush.
        var brushProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "Background", "BorderBrush", "Fill", "Foreground", "Stroke"
        };
        var lightBrushes = XDocument.Parse(selection)
            .Descendants()
            .SelectMany(element =>
            {
                if (element.Name.LocalName == "Setter" &&
                    element.Attribute("Property") is { } property &&
                    element.Attribute("Value") is { } value &&
                    brushProperties.Contains(property.Value.Split('.').Last()))
                    return new[] { value };

                return element.Attributes()
                    .Where(attribute => brushProperties.Contains(attribute.Name.LocalName));
            })
            .Where(attribute => IsLiteralWhiteBrush(attribute.Value))
            .Select(attribute => $"{attribute.Parent?.Name.LocalName}.{attribute.Name.LocalName}={attribute.Value}")
            .ToArray();

        Assert.Empty(lightBrushes);

        static bool IsLiteralWhiteBrush(string value)
        {
            var candidate = value.Trim();
            if (candidate.Equals("White", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("WhiteSmoke", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("Snow", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!candidate.StartsWith('#'))
                return false;
            var hex = candidate[1..];
            return hex.Length is 3 or 4 or 6 or 8 && hex.All(character => character is 'f' or 'F');
        }
    }

    private string CreateServerZip(string name)
    {
        var serverJar = Path.Combine(root, "server.jar");
        using (var jar = ZipFile.Open(serverJar, ZipArchiveMode.Create))
            jar.CreateEntry("META-INF/MANIFEST.MF");
        var package = Path.Combine(root, name);
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(serverJar, "server.jar");
        return package;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
