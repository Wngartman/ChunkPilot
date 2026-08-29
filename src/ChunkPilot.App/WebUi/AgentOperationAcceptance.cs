using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.App.WebUi;

/// <summary>
/// Reconciles local named-pipe acknowledgement loss without minting a second operation identity.
/// The Agent-side begin routes are idempotent for the full exact request, so reconciliation replays
/// that request rather than trusting a progress snapshot that cannot prove every submitted field.
/// </summary>
internal static class AgentOperationAcceptance
{
    public static async Task<Guid> BeginModpackCreationAsync(
        ModpackCreationPlan plan,
        Func<CancellationToken, Task<InstallOperationRequest>> begin,
        Func<CancellationToken, Task<ModpackCreationCancellationFenceResult>> cancelOrFence,
        CancellationToken cancellationToken)
    {
        if (plan.OperationId == Guid.Empty)
            throw new ArgumentException(
                "A client-generated modpack creation operation identity is required.", nameof(plan));
        var accepted = await BeginOrReconcileAsync(
            begin,
            async token =>
            {
                var result = await cancelOrFence(token).ConfigureAwait(false);
                if (!result.FenceEstablished)
                {
                    if (result.BlockedBeforeStart || result.Operation is not null)
                        throw new InvalidDataException(
                            "The Agent returned contradictory modpack cancellation-fence evidence.");
                    return null;
                }
                var operation = result.Operation ?? throw new InvalidDataException(
                    "The Agent established a modpack cancellation fence without an operation record.");
                if (result.BlockedBeforeStart &&
                    (!operation.IsTerminal || operation.Success is not false ||
                     operation.Progress.Stage != CreationStage.Cancelled))
                    throw new InvalidDataException(
                        "The Agent's pre-start modpack cancellation record is not terminal-cancelled.");
                return new InstallOperationRequest(operation.OperationId);
            },
            response =>
            {
                if (response.OperationId != plan.OperationId)
                    throw new InvalidDataException(
                        "The Agent reconciled creation under an unexpected operation identity.");
                return response;
            },
            cancellationToken).ConfigureAwait(false);
        return accepted.OperationId;
    }

    public static Task<ManagedContentOperationSnapshot> BeginManagedContentInstallAsync(
        BeginManagedContentInstallRequest request,
        Func<CancellationToken, Task<ManagedContentOperationSnapshot>> begin,
        Func<CancellationToken, Task<ManagedContentCancellationFenceResult>> cancelOrFence,
        CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty)
            throw new ArgumentException(
                "A client-generated managed-content operation identity is required.", nameof(request));
        return BeginOrReconcileAsync(
            begin,
            async token =>
            {
                var result = await cancelOrFence(token).ConfigureAwait(false);
                if (!result.FenceEstablished)
                {
                    if (result.BlockedBeforeStart || result.Operation is not null)
                        throw new InvalidDataException(
                            "The Agent returned contradictory managed-content cancellation-fence evidence.");
                    return null;
                }
                var operation = result.Operation ?? throw new InvalidDataException(
                    "The Agent established a managed-content cancellation fence without an operation record.");
                if (result.BlockedBeforeStart &&
                    (!operation.IsTerminal || operation.Success is not false ||
                     operation.Progress.Stage != ManagedContentOperationStage.Cancelled))
                    throw new InvalidDataException(
                        "The Agent's pre-start managed-content cancellation record is not terminal-cancelled.");
                return operation;
            },
            snapshot => ValidateManagedContentAcceptance(request, snapshot),
            cancellationToken);
    }

    private static ManagedContentOperationSnapshot ValidateManagedContentAcceptance(
        BeginManagedContentInstallRequest request,
        ManagedContentOperationSnapshot snapshot)
    {
        var expectedKind = request.IncludeDependencies
            ? ManagedContentOperationKind.InstallAddonPlan
            : ManagedContentOperationKind.InstallAddon;
        if (snapshot.OperationId != request.OperationId ||
            snapshot.ServerId != request.ServerId ||
            snapshot.Kind != expectedKind ||
            !snapshot.Provider.Equals(request.Provider.ToString(), StringComparison.Ordinal) ||
            !snapshot.ProjectId.Equals(request.ProjectId, StringComparison.Ordinal) ||
            !snapshot.VersionId.Equals(request.VersionId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The Agent reconciled a different managed-content request under this operation identity.");
        return snapshot;
    }

    private static async Task<T> BeginOrReconcileAsync<T>(
        Func<CancellationToken, Task<T>> begin,
        Func<CancellationToken, Task<T?>> cancelOrFence,
        Func<T, T> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var accepted = validate(await begin(cancellationToken).ConfigureAwait(false));
            return cancellationToken.IsCancellationRequested
                ? validate(await WaitForCancellationFenceAsync(cancelOrFence).ConfigureAwait(false))
                : accepted;
        }
        catch (Exception exception) when (IsAmbiguousTransportFailure(exception))
        {
            // The request may already have crossed the pipe. Replaying the full exact request makes
            // the Agent prove every submitted field before this App accepts the operation.
        }

        var attempt = 0;
        while (true)
        {
            attempt++;
            if (cancellationToken.IsCancellationRequested)
                return validate(await WaitForCancellationFenceAsync(cancelOrFence).ConfigureAwait(false));

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var accepted = validate(await begin(CancellationToken.None).ConfigureAwait(false));
                    return cancellationToken.IsCancellationRequested
                        ? validate(await WaitForCancellationFenceAsync(cancelOrFence).ConfigureAwait(false))
                        : accepted;
                }
                catch (Exception exception) when (IsAmbiguousTransportFailure(exception))
                {
                    // Query again. The exact-id replay is idempotent if this response was also lost.
                }
            }

            var delay = Math.Min(1_000, 25 * (1 << Math.Min(attempt, 5)));
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<T> WaitForCancellationFenceAsync<T>(
        Func<CancellationToken, Task<T?>> cancelOrFence)
        where T : class
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var fenced = await cancelOrFence(CancellationToken.None).ConfigureAwait(false);
                if (fenced is not null)
                    return fenced;
            }
            catch (Exception exception) when (IsAmbiguousTransportFailure(exception))
            {
                // A lost fence acknowledgement is replayed by exact identity. Until the Agent
                // acknowledges its atomic reservation, local cancellation is not terminal evidence.
            }
            var delay = Math.Min(1_000, 25 * (1 << Math.Min(attempt, 5)));
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static bool IsAmbiguousTransportFailure(Exception exception) =>
        exception is IOException or TimeoutException or OperationCanceledException or JsonException;
}
