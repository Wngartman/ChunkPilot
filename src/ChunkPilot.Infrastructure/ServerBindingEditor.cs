using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Edits only an owned server's bind address. The Agent supplies stopped/Start serialization.</summary>
public static class ServerBindingEditor
{
    public sealed record Mutation(TextFileContent Original, string AppliedSha256);

    public static async Task<Mutation> ApplyAsync(ServerDefinition server, NetworkMode mode,
        SafeFileService files, CancellationToken cancellationToken)
    {
        var unavailable = ServerBindingApplyPolicy.UnavailableReason(server);
        if (unavailable.Length > 0) throw new InvalidOperationException(unavailable);
        var bind = ServerBindingApplyPolicy.BindAddress(mode);
        var saved = await ServerConnectionObserver.ReadSavedAsync(server, files, cancellationToken).ConfigureAwait(false);
        if (!saved.Known || saved.Port != server.Port)
            throw new InvalidOperationException("The saved binding or port could not be verified against this server. Review server.properties before applying access.");
        var original = await files.ReadTextAsync(server.RootPath, "server.properties", cancellationToken).ConfigureAwait(false);
        if (original.Content.Length > 64 * 1024)
            throw new InvalidOperationException("The server configuration exceeds the safe binding-edit limit.");
        var document = ServerPropertiesDocument.Parse(original.Content);
        if (!int.TryParse(document.Get("server-port") ?? "25565", NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port != server.Port)
            throw new InvalidOperationException("The saved port changed. Reload the server before applying access.");
        document.Set("server-ip", bind);
        var changed = original with { Content = document.ToString() };
        var encoding = Encoding.GetEncoding(changed.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var bytes = encoding.GetBytes(changed.Content);
        var preamble = changed.HasBom ? encoding.GetPreamble() : [];
        var appliedHash = Convert.ToHexString(SHA256.HashData([.. preamble, .. bytes]));
        await files.WriteTextAtomicIfUnchangedAsync(server.RootPath, changed, cancellationToken).ConfigureAwait(false);
        return new(original, appliedHash);
    }

    public static async Task RollbackAsync(string root, Mutation mutation, SafeFileService files, CancellationToken cancellationToken)
    {
        // Never overwrite a configuration the failed server or an external editor subsequently changed.
        await files.WriteTextAtomicIfUnchangedAsync(root,
            mutation.Original with { LoadedSha256 = mutation.AppliedSha256 }, cancellationToken).ConfigureAwait(false);
    }
}
