using System.Collections.Concurrent;
using ChunkPilot.Core;

namespace ChunkPilot.Agent;

/// <summary>In-memory lease authority. Nothing here is restored after an Agent restart.</summary>
public sealed class PublicConnectivityLeaseRegistry
{
    private readonly object sync = new();
    private readonly UiSessionAuthority sessions;
    private readonly ConcurrentDictionary<Guid, PublicConnectivityLeaseIdentity> active = new();
    private readonly ConcurrentDictionary<Guid, PublicConnectivityLeaseIdentity> revoked = new();
    private readonly ConcurrentDictionary<Guid, long> generations = new();
    private readonly TimeProvider clock;
    private bool applicationExitSnapshotStarted;

    public PublicConnectivityLeaseRegistry(UiSessionAuthority sessions, TimeProvider? clock = null)
    {
        this.sessions = sessions;
        this.clock = clock ?? TimeProvider.System;
    }

    public PublicConnectivityLeaseIdentity Create(Guid serverId, UiSessionCredential session)
    {
        sessions.Demand(session, "Creating public connectivity");
        lock (sync)
        {
            if (applicationExitSnapshotStarted || !sessions.IsExposureEpochCurrent(sessions.LifecycleEpoch))
                throw new UnauthorizedAccessException(
                    "Creating public connectivity was refused because application exit has started.");
            var generation = generations.AddOrUpdate(serverId, 1, static (_, current) => checked(current + 1));
            var lease = new PublicConnectivityLeaseIdentity
            {
                ServerId = serverId,
                LeaseId = Guid.NewGuid(),
                Generation = generation,
                LifecycleEpoch = sessions.LifecycleEpoch,
                SessionId = session.SessionId,
                CreatedAt = clock.GetUtcNow()
            };
            if (!active.TryAdd(serverId, lease))
                throw new InvalidOperationException(
                    "A public-connectivity lease already exists for this server. Refresh before trying again.");
            revoked.TryRemove(serverId, out _);
            return lease;
        }
    }

    public PublicConnectivityLeaseIdentity Get(Guid serverId)
    {
        lock (sync)
            return active.TryGetValue(serverId, out var lease) ? lease : new PublicConnectivityLeaseIdentity();
    }

    public bool HasActive(Guid serverId)
    {
        lock (sync)
            return active.ContainsKey(serverId);
    }

    public bool IsCurrent(PublicConnectivityLeaseIdentity expected)
    {
        lock (sync)
            return expected.IsPresent && !applicationExitSnapshotStarted &&
                   sessions.IsExposureEpochCurrent(expected.LifecycleEpoch) &&
                   active.TryGetValue(expected.ServerId, out var current) && current == expected;
    }

    /// <summary>
    /// Authorizes cleanup only for the last generation revoked in this Agent epoch. If any newer
    /// generation was minted, old queued cleanup is stale and must not touch its router state.
    /// </summary>
    public bool IsExactRevokedGeneration(PublicConnectivityLeaseIdentity expected, long exitEpoch)
    {
        lock (sync)
        {
            if (!applicationExitSnapshotStarted || !expected.IsPresent || expected.LifecycleEpoch != exitEpoch ||
                !sessions.IsExitCleanupEpoch(exitEpoch) || active.ContainsKey(expected.ServerId))
                return false;
            return generations.TryGetValue(expected.ServerId, out var latest) && latest == expected.Generation;
        }
    }

    public bool IsLatestRevokedGeneration(PublicConnectivityLeaseIdentity expected)
    {
        lock (sync)
            return expected.IsPresent && !active.ContainsKey(expected.ServerId) &&
                   generations.TryGetValue(expected.ServerId, out var latest) && latest == expected.Generation;
    }

    public bool HasNoLease(Guid serverId)
    {
        lock (sync)
            return !active.ContainsKey(serverId);
    }

    public void Demand(
        Guid serverId,
        UiSessionCredential session,
        PublicConnectivityLeaseIdentity presented,
        string operation)
    {
        sessions.Demand(session, operation);
        lock (sync)
        {
            if (applicationExitSnapshotStarted ||
                !sessions.IsExposureEpochCurrent(presented.LifecycleEpoch) ||
                !presented.IsPresent || presented.ServerId != serverId ||
                !active.TryGetValue(serverId, out var current) ||
                current.LeaseId != presented.LeaseId || current.Generation != presented.Generation ||
                current.SessionId != session.SessionId)
            {
                throw new UnauthorizedAccessException(
                    $"{operation} was refused because its public-connectivity lease is missing, stale, replayed, or mismatched.");
            }
        }
    }

    public PublicConnectivityLeaseIdentity Revoke(
        Guid serverId,
        UiSessionCredential session,
        PublicConnectivityLeaseIdentity presented,
        string operation)
    {
        sessions.Demand(session, operation);
        lock (sync)
        {
            if (applicationExitSnapshotStarted ||
                !sessions.IsExposureEpochCurrent(presented.LifecycleEpoch) ||
                !presented.IsPresent || presented.ServerId != serverId ||
                !active.TryRemove(new KeyValuePair<Guid, PublicConnectivityLeaseIdentity>(serverId, presented)))
                throw new UnauthorizedAccessException(
                    $"{operation} lost authority to a newer public-connectivity generation.");
            revoked[serverId] = presented;
            return presented;
        }
    }

    public IReadOnlyList<PublicConnectivityLeaseIdentity> RevokeAll()
    {
        lock (sync)
        {
            if (sessions.ApplicationExitStarted)
                applicationExitSnapshotStarted = true;
            foreach (var pair in active.ToArray())
            {
                if (active.TryRemove(pair))
                    revoked[pair.Key] = pair.Value;
            }
            return revoked.Values.ToArray();
        }
    }
}
