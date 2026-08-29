namespace ChunkPilot.Core;

/// <summary>
/// Development-certification-only request to arm one exact server-pack update validation failure.
/// The Agent accepts this only when its isolated certification authority was enabled at process
/// startup and the caller also proves the current exact UI session.
/// </summary>
public sealed record ArmCertificationUpdateFailureRequest
{
    public Guid ServerId { get; init; }
    public Guid OperationId { get; init; }
    public string Token { get; init; } = "";
    public Guid SessionId { get; init; }
    public string SessionCapability { get; init; } = "";
}
