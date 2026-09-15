using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Agent;

public sealed partial class ManagedServer
{
    public async Task<OperationResult> SaveNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.ServerId != Definition.Id || !Enum.IsDefined(configuration.Mode))
            throw new ArgumentException("The networking preference does not identify this server and a supported mode.");
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.UpsertNetworkConfigurationAsync(configuration with
            {
                JavaPort = Definition.Port,
                PublicAddressExternallyConfirmed = false
            }, cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok("Connection preference saved. Apply the server binding separately; no router or firewall settings were changed.");
        }
        finally { operationGate.Release(); }
    }

    public async Task<OperationResult> ApplyServerBindingAsync(ApplyServerBindingRequest request, SafeFileService files,
        Action? demandSession = null,
        CancellationToken cancellationToken = default)
    {
        if (request.ServerId != Definition.Id || !request.Confirmed)
            throw new InvalidOperationException("Applying a server binding requires explicit confirmation for this server.");
        demandSession?.Invoke();
        _ = ServerBindingApplyPolicy.BindAddress(request.Mode);
        var unavailable = ServerBindingApplyPolicy.UnavailableReason(Definition);
        if (unavailable.Length > 0) throw new InvalidOperationException(unavailable);
        await RunExclusiveRestartableDataOperationAsync("applying the server binding", request.RestartIfRunning,
            async token =>
            {
                if (State != ServerState.Stopped || HasDetachedProcess || HasExactOwnedProcessAlive())
                    throw new InvalidOperationException("The server must be fully stopped before its binding can change.");
                var endpoints = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                if (endpoints.GetActiveTcpListeners().Any(endpoint => endpoint.Port == Definition.Port) ||
                    endpoints.GetActiveUdpListeners().Any(endpoint => endpoint.Port == Definition.Port))
                    throw new InvalidOperationException("The configured server port is still in use. Resolve its owner before changing the binding.");
                var current = await store.GetNetworkConfigurationAsync(Definition.Id, token).ConfigureAwait(false);
                var mode = current?.Mode ?? VanillaNetworkingPreferencePolicy.ToNetworkMode(Definition.CreationNetworkingPreference);
                if (mode != request.Mode)
                    throw new InvalidOperationException("The selected connection preference changed. Review it again before applying.");
                demandSession?.Invoke();
                var mutation = await ServerBindingEditor.ApplyAsync(Definition, mode, files, token).ConfigureAwait(false);
                connectionEvidence = connectionEvidence with { Saved = new(), Listener = new() };
                lastConnectionObservation = default;
                return mutation;
            },
            (mutation, token) => ServerBindingEditor.RollbackAsync(Definition.RootPath, mutation, files, token),
            cancellationToken).ConfigureAwait(false);
        await RefreshConnectionEvidenceAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(State == ServerState.Running
            ? "Server binding applied and startup completed. Router and Windows Firewall rules were not changed; other-device reachability is not verified."
            : "Server binding applied. The server remains stopped; start it when ready. Router and Windows Firewall rules were not changed.");
    }
}
