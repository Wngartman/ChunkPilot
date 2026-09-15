using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Single-file access changes. Caller must hold the server lifecycle gate and prove stopped.</summary>
public sealed class StoppedPlayerAccessService(SafeFileService files)
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly string[] IdentityFiles = ["whitelist.json", "ops.json", "banned-players.json", "usercache.json"];

    public static bool Supports(ServerDefinition server)
    {
        if (server.GameKind != ServerGameKind.Minecraft ||
            server.Ecosystem is ServerEcosystem.Unknown or ServerEcosystem.Custom ||
            !Version.TryParse(server.MinecraftVersion, out var version) || version < new Version(1, 7, 6) ||
            string.IsNullOrWhiteSpace(server.RootPath) || string.IsNullOrWhiteSpace(server.WorkingDirectory))
            return false;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.RootPath)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.WorkingDirectory)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    public async Task<OperationResult> ApplyAsync(ServerDefinition server, PlayerModerationAction action,
        string playerName, string reason, Func<CancellationToken, Task> verifyStopped,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifyStopped);
        if (!Supports(server))
            return OperationResult.Fail("Stopped access editing requires a known Java 1.7.6+ server with its normal root configuration.");
        var name = PlayerModerationPolicy.ValidatePlayerName(playerName);
        var targetName = action switch
        {
            PlayerModerationAction.AddToWhitelist or PlayerModerationAction.RemoveFromWhitelist => "whitelist.json",
            PlayerModerationAction.GrantOperator or PlayerModerationAction.RemoveOperator => "ops.json",
            PlayerModerationAction.Ban or PlayerModerationAction.Pardon => "banned-players.json",
            _ => null
        };
        if (targetName is null)
            return OperationResult.Fail("Kicking requires a running server. Nothing was changed.");

        await verifyStopped(cancellationToken).ConfigureAwait(false);
        var sources = new Dictionary<string, (TextFileContent Content, JsonArray Entries)>();
        var identities = new HashSet<Guid>();
        foreach (var file in IdentityFiles)
        {
            var content = await ReadAsync(server.RootPath, file, cancellationToken).ConfigureAwait(false);
            var entries = ParseEntries(content);
            sources.Add(file, (content, entries));
            foreach (var entry in entries.Cast<JsonObject>().Where(entry =>
                         string.Equals(entry["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
            {
                if (file == "usercache.json" &&
                    (!DateTimeOffset.TryParse(entry["expiresOn"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal, out var expiry) || expiry <= DateTimeOffset.UtcNow))
                    continue;
                identities.Add(Guid.Parse(entry["uuid"]!.GetValue<string>()));
            }
        }
        if (identities.Count != 1)
            return OperationResult.Fail(identities.Count == 0
                ? "This server has no current exact UUID for that player. Start it to resolve the name, then stop it to edit access. No UUID was invented and nothing was changed."
                : "This name matches conflicting UUIDs in this server's files. Resolve the identity conflict before changing access. Nothing was changed.");

        var uuid = identities.Single();
        var (targetContent, target) = sources[targetName];
        var matches = target.Cast<JsonObject>().Where(entry => Guid.Parse(entry["uuid"]!.GetValue<string>()) == uuid).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException($"{targetName} contains duplicate UUID records. The original file was preserved.");
        var remove = action is PlayerModerationAction.RemoveFromWhitelist or PlayerModerationAction.RemoveOperator or PlayerModerationAction.Pardon;
        if (remove && matches.Length == 0 || !remove && matches.Length == 1)
            return OperationResult.Ok("The saved access list already has that state. Nothing needed changing.");

        if (remove)
            target.Remove(matches[0]);
        else
        {
            var entry = new JsonObject { ["uuid"] = uuid.ToString("D"), ["name"] = name };
            if (action == PlayerModerationAction.GrantOperator)
            {
                var properties = await ReadAsync(server.RootPath, "server.properties", cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(properties.LoadedSha256))
                    throw new InvalidDataException("server.properties is missing; operator permission level is unknown.");
                var value = ServerPropertiesDocument.Parse(properties.Content).Get("op-permission-level") ?? "4";
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var level) || level is < 1 or > 4)
                    throw new InvalidDataException("The saved operator permission level is invalid. Nothing was changed.");
                entry["level"] = level;
                entry["bypassesPlayerLimit"] = false;
                sources.Add("server.properties", (properties, new JsonArray()));
            }
            if (action == PlayerModerationAction.Ban)
            {
                entry["created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss '+0000'", CultureInfo.InvariantCulture);
                entry["source"] = "ChunkPilot";
                entry["expires"] = "forever";
                entry["reason"] = string.IsNullOrWhiteSpace(reason) ? "Banned by an operator." :
                    new string(reason.Where(character => !char.IsControl(character)).Take(120).ToArray());
            }
            target.Add(entry);
        }

        // UUID provenance and permission-level input must still be the exact files we inspected.
        foreach (var (sourceName, source) in sources)
        {
            var current = await ReadAsync(server.RootPath, sourceName, cancellationToken).ConfigureAwait(false);
            if (!current.LoadedSha256.Equals(source.Content.LoadedSha256, StringComparison.Ordinal))
                throw new IOException("Player access or identity files changed outside ChunkPilot. Reload before retrying.");
        }
        await verifyStopped(cancellationToken).ConfigureAwait(false);
        var json = target.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", targetContent.LineEnding, StringComparison.Ordinal) + targetContent.LineEnding;
        var receipt = await files.WriteTextAtomicIfUnchangedAsync(server.RootPath,
            targetContent with { Content = json }, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"Saved {targetName} for {name}. The stopped server will read it on its next start.", receipt.RecoveryPath);
    }

    private async Task<TextFileContent> ReadAsync(string root, string relative, CancellationToken cancellationToken)
    {
        var path = files.ResolveWithinRoot(root, relative, mustExist: false);
        if (!File.Exists(path))
            return new TextFileContent { RelativePath = relative, Content = "[]", LineEnding = Environment.NewLine };
        if (new FileInfo(path).Length > MaximumBytes)
            throw new InvalidDataException($"{relative} exceeds the safe access-editing size limit. It was preserved.");
        var content = await files.ReadTextAsync(root, relative, cancellationToken).ConfigureAwait(false);
        if (content.Content.Length > MaximumBytes)
            throw new InvalidDataException($"{relative} changed or exceeds the safe access-editing size limit.");
        return content;
    }

    private static JsonArray ParseEntries(TextFileContent content)
    {
        if (JsonNode.Parse(content.Content) is not JsonArray array || array.Count > 10_000)
            throw new InvalidDataException($"{content.RelativePath} must contain a bounded JSON array. It was preserved.");
        foreach (var node in array)
        {
            if (node is not JsonObject entry ||
                !entry.TryGetPropertyValue("name", out var nameNode) || nameNode is not JsonValue nameValue ||
                !nameValue.TryGetValue<string>(out var name) || !PlayerModerationPolicy.IsValidPlayerName(name) ||
                !entry.TryGetPropertyValue("uuid", out var uuidNode) || uuidNode is not JsonValue uuidValue ||
                !uuidValue.TryGetValue<string>(out var uuidText) || !Guid.TryParse(uuidText, out var uuid) || uuid == Guid.Empty)
                throw new InvalidDataException($"{content.RelativePath} contains an invalid name or UUID. It was preserved.");
        }
        return array;
    }
}
