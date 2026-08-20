using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace ChunkPilot.Infrastructure;

public sealed class ServerIconService
{
    private readonly AppDataPaths paths;

    public ServerIconService(AppDataPaths paths) => this.paths = paths;

    public async Task<string> ConvertAndInstallAsync(
        ServerDefinition server,
        string sourcePath,
        double cropX = 0,
        double cropY = 0,
        double cropSize = 1,
        bool saveToLibrary = true,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected image does not exist.", sourcePath);
        var target = Path.Combine(server.RootPath, "server-icon.png");
        var temporary = Path.Combine(server.RootPath, $".server-icon.{Guid.NewGuid():N}.tmp");
        string? libraryStaging = null;
        try
        {
            // Decode completely and dispose the source before replacing anything. This means the
            // caller can move or delete its original immediately after the operation returns.
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var image = await Image.LoadAsync(source, cancellationToken).ConfigureAwait(false))
            {
                var crop = ServerIconPixelCrop.FromNormalized(
                    image.Width, image.Height, cropX, cropY, cropSize);
                image.Mutate(context => context
                    .Crop(new Rectangle(crop.X, crop.Y, crop.Size, crop.Size))
                    .Resize(new ResizeOptions
                    {
                        Size = new Size(64, 64),
                        Mode = ResizeMode.Stretch,
                        Sampler = KnownResamplers.NearestNeighbor
                    }));
                await image.SaveAsync(temporary, new PngEncoder(), cancellationToken).ConfigureAwait(false);
            }

            // Refuse to finalize anything other than the exact Minecraft icon contract.
            var output = await Image.IdentifyAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (output is null || output.Width != 64 || output.Height != 64)
                throw new InvalidDataException("The converted server icon was not a valid 64 x 64 image.");

            // Prepare the reusable copy under an unlisted name. It becomes a library record only
            // after the server icon itself has finalized successfully.
            string? libraryPath = null;
            if (saveToLibrary)
            {
                Directory.CreateDirectory(paths.ServerIcons);
                var bytes = await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
                var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
                libraryPath = Path.Combine(paths.ServerIcons, $"icon-{hash}.png");
                if (!File.Exists(libraryPath))
                {
                    libraryStaging = Path.Combine(paths.ServerIcons, $".icon-{Guid.NewGuid():N}.tmp");
                    await File.WriteAllBytesAsync(libraryStaging, bytes, cancellationToken).ConfigureAwait(false);
                }
            }

            // Only now create a recovery point. Invalid/cancelled conversions never produce noise,
            // and the existing icon is still the live file until this same-directory atomic move.
            if (File.Exists(target))
            {
                Directory.CreateDirectory(paths.Recovery);
                var recovery = Path.Combine(paths.Recovery, server.Id.ToString("N"), "Icons",
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-server-icon.png");
                Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
                File.Copy(target, recovery, overwrite: false);
            }
            File.Move(temporary, target, overwrite: true);

            if (libraryStaging is not null && libraryPath is not null)
            {
                try
                {
                    File.Move(libraryStaging, libraryPath, overwrite: false);
                    libraryStaging = null;
                }
                catch (IOException) when (File.Exists(libraryPath))
                {
                    // Another equivalent install won the content-addressed library race.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The server icon is already authoritative. A library-only failure must not
                    // turn a successful replacement into a reported failure with a stale preview.
                }
            }
            return target;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            if (libraryStaging is not null && File.Exists(libraryStaging))
                File.Delete(libraryStaging);
        }
    }

    public IReadOnlyList<ServerIconLibraryEntry> ListLibrary()
    {
        Directory.CreateDirectory(paths.ServerIcons);
        return Directory.EnumerateFiles(paths.ServerIcons, "*.png", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new ServerIconLibraryEntry(
                file.FullName,
                Path.GetFileNameWithoutExtension(file.Name),
                file.LastWriteTimeUtc))
            .ToArray();
    }

    public string? ExistingIcon(ServerDefinition server)
    {
        var path = Path.Combine(server.RootPath, "server-icon.png");
        return File.Exists(path) ? path : null;
    }
}

public sealed class WorldManager
{
    private readonly AppDataPaths paths;
    private readonly SafeFileService files;

    public WorldManager(AppDataPaths paths, SafeFileService files)
    {
        this.paths = paths;
        this.files = files;
    }

    public IReadOnlyList<WorldEntry> List(ServerDefinition server)
    {
        var active = ReadActiveWorld(server.RootPath);
        var directories = Directory.EnumerateDirectories(server.RootPath, "*", SearchOption.TopDirectoryOnly)
            .Where(directory => File.Exists(Path.Combine(directory, "level.dat")))
            .ToArray();
        var entries = new List<WorldEntry>();
        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (name.EndsWith("_nether", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_the_end", StringComparison.OrdinalIgnoreCase))
                continue;
            var dimensions = DetectDimensions(server.RootPath, directory).ToArray();
            entries.Add(new WorldEntry
            {
                Name = name,
                FolderPath = directory,
                IsActive = name.Equals(active, StringComparison.OrdinalIgnoreCase),
                ModifiedAt = Directory.GetLastWriteTimeUtc(directory),
                SizeBytes = SumSize([directory, .. dimensions]),
                DimensionFolders = dimensions
            });
        }
        return entries.OrderByDescending(world => world.IsActive).ThenBy(world => world.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> SwitchActiveAsync(
        ServerDefinition server,
        string worldName,
        ServerState state,
        CancellationToken cancellationToken = default)
    {
        if (state != ServerState.Stopped)
            throw new InvalidOperationException("Stop the server before switching worlds.");
        var worldPath = Path.GetFullPath(Path.Combine(server.RootPath, worldName));
        EnsureChild(server.RootPath, worldPath);
        if (!File.Exists(Path.Combine(worldPath, "level.dat")))
            throw new InvalidDataException($"The selected folder is not a Minecraft world: {worldPath}");
        var content = await files.ReadTextAsync(server.RootPath, "server.properties", cancellationToken).ConfigureAwait(false);
        var document = ServerPropertiesDocument.Parse(content.Content);
        document.Set("level-name", worldName);
        await files.WriteTextAtomicAsync(server.RootPath, content with { Content = document.ToString() },
            createRecoveryCopy: true, cancellationToken).ConfigureAwait(false);
        return worldPath;
    }

    public async Task<WorldEntry> ImportAsync(
        ServerDefinition server,
        string zipPath,
        string worldName,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("The selected world ZIP does not exist.", zipPath);
        var safeName = ManagedServerInstaller.MakeSafeInstanceName(worldName);
        var finalPath = Path.Combine(server.RootPath, safeName);
        if (Directory.Exists(finalPath))
            throw new IOException($"A world folder already exists: {finalPath}");
        var staging = Path.Combine(server.RootPath, $".chunkpilot-world-{Guid.NewGuid():N}");
        try
        {
            await ManagedServerInstaller.ExtractZipSafeAsync(zipPath, staging, cancellationToken).ConfigureAwait(false);
            var levelFiles = Directory.EnumerateFiles(staging, "level.dat", SearchOption.AllDirectories).ToArray();
            if (levelFiles.Length != 1)
                throw new InvalidDataException(levelFiles.Length == 0
                    ? "The ZIP does not contain a Minecraft level.dat."
                    : "The ZIP contains multiple nested worlds. Import each world separately.");
            var worldRoot = Path.GetDirectoryName(levelFiles[0])!;
            if (worldRoot.Equals(staging, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(staging, finalPath);
            }
            else
            {
                Directory.Move(worldRoot, finalPath);
                Directory.Delete(staging, recursive: true);
            }
            return new WorldEntry
            {
                Name = safeName,
                FolderPath = finalPath,
                ModifiedAt = Directory.GetLastWriteTimeUtc(finalPath),
                SizeBytes = SumSize([finalPath]),
                DimensionFolders = DetectDimensions(server.RootPath, finalPath).ToArray()
            };
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public async Task<string> ExportAsync(
        ServerDefinition server,
        WorldEntry world,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(world.FolderPath))
            throw new DirectoryNotFoundException(world.FolderPath);
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory,
            $"{ManagedServerInstaller.MakeSafeInstanceName(world.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var roots = new[] { world.FolderPath }.Concat(world.DimensionFolders).Distinct(StringComparer.OrdinalIgnoreCase);
        var manifest = new List<object>();
        foreach (var root in roots)
        {
            var topName = Path.GetFileName(root);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"World export will not traverse reparse point: {file}");
                var entryName = $"{topName}/{Path.GetRelativePath(root, file).Replace('\\', '/')}";
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = entry.Open();
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                manifest.Add(new { path = entryName, size = input.Length, sha256 = Sha256(file) });
            }
        }
        var manifestEntry = archive.CreateEntry("chunkpilot-world-manifest.json", CompressionLevel.Optimal);
        await using (var output = manifestEntry.Open())
            await JsonSerializer.SerializeAsync(output, new
            {
                schema = 1,
                server = server.Name,
                world = world.Name,
                exportedAt = DateTimeOffset.UtcNow,
                files = manifest
            }, ProtocolJson.Options, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string ReadActiveWorld(string root)
    {
        var path = Path.Combine(root, "server.properties");
        if (!File.Exists(path))
            return "world";
        var document = ServerPropertiesDocument.Parse(File.ReadAllText(path));
        return document.Get("level-name") is { Length: > 0 } name ? name : "world";
    }

    private static IEnumerable<string> DetectDimensions(string serverRoot, string worldRoot)
    {
        var name = Path.GetFileName(worldRoot);
        foreach (var candidate in new[]
                 {
                     Path.Combine(serverRoot, $"{name}_nether"),
                     Path.Combine(serverRoot, $"{name}_the_end"),
                     Path.Combine(worldRoot, "DIM-1"),
                     Path.Combine(worldRoot, "DIM1"),
                     Path.Combine(worldRoot, "dimensions")
                 })
        {
            if (Directory.Exists(candidate))
                yield return candidate;
        }
    }

    private static long SumSize(IEnumerable<string> roots)
    {
        long total = 0;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                total += new FileInfo(file).Length;
        }
        return total;
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureChild(string root, string candidate)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(candidate);
    }
}

public sealed class WhitelistService
{
    private readonly SafeFileService files;

    public WhitelistService(SafeFileService files) => this.files = files;

    public async Task<IReadOnlyList<WhitelistEntry>> ReadAsync(
        ServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(server.RootPath, "whitelist.json");
        if (!File.Exists(path))
            return [];
        var content = await files.ReadTextAsync(server.RootPath, "whitelist.json", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content.Content);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("whitelist.json must contain a JSON array.");
        return document.RootElement.EnumerateArray().Select(item => new WhitelistEntry
        {
            Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Uuid = item.TryGetProperty("uuid", out var uuid) && Guid.TryParse(uuid.GetString(), out var parsed) ? parsed : null
        }).Where(entry => entry.Name.Length > 0).ToArray();
    }

    public async Task WriteAsync(
        ServerDefinition server,
        IReadOnlyList<WhitelistEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Any(entry => entry.Uuid is null))
            throw new InvalidOperationException("Stopped-server whitelist entries require real UUIDs. Start the server to let it resolve player names.");
        var existing = File.Exists(Path.Combine(server.RootPath, "whitelist.json"))
            ? await files.ReadTextAsync(server.RootPath, "whitelist.json", cancellationToken).ConfigureAwait(false)
            : new TextFileContent { RelativePath = "whitelist.json", Content = "[]\r\n" };
        var json = JsonSerializer.Serialize(entries.Select(entry => new
        {
            uuid = entry.Uuid!.Value.ToString("D"),
            name = entry.Name
        }), new JsonSerializerOptions { WriteIndented = true }) + existing.LineEnding;
        using (JsonDocument.Parse(json)) { }
        await files.WriteTextAtomicAsync(server.RootPath, existing with { Content = json },
            createRecoveryCopy: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UnifiedPlayerAccess>> ReadUnifiedAsync(
        ServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        var players = new Dictionary<string, UnifiedPlayerAccess>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await ReadAsync(server, cancellationToken).ConfigureAwait(false))
        {
            var key = PlayerKey(entry.Uuid, entry.Name);
            players[key] = new UnifiedPlayerAccess
            {
                Name = entry.Name,
                Uuid = entry.Uuid,
                Whitelisted = true
            };
        }

        foreach (var item in await ReadJsonArrayAsync(server, "ops.json", cancellationToken)
                     .ConfigureAwait(false))
        {
            var (key, current) = GetOrCreate(players, item);
            players[key] = current with { Operator = true };
        }

        foreach (var item in await ReadJsonArrayAsync(server, "banned-players.json", cancellationToken)
                     .ConfigureAwait(false))
        {
            var (key, current) = GetOrCreate(players, item);
            players[key] = current with
            {
                PlayerBanned = true,
                BanReason = StringValue(item, "reason"),
                // Only what the file records. A missing field stays empty rather than being filled in.
                BanSource = StringValue(item, "source"),
                BanCreatedAt = ParseMinecraftTime(StringValue(item, "created")),
                BanExpiresAt = ParseBanExpiration(StringValue(item, "expires"))
            };
        }

        // usercache.json contributes a player's name and UUID, and nothing else.
        //
        // It used to supply an activity time from the entry's "expiresOn" field. That field is when the
        // name-to-UUID cache entry stops being trusted - Minecraft writes it roughly a month in the
        // future - so every row showed a date that had not happened yet. Worse, the entry is created
        // by any name lookup, which includes whitelisting somebody: a player who had never connected
        // was shown as "seen" on a future date. Nothing in this file is evidence of a session, so
        // nothing in it is read as one. Real last-seen comes from the server's own join and leave
        // lines, which the Agent observes.
        foreach (var item in await ReadJsonArrayAsync(server, "usercache.json", cancellationToken)
                     .ConfigureAwait(false))
        {
            var (key, current) = GetOrCreate(players, item);
            players[key] = current;
        }

        foreach (var item in await ReadJsonArrayAsync(server, "banned-ips.json", cancellationToken)
                     .ConfigureAwait(false))
        {
            var address = StringValue(item, "ip");
            if (string.IsNullOrWhiteSpace(address))
                continue;
            var key = "ip:" + address;
            players.TryGetValue(key, out var current);
            players[key] = (current ?? new UnifiedPlayerAccess { Name = address }) with
            {
                IpAddress = address,
                IpBanned = true,
                BanReason = StringValue(item, "reason"),
                BanSource = StringValue(item, "source"),
                BanCreatedAt = ParseMinecraftTime(StringValue(item, "created")),
                BanExpiresAt = ParseBanExpiration(StringValue(item, "expires"))
            };
        }

        return players.Values
            .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(player => player.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string EnableCommand(bool enabled) => enabled ? "whitelist on" : "whitelist off";

    public static string AddCommand(string playerName) =>
        $"whitelist add {ValidatePlayerName(playerName)}";

    public static string RemoveCommand(string playerName) =>
        $"whitelist remove {ValidatePlayerName(playerName)}";

    public static string ReloadCommand() => "whitelist reload";

    private static string ValidatePlayerName(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        if (playerName.Length is > 16 || playerName.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("Minecraft player names use 1-16 letters, numbers, or underscores.", nameof(playerName));
        return playerName;
    }

    private async Task<IReadOnlyList<JsonElement>> ReadJsonArrayAsync(
        ServerDefinition server,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(server.RootPath, relativePath)))
            return [];
        var content = await files.ReadTextAsync(server.RootPath, relativePath, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(content.Content);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{relativePath} must contain a JSON array.");
        return document.RootElement.EnumerateArray()
            .Select(item => item.Clone()).ToArray();
    }

    private static (string Key, UnifiedPlayerAccess Player) GetOrCreate(
        IDictionary<string, UnifiedPlayerAccess> players,
        JsonElement item)
    {
        var name = StringValue(item, "name");
        var uuid = Guid.TryParse(StringValue(item, "uuid"), out var parsed) ? parsed : (Guid?)null;
        var key = PlayerKey(uuid, name);
        players.TryGetValue(key, out var player);
        return (key, player ?? new UnifiedPlayerAccess { Name = name, Uuid = uuid });
    }

    private static string PlayerKey(Guid? uuid, string name) =>
        uuid is { } id ? "uuid:" + id.ToString("D") : "name:" + name;

    private static string StringValue(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static DateTimeOffset? ParseBanExpiration(string value) =>
        value.Equals("forever", StringComparison.OrdinalIgnoreCase)
            ? null
            : ParseMinecraftTime(value);

    private static DateTimeOffset? ParseMinecraftTime(string value) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var result)
            ? result
            : null;
}
