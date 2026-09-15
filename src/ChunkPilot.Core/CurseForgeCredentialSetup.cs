namespace ChunkPilot.Core;

/// <summary>Native-host-only request. Neither plaintext nor protected credentials enter the renderer.</summary>
public sealed record CurseForgeCredentialChangeRequest
{
    public UiSessionCredential Session { get; init; } = new();
    public string ProtectedApiKey { get; init; } = "";
}
