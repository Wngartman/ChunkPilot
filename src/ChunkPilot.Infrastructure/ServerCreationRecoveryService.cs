using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Reconciles creation operations that were interrupted by a crash, a forced exit or a restart.
/// </summary>
/// <remarks>
/// <para>
/// Runs once at Agent startup, before anything else looks at the server list, so a half-created
/// server is either finished or removed before it could be mistaken for a working one.
/// </para>
/// <para>
/// Every decision is derived from durable evidence — the journal's flags, the ownership marker in the
/// directory, and the persisted server record — and never from a directory simply existing. Where the
/// evidence does not permit a safe decision the entry is left alone and reported as needing
/// attention: an operation that cannot be reconciled is preserved, not tidied away.
/// </para>
/// <para>
/// Idempotent by construction. Each pass re-reads the evidence and reaches the same conclusion, and
/// registration is an upsert keyed by the server id fixed before activation, so a repeated pass
/// cannot produce a second server.
/// </para>
/// </remarks>
public sealed class ServerCreationRecoveryService
{
    /// <summary>
    /// How many times a single entry may be retried automatically.
    /// </summary>
    /// <remarks>
    /// Bounded so a permanently unhappy entry cannot make every Agent start do the same failing work
    /// forever. Once the bound is reached the entry stays in the journal, reported and untouched.
    /// </remarks>
    public const int MaximumRecoveryAttempts = 3;

    private readonly ChunkPilotStore store;
    private readonly ServerCreationTransaction transaction;

    public ServerCreationRecoveryService(ChunkPilotStore store, ServerCreationTransaction? transaction = null)
    {
        this.store = store;
        this.transaction = transaction ?? new ServerCreationTransaction(store);
    }

    /// <summary>Reconciles every outstanding creation journal entry.</summary>
    public async Task<IReadOnlyList<CreationRecoveryReport>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var reports = new List<CreationRecoveryReport>();
        foreach (var record in await store.GetCreationJournalsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reports.Add(await RecoverOneAsync(record, cancellationToken).ConfigureAwait(false));
        }
        return reports;
    }

    private async Task<CreationRecoveryReport> RecoverOneAsync(
        CreationJournalRecord record,
        CancellationToken cancellationToken)
    {
        // A row this build cannot interpret is never acted on. It still owns its destination, which
        // the destination policy honours, so leaving it is the safe answer rather than the lazy one.
        if (!record.IsReadable)
            return new CreationRecoveryReport(record.OperationId, CreationRecoveryDisposition.AttentionRequired,
                CreationOutcome.Inconsistent,
                $"A creation record could not be read and was left untouched. {record.UnreadableReason}");

        var entry = record.Entry!;
        if (entry.RecoveryAttempts >= MaximumRecoveryAttempts)
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
                CreationOutcome.RecoveryRequired,
                $"\"{entry.ServerName}\" was retried {entry.RecoveryAttempts} times without success and now needs "
                + "your attention. Nothing further was changed.");

        entry = entry with { RecoveryAttempts = entry.RecoveryAttempts + 1, UpdatedUtc = DateTimeOffset.UtcNow };
        await store.UpsertCreationJournalAsync(entry, cancellationToken).ConfigureAwait(false);

        try
        {
            if (entry.VerificationPassed)
                return await RetryCleanupAsync(entry, cancellationToken).ConfigureAwait(false);
            if (entry.RegistrationCompleted)
                return await FinishVerificationAsync(entry, cancellationToken).ConfigureAwait(false);
            if (entry.ActivationCompleted)
                return await FinishRegistrationAsync(entry, cancellationToken).ConfigureAwait(false);
            if (entry.ActivationBegan)
                return await ResolveUncertainActivationAsync(entry, cancellationToken).ConfigureAwait(false);
            return await DiscardStagingAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await MarkAttentionAsync(entry, SecretRedactor.Redact(exception.Message), cancellationToken)
                .ConfigureAwait(false);
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
                CreationOutcome.RecoveryRequired,
                $"Recovering \"{entry.ServerName}\" did not succeed and was stopped. "
                + SecretRedactor.Redact(exception.Message));
        }
    }

    /// <summary>
    /// Nothing had been promoted, so the destination must still be as the user left it.
    /// </summary>
    private async Task<CreationRecoveryReport> DiscardStagingAsync(
        CreationJournalEntry entry,
        CancellationToken cancellationToken)
    {
        // If the destination now holds this operation's marker, the flags are older than reality and
        // the evidence disagrees with itself. Stop rather than delete something that was promoted.
        if (Directory.Exists(entry.CanonicalDestination) &&
            CreationOwnershipMarker.Owns(entry.CanonicalDestination, entry.OperationId, entry.ServerId))
            return await ResolveUncertainActivationAsync(entry, cancellationToken).ConfigureAwait(false);

        var problems = ServerCreationTransaction.CleanupOwnedTemporaries(entry);
        if (problems.Count > 0)
        {
            await store.UpsertCreationJournalAsync(entry with
            {
                Phase = CreationPhase.CleanupPending,
                Outcome = CreationOutcome.NothingActivated,
                CleanupState = string.Join(" ", problems),
                RecoveryDisposition = CreationRecoveryDisposition.RetriedCleanup,
                UpdatedUtc = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.RetriedCleanup,
                CreationOutcome.NothingActivated,
                $"\"{entry.ServerName}\" was never put in place. Its temporary files could not be removed yet.");
        }

        await store.DeleteCreationJournalAsync(entry.OperationId, cancellationToken).ConfigureAwait(false);
        return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.DiscardedStaging,
            CreationOutcome.NothingActivated,
            $"\"{entry.ServerName}\" was interrupted before anything was put in place. "
            + "The folder was untouched and the temporary files were removed.");
    }

    /// <summary>
    /// Activation had started and its outcome was never recorded. Decide from the marker, not from
    /// whether a directory happens to exist.
    /// </summary>
    private async Task<CreationRecoveryReport> ResolveUncertainActivationAsync(
        CreationJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var destinationExists = Directory.Exists(entry.CanonicalDestination);
        var stagingExists = Directory.Exists(entry.CanonicalStaging);
        var destinationIsOurs = destinationExists &&
            CreationOwnershipMarker.Owns(entry.CanonicalDestination, entry.OperationId, entry.ServerId);
        var stagingIsOurs = stagingExists &&
            CreationOwnershipMarker.Owns(entry.CanonicalStaging, entry.OperationId, entry.ServerId);

        if (destinationExists && !destinationIsOurs)
        {
            await MarkAttentionAsync(entry,
                "The destination folder exists but does not carry this operation's marker, so ChunkPilot cannot "
                + "tell whether it belongs to this attempt.", cancellationToken).ConfigureAwait(false);
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
                CreationOutcome.Inconsistent,
                $"\"{entry.ServerName}\" left a folder ChunkPilot cannot prove it owns. Nothing was changed.");
        }

        if (destinationIsOurs)
        {
            var promoted = entry with
            {
                ActivationCompleted = true,
                LastCompletedCheckpoint = CreationPhase.Activated,
                Phase = CreationPhase.Activated,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            await store.UpsertCreationJournalAsync(promoted, cancellationToken).ConfigureAwait(false);
            return await FinishRegistrationAsync(promoted, cancellationToken).ConfigureAwait(false);
        }

        if (stagingIsOurs)
        {
            // The candidate never left staging. Treat it exactly as an operation that had not begun
            // activation, which is the state the filesystem is actually in.
            return await DiscardStagingAsync(entry with { ActivationBegan = false }, cancellationToken)
                .ConfigureAwait(false);
        }

        await MarkAttentionAsync(entry,
            "Neither the destination nor the temporary folder carries this operation's marker.",
            cancellationToken).ConfigureAwait(false);
        return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
            CreationOutcome.Inconsistent,
            $"\"{entry.ServerName}\" cannot be reconciled because neither folder can be identified. "
            + "Nothing was changed.");
    }

    /// <summary>
    /// The directory is ours and in place; finish writing and checking the server record.
    /// </summary>
    private async Task<CreationRecoveryReport> FinishRegistrationAsync(
        CreationJournalEntry entry,
        CancellationToken cancellationToken)
    {
        if (!CreationOwnershipMarker.Owns(entry.CanonicalDestination, entry.OperationId, entry.ServerId))
        {
            await MarkAttentionAsync(entry,
                "The destination no longer carries this operation's marker.", cancellationToken).ConfigureAwait(false);
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
                CreationOutcome.Inconsistent,
                $"\"{entry.ServerName}\" could not be confirmed as this attempt's folder. Nothing was changed.");
        }

        var missingConsent = MissingEulaEvidence(entry);
        if (missingConsent is not null)
        {
            await MarkAttentionAsync(entry, missingConsent, cancellationToken).ConfigureAwait(false);
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
                CreationOutcome.ActivatedRegistrationIncomplete,
                $"\"{entry.ServerName}\" was not added to ChunkPilot because {missingConsent} "
                + "The folder was left exactly as it is.");
        }

        var servers = await store.GetServersAsync(cancellationToken).ConfigureAwait(false);
        var existing = servers.SingleOrDefault(server => server.Id == entry.ServerId);
        if (existing is null)
        {
            // The plan recorded at the last safe checkpoint is what makes this resumable. Without it
            // the folder would be provably ours with nothing to write, and a person would have to
            // decide; with it, the operation simply finishes.
            if (entry.PlannedDefinition is null)
            {
                await MarkAttentionAsync(entry,
                    "The server files are in place but no plan was recorded, so the entry cannot be written.",
                    cancellationToken).ConfigureAwait(false);
                return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
                    CreationOutcome.ActivatedRegistrationIncomplete,
                    $"\"{entry.ServerName}\" has its files in place but was never added to ChunkPilot. "
                    + $"The folder was left exactly as it is: {entry.CanonicalDestination}");
            }

            // Keyed by the server id fixed before activation, so repeating this cannot add a second
            // server however many times recovery runs.
            await store.UpsertServerAsync(entry.PlannedDefinition, cancellationToken).ConfigureAwait(false);
        }

        var registered = entry with
        {
            RegistrationBegan = true,
            RegistrationCompleted = true,
            LastCompletedCheckpoint = CreationPhase.Registered,
            Phase = CreationPhase.Registered,
            RecoveryDisposition = CreationRecoveryDisposition.CompletedRegistration,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await store.UpsertCreationJournalAsync(registered, cancellationToken).ConfigureAwait(false);
        return await FinishVerificationAsync(registered, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the final checks against whatever is genuinely there.</summary>
    private async Task<CreationRecoveryReport> FinishVerificationAsync(
        CreationJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var servers = await store.GetServersAsync(cancellationToken).ConfigureAwait(false);
        var definition = servers.SingleOrDefault(server => server.Id == entry.ServerId);
        if (definition is null)
        {
            await MarkAttentionAsync(entry, "The server record disappeared before it could be checked.",
                cancellationToken).ConfigureAwait(false);
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
                CreationOutcome.RegisteredVerificationIncomplete,
                $"\"{entry.ServerName}\" could not be checked because its ChunkPilot entry is missing.");
        }

        var verification = await transaction.VerifyAsync(entry, definition, cancellationToken).ConfigureAwait(false);
        if (!verification.Passed)
        {
            await MarkAttentionAsync(entry, string.Join(" ", verification.Failures), cancellationToken)
                .ConfigureAwait(false);
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.AttentionRequired,
                CreationOutcome.RegisteredVerificationIncomplete,
                $"\"{entry.ServerName}\" did not pass its final checks. " + string.Join(" ", verification.Failures));
        }

        var verified = entry with
        {
            VerificationPassed = true,
            Phase = CreationPhase.CleanupPending,
            LastCompletedCheckpoint = CreationPhase.Completed,
            RecoveryDisposition = CreationRecoveryDisposition.CompletedVerification,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await store.UpsertCreationJournalAsync(verified, cancellationToken).ConfigureAwait(false);
        var report = await RetryCleanupAsync(verified, cancellationToken).ConfigureAwait(false);
        return report with
        {
            Disposition = CreationRecoveryDisposition.CompletedVerification,
            Detail = $"\"{entry.ServerName}\" was finished after an interruption. " + report.Detail
        };
    }

    /// <summary>
    /// Only temporary files remain. Retry them, and never let their failure change the server.
    /// </summary>
    private async Task<CreationRecoveryReport> RetryCleanupAsync(
        CreationJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var problems = ServerCreationTransaction.CleanupOwnedTemporaries(entry);
        if (problems.Count == 0)
        {
            await store.DeleteCreationJournalAsync(entry.OperationId, cancellationToken).ConfigureAwait(false);
            return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.RetriedCleanup,
                CreationOutcome.Completed, "The server is in place and the temporary files were removed.");
        }

        await store.UpsertCreationJournalAsync(entry with
        {
            Phase = CreationPhase.CleanupPending,
            Outcome = CreationOutcome.CleanupFailedServerKnown,
            CleanupState = string.Join(" ", problems),
            RecoveryDisposition = CreationRecoveryDisposition.RetriedCleanup,
            UpdatedUtc = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
        return new CreationRecoveryReport(entry.OperationId, CreationRecoveryDisposition.RetriedCleanup,
            CreationOutcome.CleanupFailedServerKnown,
            "The server is in place and correct. Some temporary files still could not be removed.");
    }

    /// <summary>
    /// Checks that the user's EULA acceptance survived the interruption, in both places it is kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two independent pieces of evidence, both required: the journal recorded that acceptance was
    /// given and where it was shown, and the folder this operation provably owns contains the
    /// <c>eula.txt</c> that acceptance authorised. Either one alone is weaker than it looks — a
    /// journal entry could outlive a candidate that was never finished, and a file on disk is not
    /// proof that a person agreed to anything.
    /// </para>
    /// <para>
    /// A creation that never involved the EULA at all records no acceptance timestamp and is not
    /// subject to this check; the rule is about not completing a Minecraft server on the strength of
    /// a folder alone, not about inventing a requirement where none existed.
    /// </para>
    /// </remarks>
    /// <returns>The reason recovery must stop, or null when the evidence is complete.</returns>
    private static string? MissingEulaEvidence(CreationJournalEntry entry)
    {
        if (entry.EulaAcceptedUtc == default)
            return null;
        if (string.IsNullOrWhiteSpace(entry.EulaSourceUrl))
            return "the record of which EULA was accepted did not survive.";

        var eulaFile = Path.Combine(entry.CanonicalDestination, "eula.txt");
        try
        {
            if (!File.Exists(eulaFile))
                return "the accepted EULA file is missing from the server folder.";
            if (!File.ReadAllText(eulaFile).Contains("eula=true", StringComparison.OrdinalIgnoreCase))
                return "the EULA file in the server folder does not record acceptance.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "the accepted EULA file could not be read. " + SecretRedactor.Redact(exception.Message);
        }

        return null;
    }

    private async Task MarkAttentionAsync(
        CreationJournalEntry entry,
        string reason,
        CancellationToken cancellationToken) =>
        await store.UpsertCreationJournalAsync(entry with
        {
            Phase = CreationPhase.RecoveryRequired,
            Outcome = entry.RegistrationCompleted
                ? CreationOutcome.RegisteredVerificationIncomplete
                : entry.ActivationCompleted
                    ? CreationOutcome.ActivatedRegistrationIncomplete
                    : CreationOutcome.RecoveryRequired,
            RecoveryDisposition = CreationRecoveryDisposition.AttentionRequired,
            LastError = reason,
            UpdatedUtc = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
}
