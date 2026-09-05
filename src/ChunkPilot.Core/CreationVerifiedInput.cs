namespace ChunkPilot.Core;

public sealed record CreationRecoveryRequest(Guid OperationId, int ExpectedRetryGeneration);

/// <summary>Local operation ownership evidence, not a persisted provider response or general cache key.</summary>
public sealed record CreationVerifiedInput
{
    public const int CurrentPolicyVersion = 1;
    public int PolicyVersion { get; init; } = CurrentPolicyVersion;
    public Guid OperationId { get; init; }
    public Guid ServerId { get; init; }
    public string ProjectId { get; init; } = "";
    public string ClientFileId { get; init; } = "";
    public string ServerFileId { get; init; } = "";
    public long SizeBytes { get; init; }
    public string LocalSha256 { get; init; } = "";
    public DateTimeOffset CompletedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>Exact user choices and already verified runtime requirements. No key, URL or provider label.</summary>
public sealed record CreationRetrySettings
{
    public string ProjectId { get; init; } = "";
    public string ClientFileId { get; init; } = "";
    public string ServerFileId { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public int RequiredJavaMajor { get; init; }
    public int MinimumRamMb { get; init; }
    public int MaximumRamMb { get; init; }
    public int MaxPlayers { get; init; }
    public int Port { get; init; }
    public VanillaNetworkingPreference NetworkingPreference { get; init; }
    public CreationWorldSource? InitialWorld { get; init; }
}
