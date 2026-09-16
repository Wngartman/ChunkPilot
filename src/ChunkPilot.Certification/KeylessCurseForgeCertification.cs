using System.Text.Json;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

/// <summary>Fails closed unless a disposable, credential-free client reaches the real service path.</summary>
internal static class KeylessCurseForgeCertification
{
    public static async Task VerifyAsync(CurseForgeApiClient api, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (api.AccessMode != CurseForgeAccessMode.ApplicationService)
            throw new InvalidOperationException("This check requires a build with the CurseForge application service configured.");
        if (api.HasCredential)
            throw new InvalidOperationException("The no-key acceptance check requires a clean disposable credential store.");
        using var document = await api.GetJsonAsync("/v1/games/432", cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("id", out var id) || !id.TryGetInt32(out var gameId) || gameId != 432)
            throw new InvalidDataException("The application service did not return the official Minecraft identity.");
        if (api.HasCredential)
            throw new InvalidOperationException("The application-service check unexpectedly configured a personal credential.");
    }
}
