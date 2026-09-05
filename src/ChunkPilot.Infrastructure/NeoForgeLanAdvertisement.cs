using System.Text;
using System.Text.RegularExpressions;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// NeoForge's dedicated-server LAN advertisement is independent of server-ip. Apply its documented
/// networking-only switch to new loopback creations and disposable validation worlds. Never allow
/// a UDP observation based on this setting; the shared endpoint check remains authoritative.
/// </summary>
internal static class NeoForgeLanAdvertisement
{
    private const string FileName = "neoforge-server.toml";
    private const string Key = "advertiseDedicatedServerToLan";
    private const int MaximumBytes = 64 * 1024;

    public static async Task DisableForNewCreationAsync(string root, string worldName, CancellationToken token)
    {
        var worldConfig = ConfigPath(root, worldName, "serverconfig", FileName);
        var destination = File.Exists(worldConfig) ? worldConfig : ConfigPath(root, "config", FileName);
        var source = FindEffectiveConfig(root, worldName);
        await WriteDisabledAsync(root, source, destination, token).ConfigureAwait(false);
    }

    public static Task PrepareValidationWorldAsync(string root, string intendedWorld, string validationWorld,
        CancellationToken token) => WriteDisabledAsync(root, FindEffectiveConfig(root, intendedWorld),
            ConfigPath(root, validationWorld, "serverconfig", FileName), token);

    private static string? FindEffectiveConfig(string root, string worldName)
    {
        foreach (var path in new[] { ConfigPath(root, worldName, "serverconfig", FileName),
                     ConfigPath(root, "config", FileName), ConfigPath(root, "defaultconfigs", FileName) })
            if (File.Exists(path)) return path;
        return null;
    }

    private static string ConfigPath(string root, params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        CreationPathSafety.EnsureWithin(root, path);
        var ancestor = Path.GetDirectoryName(path)!;
        while (!CreationStagingSafety.EntryExists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("The configuration has no existing ancestor.");
        CreationStagingSafety.EnsureNoReparseTraversal(ancestor);
        if (CreationStagingSafety.EntryExists(path) &&
            (Directory.Exists(path) || CreationPathSafety.IsReparsePoint(path)))
            throw new IOException("The NeoForge networking configuration is not a regular file.");
        return path;
    }

    private static async Task WriteDisabledAsync(string root, string? source, string destination, CancellationToken token)
    {
        byte[] original = [];
        if (source is not null)
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (input.Length > MaximumBytes)
                throw new InvalidDataException("The NeoForge networking configuration exceeds the bounded edit limit.");
            original = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(original, token).ConfigureAwait(false);
        }
        var updated = Disable(original);
        CreationStagingSafety.CreateDirectoryPath(root, Path.GetDirectoryName(destination)!);
        var partial = destination + ".chunkpilot-" + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous))
                await output.WriteAsync(updated, token).ConfigureAwait(false);
            _ = ConfigPath(root, Path.GetRelativePath(root, destination));
            File.Move(partial, destination, overwrite: true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    internal static byte[] Disable(byte[] original)
    {
        if (original.Length > MaximumBytes) throw new InvalidDataException("NeoForge configuration is too large.");
        var bom = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var text = new UTF8Encoding(false, true).GetString(original.AsSpan(bom ? 3 : 0));
        var replacement = -1;
        var replacementLength = 0;
        // This is deliberately a bounded editor for NeoForge's flat scalar configuration, not a
        // permissive TOML parser. Ambiguous/multiline/table shapes require review, never data loss.
        foreach (Match line in Regex.Matches(text, @"[^\r\n]+", RegexOptions.CultureInvariant,
                     TimeSpan.FromSeconds(1)))
        {
            var content = line.Value.TrimStart();
            if (content.Length == 0 || content.StartsWith('#')) continue;
            var scalar = Regex.Match(line.Value,
                "^[ \\t]*(?<key>[A-Za-z_][A-Za-z0-9_]*|\"[A-Za-z_][A-Za-z0-9_]*\"|'[A-Za-z_][A-Za-z0-9_]*')[ \\t]*=[ \\t]*(?<value>true|false|[+-]?[0-9]+|\"(?:[^\"\\\\\\r\\n]|\\\\[^\\r\\n])*\"|'[^'\\r\\n]*')[ \\t]*(?:#.*)?$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!scalar.Success)
                throw new InvalidDataException("The NeoForge LAN advertisement setting could not be safely edited; review its configuration.");
            if (scalar.Groups["key"].Value.Trim('\'', '"') != Key) continue;
            if (replacement >= 0 || scalar.Groups["value"].Value is not ("true" or "false"))
                throw new InvalidDataException("The NeoForge LAN advertisement setting is duplicated or is not a Boolean.");
            replacement = line.Index + scalar.Groups["value"].Index;
            replacementLength = scalar.Groups["value"].Length;
        }
        if (replacement >= 0) text = text.Remove(replacement, replacementLength).Insert(replacement, "false");
        else
        {
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            if (text.Length > 0 && !text.EndsWith('\n') && !text.EndsWith('\r')) text += newline;
            text += "# ChunkPilot: this computer only; disable dedicated-server LAN advertisement." + newline +
                    Key + " = false" + newline;
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        return bom ? [0xef, 0xbb, 0xbf, .. bytes] : bytes;
    }
}
