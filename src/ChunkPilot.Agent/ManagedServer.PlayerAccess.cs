using System.Net.NetworkInformation;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Agent;

public sealed partial class ManagedServer
{
    public bool CanManageAccessWhileStopped => State == ServerState.Stopped && !HasDetachedProcess &&
        !HasExactOwnedProcessAlive() && !IsRestartInProgress && StoppedPlayerAccessService.Supports(Definition);

    private async Task<OperationResult> ModerateStoppedPlayerCoreAsync(PlayerModerationAction action,
        string name, string reason, SafeFileService files, CancellationToken cancellationToken)
    {
        try
        {
            return await new StoppedPlayerAccessService(files).ApplyAsync(Definition, action, name, reason,
                VerifyStoppedDataAccessAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
                                          InvalidOperationException or UnauthorizedAccessException or NetworkInformationException)
        {
            return OperationResult.Fail($"Access was not changed: {exception.Message}");
        }
    }

    private async Task VerifyStoppedDataAccessAsync(CancellationToken cancellationToken)
    {
        if (State != ServerState.Stopped || HasDetachedProcess || HasExactOwnedProcessAlive() ||
            await store.GetProcessIdentityAsync(Definition.Id, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("The server is not proven stopped. Resolve its process state before accessing server data.");
        var endpoints = IPGlobalProperties.GetIPGlobalProperties();
        if (endpoints.GetActiveTcpListeners().Any(endpoint => endpoint.Port == Definition.Port) ||
            endpoints.GetActiveUdpListeners().Any(endpoint => endpoint.Port == Definition.Port))
            throw new InvalidOperationException("The configured server port is still in use. No server data was accessed or changed.");
    }
}
