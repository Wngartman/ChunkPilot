using ChunkPilot.Core;

namespace ChunkPilot.Agent;

/// <summary>
/// One immutable authorization carried all the way through a serialized router operation. The
/// validator is deliberately re-run under the router gate immediately before every durable or wire
/// mutation; an earlier point-in-time decision is never authority for later work.
/// </summary>
public sealed class RouterOperationAuthority
{
    private readonly Func<bool> validate;
    private readonly Func<bool> validateRevokedCleanup;

    private RouterOperationAuthority(
        Guid serverId,
        PublicConnectivityLeaseIdentity expectedLease,
        long lifecycleEpoch,
        bool mayEstablish,
        bool exactLeaseCleanup,
        Func<bool> validate,
        Func<bool> validateRevokedCleanup)
    {
        ServerId = serverId;
        ExpectedLease = expectedLease;
        LifecycleEpoch = lifecycleEpoch;
        MayEstablish = mayEstablish;
        ExactLeaseCleanup = exactLeaseCleanup;
        this.validate = validate;
        this.validateRevokedCleanup = validateRevokedCleanup;
    }

    public Guid ServerId { get; }
    public PublicConnectivityLeaseIdentity ExpectedLease { get; }
    public long LifecycleEpoch { get; }
    public bool MayEstablish { get; }
    public bool ExactLeaseCleanup { get; }

    public static RouterOperationAuthority Exposure(
        PublicConnectivityLeaseIdentity lease,
        Func<bool> validate,
        Func<bool> validateRevokedCleanup) =>
        new(lease.ServerId, lease, lease.LifecycleEpoch, mayEstablish: true,
            exactLeaseCleanup: false, validate, validateRevokedCleanup);

    public static RouterOperationAuthority LeaseCleanup(
        PublicConnectivityLeaseIdentity lease,
        long lifecycleEpoch,
        Func<bool> validate) =>
        new(lease.ServerId, lease, lifecycleEpoch, mayEstablish: false,
            exactLeaseCleanup: true, validate, validate);

    public static RouterOperationAuthority StaleCleanup(
        RouterMappingRecord record,
        long lifecycleEpoch,
        Func<bool> validate) =>
        new(record.ServerId,
            new PublicConnectivityLeaseIdentity
            {
                ServerId = record.ServerId,
                LeaseId = record.PublicLeaseId,
                Generation = record.PublicLeaseGeneration,
                LifecycleEpoch = record.PublicLifecycleEpoch
            },
            lifecycleEpoch, mayEstablish: false, exactLeaseCleanup: false, validate, validate);

    /// <summary>
    /// Compatibility authority for the isolated router protocol fixtures. Production composition
    /// never calls this path; architecture tests enforce that boundary.
    /// </summary>
    public static RouterOperationAuthority IsolatedFixture(Guid serverId, bool mayEstablish = true) =>
        new(serverId,
            mayEstablish
                ? new PublicConnectivityLeaseIdentity
                {
                    ServerId = serverId,
                    LeaseId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    Generation = 1,
                    LifecycleEpoch = 1
                }
                : new PublicConnectivityLeaseIdentity(),
            1, mayEstablish, exactLeaseCleanup: false, static () => true, static () => true);

    public void Demand(RouterMappingRecord? record, string checkpoint)
    {
        if (!validate())
            throw new OperationCanceledException(
                $"Router work for {ServerId} became stale before {checkpoint}.");
        if (MayEstablish && !ExpectedLease.IsPresent)
            throw new UnauthorizedAccessException("Router establishment requires an exact lease generation.");
        if (record is null)
            return;

        if (ExactLeaseCleanup && record.PublicLeaseGeneration > 0 &&
            (record.PublicLeaseId != ExpectedLease.LeaseId ||
             record.PublicLeaseGeneration != ExpectedLease.Generation ||
             record.PublicLifecycleEpoch != ExpectedLease.LifecycleEpoch))
        {
            throw new OperationCanceledException(
                $"Router cleanup for {ServerId} cannot affect a different lease generation.");
        }
        if (MayEstablish && record.PublicLeaseGeneration > 0 && record.DirectInternetEnabled &&
            (record.PublicLeaseId != ExpectedLease.LeaseId ||
             record.PublicLeaseGeneration != ExpectedLease.Generation ||
             record.PublicLifecycleEpoch != ExpectedLease.LifecycleEpoch))
        {
            throw new OperationCanceledException(
                $"Router establishment for {ServerId} cannot overwrite a different lease generation.");
        }
    }

    public RouterMappingRecord Bind(RouterMappingRecord record)
    {
        if (!MayEstablish)
            return record;
        return record with
        {
            PublicLeaseId = ExpectedLease.LeaseId,
            PublicLeaseGeneration = ExpectedLease.Generation,
            PublicLifecycleEpoch = ExpectedLease.LifecycleEpoch
        };
    }

    /// <summary>
    /// A successful wire result that arrives after revocation may be retained only as exact cleanup
    /// evidence for that same latest generation. It is never accepted as active intent.
    /// </summary>
    public bool CanRetainRevokedCleanupEvidence(RouterMappingRecord record) =>
        MayEstablish && ExpectedLease.IsPresent && validateRevokedCleanup() &&
        record.PublicLeaseId == ExpectedLease.LeaseId &&
        record.PublicLeaseGeneration == ExpectedLease.Generation &&
        record.PublicLifecycleEpoch == ExpectedLease.LifecycleEpoch;

    public void DemandPersistence(RouterMappingRecord record, string checkpoint)
    {
        if (!record.DirectInternetEnabled && !record.ConsentGranted && record.RemovalPending &&
            CanRetainRevokedCleanupEvidence(record))
            return;
        Demand(record, checkpoint);
    }
}
