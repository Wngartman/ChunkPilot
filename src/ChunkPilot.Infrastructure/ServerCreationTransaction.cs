using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// The file ChunkPilot writes into a candidate so it can later prove the directory is its own.
/// </summary>
/// <remarks>
/// Written into staging before anything is promoted, so it travels with the candidate and is present
/// in the destination the instant activation completes. Recovery refuses to delete or take over any
/// directory that does not carry a matching marker; that single rule is what stops an interrupted
/// operation from touching a folder that turned out to belong to somebody else.
/// </remarks>
public sealed record CreationOwnershipMarker(
    int SchemaVersion,
    Guid OperationId,
    Guid ServerId,
    string CanonicalDestination,
    DateTimeOffset CreatedUtc)
{
    public const string FileName = ".chunkpilot-creation.json";
    public const int CurrentSchemaVersion = 1;

    public static string PathIn(string directory) => Path.Combine(directory, FileName);

    public static async Task WriteAsync(
        string directory,
        CreationOwnershipMarker marker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marker);
        await File.WriteAllTextAsync(
            PathIn(directory),
            JsonSerializer.Serialize(marker, ProtocolJson.Options),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a marker, returning null when it is missing or cannot be understood.</summary>
    public static CreationOwnershipMarker? TryRead(string directory)
    {
        try
        {
            var path = PathIn(directory);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<CreationOwnershipMarker>(
                File.ReadAllText(path), ProtocolJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the directory provably belongs to this operation and server.</summary>
    public static bool Owns(string directory, Guid operationId, Guid serverId)
    {
        var marker = TryRead(directory);
        return marker is not null && marker.OperationId == operationId && marker.ServerId == serverId;
    }
}

/// <summary>Where a candidate is being built and where it is destined for.</summary>
/// <param name="OperationId">The operation identifier, used for logs and download naming.</param>
/// <param name="StagingPath">The operation-owned directory to materialise into.</param>
/// <param name="DestinationPath">Where the candidate will end up, needed for launch arguments.</param>
/// <param name="LogPath">The staging log for this operation.</param>
public sealed record CreationMaterializationContext(
    Guid OperationId,
    string StagingPath,
    string DestinationPath,
    string LogPath);

/// <summary>What materialisation produced, ready to be verified and promoted.</summary>
/// <param name="Definition">The server as it will be registered.</param>
/// <param name="SourceUrl">Where the payload came from, for instance history.</param>
/// <param name="Sha256">The payload hash, for instance history. Empty when none was computed.</param>
/// <param name="HistoryDetail">Plain description recorded alongside the hash.</param>
public sealed record CreationCandidate(
    ServerDefinition Definition,
    string SourceUrl,
    string Sha256,
    string HistoryDetail);

/// <summary>Everything the transaction needs to run one creation.</summary>
public sealed record CreationTransactionRequest
{
    public required Guid OperationId { get; init; }
    public required Guid ServerId { get; init; }
    public required string ServerName { get; init; }
    public required string CreationKind { get; init; }
    public required string InstanceRoot { get; init; }
    public required string Destination { get; init; }
    public required string StagingPath { get; init; }
    public required string LogPath { get; init; }
    public DateTimeOffset EulaAcceptedAt { get; init; }
    public string EulaUrl { get; init; } = "";
}

/// <summary>The truthful conclusion of one creation transaction.</summary>
public sealed record CreationTransactionResult
{
    public required CreationPhase Phase { get; init; }
    public required CreationOutcome Outcome { get; init; }
    public ServerDefinition? Definition { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public CreationJournalEntry? Journal { get; init; }

    /// <summary>
    /// The original failure, when there was one, so a caller can rethrow it unchanged.
    /// </summary>
    /// <remarks>
    /// The transaction reports rather than throws, because a failure inside the critical section is a
    /// state that has to be recorded before anything is raised. Callers that owe their own callers an
    /// exception rethrow this one instead of inventing a new type and losing the cause.
    /// </remarks>
    public Exception? Failure { get; init; }

    public bool Succeeded => CreationPhasePolicy.IsSuccessful(Outcome);
}

/// <summary>
/// Deterministic interception points, used by tests to interrupt a transaction exactly where it
/// matters.
/// </summary>
/// <remarks>
/// Production passes null. This exists so an interruption test can throw at a chosen durable
/// checkpoint instead of racing a timer, which is the difference between a test that proves recovery
/// and a test that occasionally proves it.
/// </remarks>
public interface ICreationTransactionObserver
{
    Task OnPhaseAsync(CreationPhase phase, CreationJournalEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Runs one managed-server creation as a journalled transaction.
/// </summary>
/// <remarks>
/// <para>
/// The shape is deliberately boring: every durable side effect is bracketed by a journal write that
/// records it was about to happen and a second that records it did. Nothing between those two writes
/// may be interrupted by cancellation, because the whole point is that a crash there leaves evidence
/// a later run can act on rather than a directory nobody owns.
/// </para>
/// <para>
/// This is not a second installer. Collecting the files stays where it already was, in
/// <see cref="ManagedServerInstaller"/>, and is passed in as a callback; what lives here is the part
/// that was previously spread across the installer, the coordinator and nothing at all - destination
/// policy, promotion, registration, verification, rollback and cleanup.
/// </para>
/// </remarks>
public sealed class ServerCreationTransaction
{
    private readonly ChunkPilotStore store;
    private readonly CanonicalPathLockManager pathLocks;
    private readonly ICreationTransactionObserver? observer;
    private readonly CreationActivationMode? activationModeOverride;

    /// <param name="store">Persistence for the journal and the server record.</param>
    /// <param name="pathLocks">Shared canonical-path locks, so two operations cannot race a folder.</param>
    /// <param name="observer">Test-only interception. Null in production.</param>
    /// <param name="activationModeOverride">
    /// Test-only. Forces the cross-volume protocol so it can be exercised without a second volume.
    /// </param>
    public ServerCreationTransaction(
        ChunkPilotStore store,
        CanonicalPathLockManager? pathLocks = null,
        ICreationTransactionObserver? observer = null,
        CreationActivationMode? activationModeOverride = null)
    {
        this.store = store;
        this.pathLocks = pathLocks ?? new CanonicalPathLockManager();
        this.observer = observer;
        this.activationModeOverride = activationModeOverride;
    }

    public async Task<CreationTransactionResult> RunAsync(
        CreationTransactionRequest request,
        Func<CreationMaterializationContext, CancellationToken, Task<CreationCandidate>> materialize,
        Action<string, CreationCandidate> verifyCandidate,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(materialize);
        ArgumentNullException.ThrowIfNull(verifyCandidate);

        var destination = CreationPathSafety.Canonical(request.Destination);
        await using var destinationLock = await pathLocks.AcquireAsync(destination, cancellationToken)
            .ConfigureAwait(false);

        var entry = new CreationJournalEntry
        {
            OperationId = request.OperationId,
            ServerId = request.ServerId,
            CreationKind = request.CreationKind,
            ServerName = request.ServerName,
            CanonicalDestination = destination,
            CanonicalStaging = CreationPathSafety.Canonical(request.StagingPath),
            InstanceRoot = CreationPathSafety.Canonical(request.InstanceRoot),
            StartedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Phase = CreationPhase.Requested,
            LastCompletedCheckpoint = CreationPhase.Requested,
            OwnershipMarkerFile = CreationOwnershipMarker.FileName,
            // Recorded before anything can be promoted, so an interruption after activation still has
            // durable proof that the user accepted the EULA rather than only a folder that contains a
            // file somebody could have written.
            EulaAcceptedUtc = request.EulaAcceptedAt,
            EulaSourceUrl = request.EulaUrl
        };
        entry = await CommitAsync(entry, entry.Phase, progress, request, cancellationToken).ConfigureAwait(false);

        CreationCandidate? candidate = null;
        try
        {
            // ---- Safe preparatory phases. Cancellation here costs nothing. ----
            cancellationToken.ThrowIfCancellationRequested();
            entry = await CommitAsync(entry with { Phase = CreationPhase.ValidatingDestination },
                CreationPhase.ValidatingDestination, progress, request, cancellationToken).ConfigureAwait(false);
            var decision = await EvaluateDestinationAsync(request, destination, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
                throw new CreationDestinationBlockedException(decision);
            entry = entry with { DestinationExistedBefore = decision.DestinationExisted };

            cancellationToken.ThrowIfCancellationRequested();
            entry = await CommitAsync(entry with { Phase = CreationPhase.PreparingStaging },
                CreationPhase.PreparingStaging, progress, request, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(request.StagingPath);

            cancellationToken.ThrowIfCancellationRequested();
            entry = await CommitAsync(entry with { Phase = CreationPhase.MaterializingCandidate },
                CreationPhase.MaterializingCandidate, progress, request, cancellationToken).ConfigureAwait(false);
            candidate = await materialize(
                new CreationMaterializationContext(request.OperationId, request.StagingPath, destination, request.LogPath),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            entry = await CommitAsync(entry with { Phase = CreationPhase.VerifyingCandidate },
                CreationPhase.VerifyingCandidate, progress, request, cancellationToken).ConfigureAwait(false);
            verifyCandidate(request.StagingPath, candidate);
            await CreationOwnershipMarker.WriteAsync(request.StagingPath, new CreationOwnershipMarker(
                CreationOwnershipMarker.CurrentSchemaVersion, request.OperationId, request.ServerId,
                destination, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            entry = await CommitAsync(
                entry with
                {
                    Phase = CreationPhase.ReadyToActivate,
                    LastCompletedCheckpoint = CreationPhase.ReadyToActivate,
                    PlannedDefinition = candidate.Definition
                },
                CreationPhase.ReadyToActivate, progress, request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException cancelled)
        {
            return await AbandonBeforeActivationAsync(
                entry with { CancellationRequested = true },
                CreationOutcome.NothingActivated,
                "Cancelled before anything was put in place.",
                progress, request, cancelled).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await AbandonBeforeActivationAsync(
                entry with { LastError = SecretRedactor.Redact(exception.Message) },
                CreationOutcome.NothingActivated,
                SecretRedactor.Redact(exception.Message),
                progress, request, exception).ConfigureAwait(false);
        }

        // ---- Critical section. Cancellation is recorded, never obeyed mid-way. ----
        var criticalToken = CancellationToken.None;
        var cancellationArrivedLate = cancellationToken.IsCancellationRequested;
        if (cancellationArrivedLate)
            entry = entry with { CancellationRequested = true };

        return await CompleteCriticalSectionAsync(
            entry, request, candidate!, progress, cancellationArrivedLate, criticalToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Promotes, registers and verifies. Shared with recovery so a resumed operation follows exactly
    /// the same path as a first attempt.
    /// </summary>
    internal async Task<CreationTransactionResult> CompleteCriticalSectionAsync(
        CreationJournalEntry entry,
        CreationTransactionRequest request,
        CreationCandidate candidate,
        IProgress<InstallProgress>? progress,
        bool cancellationArrivedLate,
        CancellationToken cancellationToken)
    {
        var destination = entry.CanonicalDestination;
        var warnings = new List<string>();
        if (cancellationArrivedLate)
            warnings.Add("Cancelling arrived after the point of no return, so the server was created anyway.");

        try
        {
            if (!entry.ActivationCompleted)
            {
                entry = await CommitAsync(
                    entry with { Phase = CreationPhase.Activating, ActivationBegan = true },
                    CreationPhase.Activating, progress, request, cancellationToken).ConfigureAwait(false);

                // The destination is re-checked immediately before promotion, not only at the start:
                // a folder can appear between validation and here, and merging into it is exactly the
                // outcome this transaction exists to prevent.
                var recheck = await EvaluateDestinationAsync(request, destination, cancellationToken).ConfigureAwait(false);
                if (!recheck.IsAllowed)
                    throw new CreationDestinationBlockedException(recheck);

                var mode = activationModeOverride ??
                           (CreationPathSafety.IsSameVolume(entry.CanonicalStaging, destination)
                               ? CreationActivationMode.DirectoryMove
                               : CreationActivationMode.StagedCopy);
                entry = entry with { ActivationMode = mode };
                Activate(entry.CanonicalStaging, destination, mode, recheck.DestinationExisted);

                entry = await CommitAsync(
                    entry with
                    {
                        Phase = CreationPhase.Activated,
                        ActivationCompleted = true,
                        LastCompletedCheckpoint = CreationPhase.Activated
                    },
                    CreationPhase.Activated, progress, request, cancellationToken).ConfigureAwait(false);
            }

            if (!entry.RegistrationCompleted)
            {
                entry = await CommitAsync(
                    entry with { Phase = CreationPhase.Registering, RegistrationBegan = true },
                    CreationPhase.Registering, progress, request, cancellationToken).ConfigureAwait(false);

                // The server row goes first so the acceptance and history rows never reference an id
                // that has no server.
                await store.UpsertServerAsync(candidate.Definition, cancellationToken).ConfigureAwait(false);
                if (request.EulaAcceptedAt != default)
                    await store.RecordEulaAcceptanceAsync(entry.ServerId, request.EulaAcceptedAt,
                        request.EulaUrl, "Minecraft EULA", cancellationToken).ConfigureAwait(false);
                await store.RecordInstanceHistoryAsync(entry.ServerId, "Installed", candidate.SourceUrl,
                    candidate.Sha256, candidate.HistoryDetail, cancellationToken).ConfigureAwait(false);

                entry = await CommitAsync(
                    entry with
                    {
                        Phase = CreationPhase.Registered,
                        RegistrationCompleted = true,
                        LastCompletedCheckpoint = CreationPhase.Registered
                    },
                    CreationPhase.Registered, progress, request, cancellationToken).ConfigureAwait(false);
            }

            entry = await CommitAsync(entry with { Phase = CreationPhase.VerifyingRegistration },
                CreationPhase.VerifyingRegistration, progress, request, cancellationToken).ConfigureAwait(false);
            var verification = await VerifyAsync(entry, candidate.Definition, cancellationToken).ConfigureAwait(false);
            if (!verification.Passed)
            {
                entry = await CommitAsync(
                    entry with
                    {
                        Phase = CreationPhase.RecoveryRequired,
                        Outcome = CreationOutcome.RegisteredVerificationIncomplete,
                        LastError = string.Join(" ", verification.Failures)
                    },
                    CreationPhase.RecoveryRequired, progress, request, cancellationToken).ConfigureAwait(false);
                return new CreationTransactionResult
                {
                    Phase = CreationPhase.RecoveryRequired,
                    Outcome = CreationOutcome.RegisteredVerificationIncomplete,
                    Definition = candidate.Definition,
                    Warnings = [.. warnings, .. verification.Failures],
                    Journal = entry
                };
            }

            entry = entry with { VerificationPassed = true, LastCompletedCheckpoint = CreationPhase.Completed };
            return await FinalizeAsync(entry, candidate.Definition, warnings, progress, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CreationDestinationBlockedException blocked)
        {
            // Refused before any mutation: the destination is untouched and staging is still ours.
            return await AbandonBeforeActivationAsync(
                entry with { LastError = blocked.Message },
                CreationOutcome.StagingResumable, blocked.Message, progress, request, blocked).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await HandleCriticalFailureAsync(entry, exception, warnings, progress, request).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the final checks: the directory, the marker and the persisted record must all agree.
    /// </summary>
    /// <remarks>
    /// A write that returned without throwing is not evidence that the right thing was written. Every
    /// invariant here is read back from the store or the filesystem.
    /// </remarks>
    public async Task<CreationVerificationResult> VerifyAsync(
        CreationJournalEntry entry,
        ServerDefinition definition,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var destination = entry.CanonicalDestination;

        if (!Directory.Exists(destination))
            failures.Add("The server folder does not exist.");
        else if (!CreationPathSafety.IsSamePath(destination, entry.CanonicalDestination))
            failures.Add("The server folder does not resolve to the expected location.");

        if (Directory.Exists(destination) &&
            !CreationOwnershipMarker.Owns(destination, entry.OperationId, entry.ServerId))
            failures.Add("The server folder does not carry this operation's ownership marker.");

        var servers = await store.GetServersAsync(cancellationToken).ConfigureAwait(false);
        var matching = servers.Where(server => server.Id == entry.ServerId).ToArray();
        if (matching.Length == 0)
            failures.Add("The server was not found in ChunkPilot after it was added.");
        else if (matching.Length > 1)
            failures.Add("The server appears more than once in ChunkPilot.");
        else
        {
            var persisted = matching[0];
            if (string.IsNullOrWhiteSpace(persisted.RootPath) ||
                !CreationPathSafety.IsSamePath(persisted.RootPath, destination))
                failures.Add("The saved server points at a different folder than the one that was created.");
            if (!persisted.IsManaged)
                failures.Add("The saved server is not marked as managed by ChunkPilot.");
            if (!string.Equals(persisted.Name, definition.Name, StringComparison.Ordinal))
                failures.Add("The saved server name does not match the one that was requested.");
            if (persisted.Ecosystem != definition.Ecosystem)
                failures.Add("The saved server software does not match the plan.");
            if (persisted.GameKind != definition.GameKind)
                failures.Add("The saved server game does not match the plan.");
            if (!string.Equals(persisted.GameVersion, definition.GameVersion, StringComparison.Ordinal))
                failures.Add("The saved game version does not match the plan.");
            if (!string.Equals(persisted.MinecraftVersion, definition.MinecraftVersion, StringComparison.Ordinal))
                failures.Add("The saved Minecraft version does not match the plan.");
        }

        var otherOwners = servers
            .Where(server => server.Id != entry.ServerId)
            .Where(server => !string.IsNullOrWhiteSpace(server.RootPath))
            .Where(server => SafeIsSamePath(server.RootPath, destination))
            .ToArray();
        if (otherOwners.Length > 0)
            failures.Add($"Another server (\"{otherOwners[0].Name}\") already claims that folder.");

        return failures.Count == 0 ? CreationVerificationResult.Success : new CreationVerificationResult(false, failures);
    }

    /// <summary>
    /// Removes operation-owned temporary state and closes the journal.
    /// </summary>
    /// <remarks>
    /// A cleanup failure never demotes a verified creation to a failure: the server exists, is
    /// registered and passed its checks. The journal stays behind in <see cref="CreationPhase.CleanupPending"/>
    /// so a later pass can retry exactly the files this operation owns.
    /// </remarks>
    internal async Task<CreationTransactionResult> FinalizeAsync(
        CreationJournalEntry entry,
        ServerDefinition definition,
        List<string> warnings,
        IProgress<InstallProgress>? progress,
        CreationTransactionRequest request,
        CancellationToken cancellationToken)
    {
        if (definition.IsManaged)
        {
            try
            {
                await ManagedInstanceOwnershipMarker.WriteAsync(
                    entry.CanonicalDestination, definition.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The server is already registered and verified. Keep it usable, but without this
                // durable proof ChunkPilot will refuse any future data-deletion action.
                warnings.Add("Managed ownership proof could not be finalized; permanent deletion will remain unavailable. " +
                             SecretRedactor.Redact(exception.Message));
            }
        }
        var cleanupProblems = CleanupOwnedTemporaries(entry);
        if (cleanupProblems.Count == 0)
        {
            var completed = entry with
            {
                Phase = CreationPhase.Completed,
                Outcome = warnings.Count > 0 ? CreationOutcome.Completed : CreationOutcome.Completed,
                CleanupState = "Completed",
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            Report(progress, request, completed);
            await store.DeleteCreationJournalAsync(entry.OperationId, cancellationToken).ConfigureAwait(false);
            return new CreationTransactionResult
            {
                Phase = CreationPhase.Completed,
                Outcome = CreationOutcome.Completed,
                Definition = definition,
                Warnings = warnings,
                Journal = completed
            };
        }

        warnings.AddRange(cleanupProblems);
        var pending = await CommitAsync(
            entry with
            {
                Phase = CreationPhase.CleanupPending,
                Outcome = CreationOutcome.CompletedWithCleanupWarning,
                CleanupState = string.Join(" ", cleanupProblems)
            },
            CreationPhase.CleanupPending, progress, request, cancellationToken).ConfigureAwait(false);
        return new CreationTransactionResult
        {
            Phase = CreationPhase.CleanupPending,
            Outcome = CreationOutcome.CompletedWithCleanupWarning,
            Definition = definition,
            Warnings = warnings,
            Journal = pending
        };
    }

    /// <summary>
    /// Deletes only what this operation owns: its staging directory and its ownership marker.
    /// </summary>
    /// <returns>One message per thing that could not be removed. Empty when everything went.</returns>
    public static List<string> CleanupOwnedTemporaries(CreationJournalEntry entry)
    {
        var problems = new List<string>();

        var marker = CreationOwnershipMarker.PathIn(entry.CanonicalDestination);
        try
        {
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The temporary marker file could not be removed: {SecretRedactor.Redact(exception.Message)}");
        }

        try
        {
            if (Directory.Exists(entry.CanonicalStaging) && OwnsStaging(entry))
                Directory.Delete(entry.CanonicalStaging, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The temporary working folder could not be removed: {SecretRedactor.Redact(exception.Message)}");
        }

        return problems;
    }

    /// <summary>
    /// True when the staging path is unmistakably this operation's own working directory.
    /// </summary>
    /// <remarks>
    /// Two independent conditions, both required: the folder is named for this operation and it sits
    /// under the instance root the operation recorded. Cleanup never deletes anything that fails
    /// either test.
    /// </remarks>
    public static bool OwnsStaging(CreationJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.CanonicalStaging) || string.IsNullOrWhiteSpace(entry.InstanceRoot))
            return false;
        var expectedName = StagingFolderName(entry.OperationId);
        return Path.GetFileName(entry.CanonicalStaging).Equals(expectedName, StringComparison.OrdinalIgnoreCase) &&
               CreationPathSafety.IsUnder(entry.InstanceRoot, entry.CanonicalStaging);
    }

    /// <summary>The one place the staging folder name is defined.</summary>
    public static string StagingFolderName(Guid operationId) => $".chunkpilot-staging-{operationId:N}";

    private static bool SafeIsSamePath(string left, string right)
    {
        try
        {
            return CreationPathSafety.IsSamePath(left, right);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Promotes a verified candidate into its destination.
    /// </summary>
    /// <remarks>
    /// Same volume uses a single rename, which the filesystem makes atomic. Across volumes no such
    /// primitive exists, so the candidate is copied to a temporary sibling, checked, and only then
    /// renamed into place - and the journal records that the guarantee came from the marker and the
    /// checkpoints rather than from the filesystem. Calling the second one atomic would be a lie.
    /// </remarks>
    private static void Activate(
        string stagingPath,
        string destination,
        CreationActivationMode mode,
        bool destinationExistedEmpty)
    {
        if (destinationExistedEmpty && Directory.Exists(destination))
        {
            // An accepted empty directory is removed first so the rename lands cleanly. It is proven
            // empty by the policy immediately before this runs, so nothing can be lost.
            Directory.Delete(destination, recursive: false);
        }

        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (mode == CreationActivationMode.DirectoryMove)
        {
            Directory.Move(stagingPath, destination);
            return;
        }

        var landing = destination + ".chunkpilot-incoming";
        if (Directory.Exists(landing))
            Directory.Delete(landing, recursive: true);
        CopyTree(stagingPath, landing);
        if (!File.Exists(CreationOwnershipMarker.PathIn(landing)))
            throw new IOException("The copied candidate is missing its ownership marker, so it was not promoted.");
        Directory.Move(landing, destination);
        Directory.Delete(stagingPath, recursive: true);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private async Task<CreationDestinationDecision> EvaluateDestinationAsync(
        CreationTransactionRequest request,
        string destination,
        CancellationToken cancellationToken)
    {
        var servers = await store.GetServersAsync(cancellationToken).ConfigureAwait(false);
        var journals = await store.GetCreationJournalsAsync(cancellationToken).ConfigureAwait(false);
        var active = journals
            .Where(record => record.Entry is not null)
            .Select(record => record.Entry!)
            .ToArray();

        // A row this build cannot read still owns its destination. Refusing is the safe answer.
        var unreadable = journals.FirstOrDefault(record => !record.IsReadable);
        if (unreadable is not null)
            active =
            [
                .. active,
                new CreationJournalEntry
                {
                    OperationId = unreadable.OperationId,
                    CanonicalDestination = destination,
                    Phase = CreationPhase.RecoveryRequired
                }
            ];

        return CreationDestinationPolicy.Evaluate(new CreationDestinationQuery(
            request.OperationId, destination, request.StagingPath, servers, active));
    }

    /// <summary>Ends an operation that never mutated the destination.</summary>
    /// <remarks>
    /// The destination is provably untouched here, so the journal has served its purpose and is
    /// removed. Staging goes with it unless the candidate is still usable, in which case it is left
    /// for inspection and the outcome says so.
    /// </remarks>
    private async Task<CreationTransactionResult> AbandonBeforeActivationAsync(
        CreationJournalEntry entry,
        CreationOutcome outcome,
        string detail,
        IProgress<InstallProgress>? progress,
        CreationTransactionRequest request,
        Exception failure)
    {
        var phase = entry.CancellationRequested ? CreationPhase.Cancelling : CreationPhase.Failed;
        var closed = entry with
        {
            Phase = phase,
            Outcome = outcome,
            LastError = detail,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        Report(progress, request, closed);

        if (outcome != CreationOutcome.StagingResumable)
        {
            var problems = CleanupOwnedTemporaries(closed);
            if (problems.Count > 0)
                closed = closed with { CleanupState = string.Join(" ", problems) };
        }

        await store.DeleteCreationJournalAsync(entry.OperationId, CancellationToken.None).ConfigureAwait(false);
        return new CreationTransactionResult
        {
            Phase = phase,
            Outcome = outcome,
            Warnings = [detail],
            Journal = closed,
            Failure = failure
        };
    }

    /// <summary>
    /// Decides what to do after a failure inside the critical section, using only what is provable.
    /// </summary>
    private async Task<CreationTransactionResult> HandleCriticalFailureAsync(
        CreationJournalEntry entry,
        Exception exception,
        List<string> warnings,
        IProgress<InstallProgress>? progress,
        CreationTransactionRequest request)
    {
        var message = SecretRedactor.Redact(exception.Message);
        entry = entry with { LastError = message };

        // Activated but not registered: the directory is ours and nothing points at it yet, so the
        // change can be reversed.
        if (entry.ActivationCompleted && !entry.RegistrationCompleted)
        {
            entry = await CommitAsync(entry with { Phase = CreationPhase.RollingBack },
                CreationPhase.RollingBack, progress, request, CancellationToken.None).ConfigureAwait(false);
            var rolledBack = TryRollBackActivation(entry);
            if (rolledBack)
            {
                var done = entry with
                {
                    Phase = CreationPhase.RolledBack,
                    Outcome = CreationOutcome.RolledBack,
                    RollbackState = "Reversed",
                    ActivationCompleted = false,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                Report(progress, request, done);
                CleanupOwnedTemporaries(done);
                await store.DeleteCreationJournalAsync(entry.OperationId, CancellationToken.None).ConfigureAwait(false);
                return new CreationTransactionResult
                {
                    Phase = CreationPhase.RolledBack,
                    Outcome = CreationOutcome.RolledBack,
                    Warnings = [.. warnings, message],
                    Journal = done,
                    Failure = exception
                };
            }

            var stuck = await CommitAsync(
                entry with
                {
                    Phase = CreationPhase.RecoveryRequired,
                    Outcome = CreationOutcome.ActivatedRegistrationIncomplete,
                    RollbackState = "Could not be reversed automatically"
                },
                CreationPhase.RecoveryRequired, progress, request, CancellationToken.None).ConfigureAwait(false);
            return new CreationTransactionResult
            {
                Phase = CreationPhase.RecoveryRequired,
                Outcome = CreationOutcome.ActivatedRegistrationIncomplete,
                Warnings = [.. warnings, message],
                Journal = stuck,
                Failure = exception
            };
        }

        if (!entry.ActivationBegan || !entry.ActivationCompleted && !Directory.Exists(entry.CanonicalDestination))
        {
            var failed = entry with
            {
                Phase = CreationPhase.Failed,
                Outcome = CreationOutcome.NothingActivated,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            Report(progress, request, failed);
            CleanupOwnedTemporaries(failed);
            await store.DeleteCreationJournalAsync(entry.OperationId, CancellationToken.None).ConfigureAwait(false);
            return new CreationTransactionResult
            {
                Phase = CreationPhase.Failed,
                Outcome = CreationOutcome.NothingActivated,
                Warnings = [.. warnings, message],
                Journal = failed,
                Failure = exception
            };
        }

        var attention = await CommitAsync(
            entry with { Phase = CreationPhase.RecoveryRequired, Outcome = CreationOutcome.RecoveryRequired },
            CreationPhase.RecoveryRequired, progress, request, CancellationToken.None).ConfigureAwait(false);
        return new CreationTransactionResult
        {
            Phase = CreationPhase.RecoveryRequired,
            Outcome = CreationOutcome.RecoveryRequired,
            Warnings = [.. warnings, message],
            Journal = attention,
            Failure = exception
        };
    }

    /// <summary>
    /// Reverses a completed activation, but only when the destination is provably this operation's.
    /// </summary>
    public static bool TryRollBackActivation(CreationJournalEntry entry)
    {
        try
        {
            if (!Directory.Exists(entry.CanonicalDestination))
                return true;
            if (!CreationOwnershipMarker.Owns(entry.CanonicalDestination, entry.OperationId, entry.ServerId))
                return false;
            if (Directory.Exists(entry.CanonicalStaging))
                return false;
            Directory.Move(entry.CanonicalDestination, entry.CanonicalStaging);
            if (entry.DestinationExistedBefore)
                Directory.CreateDirectory(entry.CanonicalDestination);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<CreationJournalEntry> CommitAsync(
        CreationJournalEntry entry,
        CreationPhase phase,
        IProgress<InstallProgress>? progress,
        CreationTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var stamped = entry with { Phase = phase, UpdatedUtc = DateTimeOffset.UtcNow };
        await store.UpsertCreationJournalAsync(stamped, cancellationToken).ConfigureAwait(false);
        Report(progress, request, stamped);
        if (observer is not null)
            await observer.OnPhaseAsync(phase, stamped, cancellationToken).ConfigureAwait(false);
        return stamped;
    }

    private static void Report(
        IProgress<InstallProgress>? progress,
        CreationTransactionRequest request,
        CreationJournalEntry entry) =>
        progress?.Report(new InstallProgress
        {
            OperationId = entry.OperationId,
            State = CreationProgressMapping.ToInstallState(entry.Phase),
            Phase = entry.Phase,
            Stage = CreationStagePolicy.ForPhase(entry.Phase),
            CurrentStep = CreationStagePolicy.Describe(CreationStagePolicy.ForPhase(entry.Phase)),
            OverallPercent = CreationProgressMapping.PercentFor(entry.Phase),
            Detail = entry.LastError.Length > 0 ? entry.LastError : entry.CanonicalDestination,
            StagingLogPath = request.LogPath
        });
}

/// <summary>
/// Translates the creation state machine into the progress vocabulary the App already speaks.
/// </summary>
/// <remarks>
/// The App never sees a phase name. It sees an <see cref="InstallState"/> it already handles plus
/// the plain-language step text, so richer internal states do not leak an enum into the interface.
/// </remarks>
public static class CreationProgressMapping
{
    public static InstallState ToInstallState(CreationPhase phase) => phase switch
    {
        CreationPhase.Requested or CreationPhase.ValidatingDestination => InstallState.Planned,
        CreationPhase.PreparingStaging => InstallState.Staging,
        CreationPhase.MaterializingCandidate => InstallState.Installing,
        CreationPhase.VerifyingCandidate => InstallState.Validating,
        CreationPhase.ReadyToActivate or CreationPhase.Activating or CreationPhase.Activated =>
            InstallState.Finalizing,
        CreationPhase.Registering or CreationPhase.Registered or CreationPhase.VerifyingRegistration =>
            InstallState.Registering,
        CreationPhase.Completed or CreationPhase.CleanupPending => InstallState.Completed,
        CreationPhase.Cancelling => InstallState.Cancelled,
        CreationPhase.RollingBack => InstallState.RollingBack,
        CreationPhase.RolledBack => InstallState.Cancelled,
        CreationPhase.RecoveryRequired => InstallState.RecoveryRequired,
        _ => InstallState.Failed
    };

    /// <summary>
    /// A coarse completion figure. Deliberately not derived from bytes: after the download the
    /// remaining work is a handful of discrete, fast steps.
    /// </summary>
    public static double PercentFor(CreationPhase phase) => phase switch
    {
        CreationPhase.Requested => 1,
        CreationPhase.ValidatingDestination => 3,
        CreationPhase.PreparingStaging => 5,
        CreationPhase.MaterializingCandidate => 40,
        CreationPhase.VerifyingCandidate => 85,
        CreationPhase.ReadyToActivate => 90,
        CreationPhase.Activating => 93,
        CreationPhase.Activated => 95,
        CreationPhase.Registering => 96,
        CreationPhase.Registered => 98,
        CreationPhase.VerifyingRegistration => 99,
        CreationPhase.Completed or CreationPhase.CleanupPending => 100,
        _ => 0
    };
}
