using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// The creation transaction, its journal, and reconciliation after an interruption.
/// </summary>
/// <remarks>
/// <para>
/// Everything runs against a real SQLite database and a real temporary filesystem, because the
/// guarantees being tested are precisely the ones a mock would assume away: that a journal row
/// survives a restart, that a directory rename either happened or did not, and that a second pass
/// over the same evidence reaches the same conclusion.
/// </para>
/// <para>
/// A crash is simulated by constructing the durable state a crash leaves behind - the journal row,
/// the files, and whatever did or did not reach the servers table - and then handing it to a fresh
/// recovery service. That is exactly what the Agent does on its next start, and it is deterministic,
/// unlike stopping a running transaction at a chosen instruction.
/// </para>
/// </remarks>
public sealed class ServerCreationTransactionIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-creation-" + Guid.NewGuid().ToString("N"));

    public ServerCreationTransactionIntegrationTests() => Directory.CreateDirectory(root);

    // ================================================================ journal

    [Fact]
    public async Task The_journal_survives_a_restart_and_updates_in_place()
    {
        var paths = Paths("journal");
        var operationId = Guid.NewGuid();
        var entry = NewEntry(operationId, Path.Combine(root, "dest"), Path.Combine(root, "stage"));

        await using (var store = await OpenStoreAsync(paths))
        {
            await store.UpsertCreationJournalAsync(entry);
            await store.UpsertCreationJournalAsync(entry with
            {
                Phase = CreationPhase.Activated,
                ActivationBegan = true,
                ActivationCompleted = true
            });
        }

        SqliteConnection.ClearAllPools();
        await using var reopened = await OpenStoreAsync(paths);
        var rows = await reopened.GetCreationJournalsAsync();

        var only = Assert.Single(rows);
        Assert.True(only.IsReadable);
        Assert.Equal(CreationPhase.Activated, only.Entry!.Phase);
        Assert.True(only.Entry.ActivationCompleted);
        Assert.Equal(entry.CanonicalDestination, only.Entry.CanonicalDestination);
    }

    [Fact]
    public async Task A_missing_journal_entry_reads_as_nothing_rather_than_an_error()
    {
        await using var store = await OpenStoreAsync(Paths("journal-missing"));

        Assert.Null(await store.GetCreationJournalAsync(Guid.NewGuid()));
        Assert.Empty(await store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task A_journal_entry_from_a_newer_build_is_preserved_and_never_acted_on()
    {
        var paths = Paths("journal-newer");
        await using var store = await OpenStoreAsync(paths);
        var operationId = Guid.NewGuid();
        await WriteRawJournalAsync(paths, operationId,
            CreationJournalEntry.CurrentSchemaVersion + 1, "{\"schemaVersion\":99}");

        var record = Assert.Single(await store.GetCreationJournalsAsync());
        Assert.False(record.IsReadable);
        Assert.Contains("newer version", record.UnreadableReason, StringComparison.OrdinalIgnoreCase);

        var reports = await new ServerCreationRecoveryService(store).RecoverAsync();

        Assert.Equal(CreationOutcome.Inconsistent, Assert.Single(reports).Outcome);
        // The row stays: it still owns its destination, and deleting it would let a later run reuse
        // a folder another build is mid-way through owning.
        Assert.Single(await store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task A_corrupt_journal_entry_is_preserved_and_never_acted_on()
    {
        var paths = Paths("journal-corrupt");
        await using var store = await OpenStoreAsync(paths);
        await WriteRawJournalAsync(paths, Guid.NewGuid(),
            CreationJournalEntry.CurrentSchemaVersion, "{ this is not json");

        var record = Assert.Single(await store.GetCreationJournalsAsync());
        Assert.False(record.IsReadable);

        var reports = await new ServerCreationRecoveryService(store).RecoverAsync();

        Assert.Equal(CreationRecoveryDisposition.AttentionRequired, Assert.Single(reports).Disposition);
        Assert.Single(await store.GetCreationJournalsAsync());
    }

    // ================================================================ happy path

    [Fact]
    public async Task A_successful_creation_activates_registers_verifies_and_leaves_no_journal()
    {
        var fixture = await FixtureAsync("success");

        var result = await fixture.RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(CreationPhase.Completed, result.Phase);
        Assert.Equal(CreationOutcome.Completed, result.Outcome);

        Assert.True(File.Exists(Path.Combine(fixture.Destination, "server.jar")));
        Assert.False(Directory.Exists(fixture.Staging));
        // The creation marker is temporary. A distinct durable marker proves that later destructive
        // operations may act on this exact managed root and no imported or ownership-uncertain path.
        Assert.False(File.Exists(CreationOwnershipMarker.PathIn(fixture.Destination)));
        Assert.True(ManagedInstanceOwnershipMarker.Proves(fixture.Destination, fixture.ServerId));

        var registered = Assert.Single(await fixture.Store.GetServersAsync());
        Assert.Equal(fixture.ServerId, registered.Id);
        Assert.Equal(CreationPathSafety.Canonical(fixture.Destination), CreationPathSafety.Canonical(registered.RootPath));
        Assert.True(registered.IsManaged);
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Progress_reports_the_creation_phases_in_order_and_ends_at_completed()
    {
        var fixture = await FixtureAsync("progress");
        var phases = new List<CreationPhase>();

        await fixture.RunAsync(progress: new CollectingProgress(update => phases.Add(update.Phase)));

        Assert.Contains(CreationPhase.ValidatingDestination, phases);
        Assert.Contains(CreationPhase.ReadyToActivate, phases);
        Assert.Contains(CreationPhase.Activated, phases);
        Assert.Contains(CreationPhase.Registered, phases);
        Assert.Equal(CreationPhase.Completed, phases[^1]);
        Assert.True(phases.IndexOf(CreationPhase.Activated) < phases.IndexOf(CreationPhase.Registered));
    }

    [Fact]
    public async Task The_cross_volume_protocol_promotes_a_verified_copy_and_says_it_is_not_atomic()
    {
        var fixture = await FixtureAsync("staged-copy", activationMode: CreationActivationMode.StagedCopy);

        var result = await fixture.RunAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(CreationActivationMode.StagedCopy, result.Journal!.ActivationMode);
        Assert.True(File.Exists(Path.Combine(fixture.Destination, "server.jar")));
        Assert.False(Directory.Exists(fixture.Staging));
        Assert.False(Directory.Exists(fixture.Destination + ".chunkpilot-incoming"));
        Assert.Single(await fixture.Store.GetServersAsync());
    }

    [Fact]
    public async Task An_accepted_empty_destination_is_used_without_merging_anything()
    {
        var fixture = await FixtureAsync("empty-destination");
        Directory.CreateDirectory(fixture.Destination);

        var result = await fixture.RunAsync();

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(fixture.Destination, "server.jar")));
    }

    // ================================================================ destination refusal

    [Fact]
    public async Task A_destination_that_fills_up_between_validation_and_promotion_is_refused()
    {
        var fixture = await FixtureAsync("late-collision");
        fixture.Observer = new ObserverAt(CreationPhase.Activating, _ =>
        {
            Directory.CreateDirectory(fixture.Destination);
            File.WriteAllText(Path.Combine(fixture.Destination, "someone-elses.txt"), "not ours");
        });

        var result = await fixture.RunAsync();

        Assert.False(result.Succeeded);
        Assert.IsType<CreationDestinationBlockedException>(result.Failure);
        // The other files are untouched and the candidate is still available for inspection.
        Assert.Equal("not ours", await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "someone-elses.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "server.jar")));
        Assert.Equal(CreationOutcome.StagingResumable, result.Outcome);
        Assert.True(Directory.Exists(fixture.Staging));
        Assert.Empty(await fixture.Store.GetServersAsync());
    }

    [Fact]
    public async Task A_destination_already_owned_by_a_registered_server_is_refused_before_staging()
    {
        var fixture = await FixtureAsync("owned-destination");
        await fixture.Store.UpsertServerAsync(new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Already here",
            RootPath = fixture.Destination,
            IsManaged = true
        });

        var result = await fixture.RunAsync();

        Assert.False(result.Succeeded);
        Assert.IsType<CreationDestinationBlockedException>(result.Failure);
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    // ================================================================ cancellation

    [Theory]
    [InlineData(CreationPhase.ValidatingDestination)]
    [InlineData(CreationPhase.PreparingStaging)]
    [InlineData(CreationPhase.MaterializingCandidate)]
    [InlineData(CreationPhase.VerifyingCandidate)]
    [InlineData(CreationPhase.ReadyToActivate)]
    public async Task Cancelling_in_a_safe_phase_changes_nothing_and_leaves_no_journal(CreationPhase phase)
    {
        var fixture = await FixtureAsync("cancel-" + phase);
        using var cancellation = new CancellationTokenSource();
        fixture.Observer = new ObserverAt(phase, _ => cancellation.Cancel());

        var result = await fixture.RunAsync(cancellationToken: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(CreationOutcome.NothingActivated, result.Outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Failure);
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.False(Directory.Exists(fixture.Staging));
        Assert.Empty(await fixture.Store.GetServersAsync());
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Cancelling_inside_the_critical_section_finishes_the_work_and_says_so()
    {
        var fixture = await FixtureAsync("cancel-critical");
        using var cancellation = new CancellationTokenSource();
        // Cancelled at the last safe checkpoint's boundary is honoured; cancelled once promotion has
        // begun is recorded and the operation runs to a consistent end rather than tearing.
        fixture.Observer = new ObserverAt(CreationPhase.Activating, _ => cancellation.Cancel());

        var result = await fixture.RunAsync(cancellationToken: cancellation.Token);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(fixture.Destination, "server.jar")));
        Assert.Single(await fixture.Store.GetServersAsync());
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Cancelling_repeatedly_is_the_same_as_cancelling_once()
    {
        var fixture = await FixtureAsync("cancel-repeat");
        using var cancellation = new CancellationTokenSource();
        fixture.Observer = new ObserverAt(CreationPhase.MaterializingCandidate, _ =>
        {
            cancellation.Cancel();
            cancellation.Cancel();
            cancellation.Cancel();
        });

        var result = await fixture.RunAsync(cancellationToken: cancellation.Token);

        Assert.Equal(CreationOutcome.NothingActivated, result.Outcome);
        Assert.False(Directory.Exists(fixture.Staging));
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    // ================================================================ failure and rollback

    [Fact]
    public async Task A_failure_before_promotion_leaves_the_destination_untouched()
    {
        var fixture = await FixtureAsync("materialize-failure");
        fixture.MaterializeFailure = () => new InvalidDataException("The staged package was unusable.");

        var result = await fixture.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(CreationOutcome.NothingActivated, result.Outcome);
        Assert.IsType<InvalidDataException>(result.Failure);
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.False(Directory.Exists(fixture.Staging));
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task A_registration_failure_after_promotion_is_rolled_back()
    {
        var fixture = await FixtureAsync("registration-failure");
        fixture.Observer = new ObserverAt(CreationPhase.Registering,
            _ => throw new InvalidOperationException("The database rejected the write."));

        var result = await fixture.RunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(CreationPhase.RolledBack, result.Phase);
        Assert.Equal(CreationOutcome.RolledBack, result.Outcome);
        // Reversed completely: no server folder, no record, no journal.
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Empty(await fixture.Store.GetServersAsync());
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task A_rollback_that_cannot_be_proven_safe_preserves_everything_and_asks_for_attention()
    {
        var fixture = await FixtureAsync("rollback-blocked");
        fixture.Observer = new ObserverAt(CreationPhase.Registering, _ =>
        {
            // Something occupies the folder the rollback would move the candidate back into, so the
            // reversal is no longer provably safe.
            Directory.CreateDirectory(fixture.Staging);
            throw new InvalidOperationException("The database rejected the write.");
        });

        var result = await fixture.RunAsync();

        Assert.Equal(CreationPhase.RecoveryRequired, result.Phase);
        Assert.Equal(CreationOutcome.ActivatedRegistrationIncomplete, result.Outcome);
        Assert.True(Directory.Exists(fixture.Destination));
        Assert.True(File.Exists(CreationOwnershipMarker.PathIn(fixture.Destination)));

        // The evidence is kept so the next Agent start can finish the job.
        var journal = Assert.Single(await fixture.Store.GetCreationJournalsAsync());
        Assert.True(journal.Entry!.ActivationCompleted);
        Assert.False(journal.Entry.RegistrationCompleted);
        Assert.NotNull(journal.Entry.PlannedDefinition);
    }

    [Fact]
    public async Task Rollback_refuses_a_folder_that_does_not_carry_this_operations_marker()
    {
        var destination = Path.Combine(root, "not-ours");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "someone else's");
        var entry = NewEntry(Guid.NewGuid(), destination, Path.Combine(root, "not-ours-staging")) with
        {
            ActivationCompleted = true
        };

        Assert.False(ServerCreationTransaction.TryRollBackActivation(entry));
        Assert.True(File.Exists(Path.Combine(destination, "keep.txt")));
    }

    // ================================================================ verification

    [Fact]
    public async Task Verification_fails_when_the_saved_record_points_somewhere_else()
    {
        var fixture = await FixtureAsync("verify-mismatch");
        var result = await fixture.RunAsync();
        Assert.True(result.Succeeded);

        var saved = Assert.Single(await fixture.Store.GetServersAsync());
        await fixture.Store.UpsertServerAsync(saved with { RootPath = Path.Combine(root, "somewhere-else") });

        var entry = NewEntry(fixture.OperationId, fixture.Destination, fixture.Staging) with
        {
            ServerId = fixture.ServerId,
            ActivationCompleted = true,
            RegistrationCompleted = true
        };
        var verification = await fixture.Transaction.VerifyAsync(entry, saved, CancellationToken.None);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, failure =>
            failure.Contains("different folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verification_fails_when_another_server_already_claims_the_folder()
    {
        var fixture = await FixtureAsync("verify-duplicate");
        var result = await fixture.RunAsync();
        Assert.True(result.Succeeded);
        var saved = Assert.Single(await fixture.Store.GetServersAsync());

        await fixture.Store.UpsertServerAsync(new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Impostor",
            RootPath = fixture.Destination,
            IsManaged = true
        });

        var entry = NewEntry(fixture.OperationId, fixture.Destination, fixture.Staging) with
        {
            ServerId = fixture.ServerId,
            ActivationCompleted = true,
            RegistrationCompleted = true
        };
        var verification = await fixture.Transaction.VerifyAsync(entry, saved, CancellationToken.None);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, failure =>
            failure.Contains("Impostor", StringComparison.Ordinal));
    }

    // ================================================================ cleanup

    [Fact]
    public async Task A_cleanup_failure_never_demotes_a_verified_creation_to_a_failure()
    {
        var fixture = await FixtureAsync("cleanup-failure", activationMode: CreationActivationMode.StagedCopy);
        // The staged-copy protocol leaves the staging folder in place until the end, so holding a file
        // inside it open makes the final removal fail while the server itself is untouched.
        FileStream? handle = null;
        fixture.Observer = new ObserverAt(CreationPhase.Registered, _ =>
        {
            Directory.CreateDirectory(fixture.Staging);
            handle = new FileStream(Path.Combine(fixture.Staging, "locked.bin"),
                FileMode.Create, FileAccess.Write, FileShare.None);
        });

        try
        {
            var result = await fixture.RunAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(CreationOutcome.CompletedWithCleanupWarning, result.Outcome);
            Assert.Equal(CreationPhase.CleanupPending, result.Phase);
            Assert.NotEmpty(result.Warnings);

            // The server is real, registered and correct despite the untidy leftovers.
            var registered = Assert.Single(await fixture.Store.GetServersAsync());
            Assert.Equal(fixture.ServerId, registered.Id);
            Assert.True(File.Exists(Path.Combine(fixture.Destination, "server.jar")));

            var journal = Assert.Single(await fixture.Store.GetCreationJournalsAsync());
            Assert.True(journal.Entry!.VerificationPassed);
            Assert.Equal(CreationPhase.CleanupPending, journal.Entry.Phase);

            // Releasing the handle lets a later pass finish the job without touching the server.
            handle!.Dispose();
            handle = null;
            var reports = await new ServerCreationRecoveryService(fixture.Store).RecoverAsync();

            Assert.Equal(CreationOutcome.Completed, Assert.Single(reports).Outcome);
            Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
            Assert.Single(await fixture.Store.GetServersAsync());
            Assert.True(File.Exists(Path.Combine(fixture.Destination, "server.jar")));
        }
        finally
        {
            handle?.Dispose();
        }
    }

    [Fact]
    public void Cleanup_only_ever_deletes_a_folder_this_operation_named_and_owns()
    {
        var operationId = Guid.NewGuid();
        var instanceRoot = Path.Combine(root, "instances");
        var owned = Path.Combine(instanceRoot, ServerCreationTransaction.StagingFolderName(operationId));
        var strayName = Path.Combine(instanceRoot, "someone-elses-folder");
        var outsideRoot = Path.Combine(root, ServerCreationTransaction.StagingFolderName(operationId));

        var entry = NewEntry(operationId, Path.Combine(instanceRoot, "dest"), owned) with
        {
            InstanceRoot = CreationPathSafety.Canonical(instanceRoot)
        };

        Assert.True(ServerCreationTransaction.OwnsStaging(entry));
        Assert.False(ServerCreationTransaction.OwnsStaging(entry with
        {
            CanonicalStaging = CreationPathSafety.Canonical(strayName)
        }));
        Assert.False(ServerCreationTransaction.OwnsStaging(entry with
        {
            CanonicalStaging = CreationPathSafety.Canonical(outsideRoot)
        }));
    }

    // ================================================================ recovery by checkpoint

    [Fact]
    public async Task Recovery_before_activation_discards_staging_and_leaves_the_folder_alone()
    {
        var fixture = await FixtureAsync("recover-before-activation");
        Directory.CreateDirectory(fixture.Staging);
        await File.WriteAllTextAsync(Path.Combine(fixture.Staging, "server.jar"), "candidate");
        await WriteMarkerAsync(fixture.Staging, fixture.OperationId, fixture.ServerId, fixture.Destination);
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.ReadyToActivate));

        var report = Assert.Single(await Recovery(fixture).RecoverAsync());

        Assert.Equal(CreationRecoveryDisposition.DiscardedStaging, report.Disposition);
        Assert.Equal(CreationOutcome.NothingActivated, report.Outcome);
        Assert.False(Directory.Exists(fixture.Staging));
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Empty(await fixture.Store.GetServersAsync());
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Recovery_after_promotion_but_before_registration_finishes_the_creation()
    {
        var fixture = await FixtureAsync("recover-after-activation");
        await BuildActivatedDestinationAsync(fixture);
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.Activated) with
        {
            ActivationBegan = true,
            ActivationCompleted = true
        });

        var report = Assert.Single(await Recovery(fixture).RecoverAsync());

        Assert.Equal(CreationOutcome.Completed, report.Outcome);
        var registered = Assert.Single(await fixture.Store.GetServersAsync());
        Assert.Equal(fixture.ServerId, registered.Id);
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
        Assert.False(File.Exists(CreationOwnershipMarker.PathIn(fixture.Destination)));
    }

    [Fact]
    public async Task Recovery_when_activation_began_and_the_outcome_is_unknown_reads_the_marker()
    {
        var promoted = await FixtureAsync("recover-uncertain-promoted");
        await BuildActivatedDestinationAsync(promoted);
        await promoted.Store.UpsertCreationJournalAsync(Checkpoint(promoted, CreationPhase.Activating) with
        {
            ActivationBegan = true
        });

        var promotedReport = Assert.Single(await Recovery(promoted).RecoverAsync());
        Assert.Equal(CreationOutcome.Completed, promotedReport.Outcome);
        Assert.Single(await promoted.Store.GetServersAsync());

        var notPromoted = await FixtureAsync("recover-uncertain-staged");
        Directory.CreateDirectory(notPromoted.Staging);
        await WriteMarkerAsync(notPromoted.Staging, notPromoted.OperationId, notPromoted.ServerId, notPromoted.Destination);
        await notPromoted.Store.UpsertCreationJournalAsync(Checkpoint(notPromoted, CreationPhase.Activating) with
        {
            ActivationBegan = true
        });

        var stagedReport = Assert.Single(await Recovery(notPromoted).RecoverAsync());
        Assert.Equal(CreationOutcome.NothingActivated, stagedReport.Outcome);
        Assert.False(Directory.Exists(notPromoted.Staging));
        Assert.Empty(await notPromoted.Store.GetServersAsync());
    }

    [Fact]
    public async Task Recovery_never_takes_over_a_folder_it_cannot_prove_it_owns()
    {
        var fixture = await FixtureAsync("recover-unknown-owner");
        Directory.CreateDirectory(fixture.Destination);
        await File.WriteAllTextAsync(Path.Combine(fixture.Destination, "somebody-elses.txt"), "keep me");
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.Activating) with
        {
            ActivationBegan = true
        });

        var report = Assert.Single(await Recovery(fixture).RecoverAsync());

        Assert.Equal(CreationRecoveryDisposition.AttentionRequired, report.Disposition);
        Assert.Equal(CreationOutcome.Inconsistent, report.Outcome);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "somebody-elses.txt")));
        Assert.Empty(await fixture.Store.GetServersAsync());
        Assert.Single(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Recovery_stops_when_neither_folder_can_be_identified()
    {
        var fixture = await FixtureAsync("recover-nothing-there");
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.Activating) with
        {
            ActivationBegan = true
        });

        var report = Assert.Single(await Recovery(fixture).RecoverAsync());

        Assert.Equal(CreationOutcome.Inconsistent, report.Outcome);
        Assert.Single(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Recovery_after_a_lost_registration_response_does_not_add_a_second_server()
    {
        var fixture = await FixtureAsync("recover-lost-response");
        await BuildActivatedDestinationAsync(fixture);
        // The row reached the database; the journal never learned that it had.
        await fixture.Store.UpsertServerAsync(fixture.Definition);
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.Registering) with
        {
            ActivationBegan = true,
            ActivationCompleted = true,
            RegistrationBegan = true
        });

        var report = Assert.Single(await Recovery(fixture).RecoverAsync());

        Assert.Equal(CreationOutcome.Completed, report.Outcome);
        Assert.Single(await fixture.Store.GetServersAsync());
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Recovery_after_registration_runs_the_final_checks_and_reports_a_mismatch()
    {
        var fixture = await FixtureAsync("recover-verify-fails");
        await BuildActivatedDestinationAsync(fixture);
        await fixture.Store.UpsertServerAsync(fixture.Definition with
        {
            RootPath = Path.Combine(root, "a-different-folder")
        });
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.Registered) with
        {
            ActivationBegan = true,
            ActivationCompleted = true,
            RegistrationBegan = true,
            RegistrationCompleted = true
        });

        var report = Assert.Single(await Recovery(fixture).RecoverAsync());

        Assert.Equal(CreationOutcome.RegisteredVerificationIncomplete, report.Outcome);
        // Nothing was deleted: the files are still there for a person to look at.
        Assert.True(Directory.Exists(fixture.Destination));
        Assert.Single(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Running_recovery_twice_reaches_the_same_conclusion_and_creates_nothing_extra()
    {
        var fixture = await FixtureAsync("recover-idempotent");
        await BuildActivatedDestinationAsync(fixture);
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.Activated) with
        {
            ActivationBegan = true,
            ActivationCompleted = true
        });

        var first = Assert.Single(await Recovery(fixture).RecoverAsync());
        var second = await Recovery(fixture).RecoverAsync();

        Assert.Equal(CreationOutcome.Completed, first.Outcome);
        Assert.Empty(second);
        Assert.Single(await fixture.Store.GetServersAsync());
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task Recovery_gives_up_after_a_bounded_number_of_attempts_instead_of_retrying_forever()
    {
        var fixture = await FixtureAsync("recover-bounded");
        Directory.CreateDirectory(fixture.Destination);
        await File.WriteAllTextAsync(Path.Combine(fixture.Destination, "unknown.txt"), "not ours");
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.Activating) with
        {
            ActivationBegan = true
        });

        for (var attempt = 0; attempt < ServerCreationRecoveryService.MaximumRecoveryAttempts; attempt++)
        {
            var report = Assert.Single(await Recovery(fixture).RecoverAsync());
            Assert.Equal(CreationRecoveryDisposition.AttentionRequired, report.Disposition);
        }

        var exhausted = Assert.Single(await Recovery(fixture).RecoverAsync());

        Assert.Equal(CreationOutcome.RecoveryRequired, exhausted.Outcome);
        Assert.Contains("needs your attention", exhausted.Detail, StringComparison.OrdinalIgnoreCase);
        // Still preserved, still untouched, and no longer being retried on every start.
        Assert.Equal("not ours", await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "unknown.txt")));
        var journal = Assert.Single(await fixture.Store.GetCreationJournalsAsync());
        Assert.Equal(ServerCreationRecoveryService.MaximumRecoveryAttempts, journal.Entry!.RecoveryAttempts);
    }

    [Fact]
    public async Task An_unfinished_creation_keeps_its_folder_reserved_against_a_second_attempt()
    {
        var fixture = await FixtureAsync("reserved");
        await fixture.Store.UpsertCreationJournalAsync(Checkpoint(fixture, CreationPhase.Activated) with
        {
            ActivationBegan = true,
            ActivationCompleted = true
        });

        var second = await FixtureAsync("reserved", reuseStoreFrom: fixture, destination: fixture.Destination);
        var result = await second.RunAsync();

        Assert.False(result.Succeeded);
        Assert.IsType<CreationDestinationBlockedException>(result.Failure);
        Assert.Contains("already being created", result.Failure!.Message, StringComparison.Ordinal);
    }

    // ================================================================ end-to-end through the installer

    [Fact]
    public async Task An_interrupted_creation_is_finished_by_the_next_startup_recovery_pass()
    {
        var fixture = await FixtureAsync("end-to-end-recovery");
        // Registration fails and the rollback is deliberately blocked, which is the durable state a
        // crash between promotion and persistence leaves behind.
        fixture.Observer = new ObserverAt(CreationPhase.Registering, _ =>
        {
            Directory.CreateDirectory(fixture.Staging);
            throw new InvalidOperationException("Simulated interruption between promotion and persistence.");
        });

        var interrupted = await fixture.RunAsync();
        Assert.Equal(CreationOutcome.ActivatedRegistrationIncomplete, interrupted.Outcome);
        Assert.Empty(await fixture.Store.GetServersAsync());

        var report = Assert.Single(await Recovery(fixture).RecoverAsync());

        Assert.Equal(CreationOutcome.Completed, report.Outcome);
        var registered = Assert.Single(await fixture.Store.GetServersAsync());
        Assert.Equal(fixture.ServerId, registered.Id);
        Assert.Equal(CreationPathSafety.Canonical(fixture.Destination),
            CreationPathSafety.Canonical(registered.RootPath));
        Assert.True(File.Exists(Path.Combine(fixture.Destination, "server.jar")));
        Assert.False(Directory.Exists(fixture.Staging));
        Assert.Empty(await fixture.Store.GetCreationJournalsAsync());
    }

    [Fact]
    public async Task The_production_installer_creates_registers_and_verifies_one_server()
    {
        var paths = Paths("installer");
        await using var store = await OpenStoreAsync(paths);
        var instances = Path.Combine(root, "installer-instances");
        var package = CreateServerZip(Path.Combine(root, "installer-package.zip"));
        var java = Path.Combine(root, "installer-java.exe");
        await File.WriteAllTextAsync(java, "");
        using var http = new HttpClient(new RefusingHandler());
        var installer = new ManagedServerInstaller(paths, store, new ServerDownloadCatalog(http), http);

        var result = await installer.InstallAsync(new ServerInstallRequest
        {
            OperationId = Guid.NewGuid(),
            SourceType = InstallSourceType.LocalZip,
            Source = package,
            ServerName = "Transaction Fixture",
            InstanceRoot = instances,
            JavaPath = java,
            EulaAccepted = true,
            EulaAcceptedAt = DateTimeOffset.Now
        });

        Assert.Equal(CreationOutcome.Completed, result.Outcome);
        var registered = Assert.Single(await store.GetServersAsync());
        Assert.Equal(result.Definition.Id, registered.Id);
        Assert.True(registered.IsManaged);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, "server.jar")));
        Assert.False(File.Exists(CreationOwnershipMarker.PathIn(result.Definition.RootPath)));
        Assert.Empty(await store.GetCreationJournalsAsync());
        Assert.DoesNotContain(Directory.EnumerateDirectories(instances),
            path => Path.GetFileName(path).StartsWith(".chunkpilot-staging-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_production_installer_refuses_a_folder_that_already_has_files_in_it()
    {
        var paths = Paths("installer-occupied");
        await using var store = await OpenStoreAsync(paths);
        var instances = Path.Combine(root, "occupied-instances");
        var destination = Path.Combine(instances, "Occupied-Server");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "world.dat"), "existing world");
        var package = CreateServerZip(Path.Combine(root, "occupied-package.zip"));
        var java = Path.Combine(root, "occupied-java.exe");
        await File.WriteAllTextAsync(java, "");
        using var http = new HttpClient(new RefusingHandler());
        var installer = new ManagedServerInstaller(paths, store, new ServerDownloadCatalog(http), http);

        var blocked = await Assert.ThrowsAsync<CreationDestinationBlockedException>(() =>
            installer.InstallAsync(new ServerInstallRequest
            {
                OperationId = Guid.NewGuid(),
                SourceType = InstallSourceType.LocalZip,
                Source = package,
                ServerName = "Occupied Server",
                InstanceRoot = instances,
                JavaPath = java,
                EulaAccepted = true,
                EulaAcceptedAt = DateTimeOffset.Now
            }));

        Assert.Equal(CreationDestinationVerdict.BlockedNotEmpty, blocked.Decision.Verdict);
        Assert.Equal("existing world", await File.ReadAllTextAsync(Path.Combine(destination, "world.dat")));
        Assert.Empty(await store.GetServersAsync());
        Assert.Empty(await store.GetCreationJournalsAsync());
    }

    // ================================================================ fixture

    private AppDataPaths Paths(string name) => new(Path.Combine(root, "appdata-" + name));

    private static async Task<ChunkPilotStore> OpenStoreAsync(AppDataPaths paths)
    {
        var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        return store;
    }

    private async Task<CreationFixture> FixtureAsync(
        string name,
        CreationActivationMode? activationMode = null,
        CreationFixture? reuseStoreFrom = null,
        string? destination = null)
    {
        var store = reuseStoreFrom?.Store ?? await OpenStoreAsync(Paths(name));
        var instanceRoot = Path.Combine(root, "instances-" + name);
        Directory.CreateDirectory(instanceRoot);
        var operationId = Guid.NewGuid();
        return new CreationFixture(
            store,
            operationId,
            Guid.NewGuid(),
            instanceRoot,
            destination ?? Path.Combine(instanceRoot, "Fixture-Server"),
            Path.Combine(instanceRoot, ServerCreationTransaction.StagingFolderName(operationId)),
            Path.Combine(root, "logs", operationId.ToString("N") + ".log"),
            activationMode);
    }

    private static ServerCreationRecoveryService Recovery(CreationFixture fixture) =>
        new(fixture.Store, new ServerCreationTransaction(fixture.Store));

    private static CreationJournalEntry NewEntry(Guid operationId, string destination, string staging) => new()
    {
        OperationId = operationId,
        ServerId = Guid.NewGuid(),
        CreationKind = "LocalZip",
        ServerName = "Fixture Server",
        CanonicalDestination = CreationPathSafety.Canonical(destination),
        CanonicalStaging = CreationPathSafety.Canonical(staging),
        InstanceRoot = CreationPathSafety.Canonical(Path.GetDirectoryName(destination)!),
        StartedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
        OwnershipMarkerFile = CreationOwnershipMarker.FileName
    };

    private static CreationJournalEntry Checkpoint(CreationFixture fixture, CreationPhase phase) => new()
    {
        OperationId = fixture.OperationId,
        ServerId = fixture.ServerId,
        CreationKind = "LocalZip",
        ServerName = "Fixture Server",
        CanonicalDestination = CreationPathSafety.Canonical(fixture.Destination),
        CanonicalStaging = CreationPathSafety.Canonical(fixture.Staging),
        InstanceRoot = CreationPathSafety.Canonical(fixture.InstanceRoot),
        StartedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
        Phase = phase,
        LastCompletedCheckpoint = phase,
        OwnershipMarkerFile = CreationOwnershipMarker.FileName,
        PlannedDefinition = fixture.Definition
    };

    /// <summary>Builds the exact on-disk state a completed promotion leaves behind.</summary>
    private static async Task BuildActivatedDestinationAsync(CreationFixture fixture)
    {
        Directory.CreateDirectory(fixture.Destination);
        await File.WriteAllTextAsync(Path.Combine(fixture.Destination, "server.jar"), "candidate");
        await WriteMarkerAsync(fixture.Destination, fixture.OperationId, fixture.ServerId, fixture.Destination);
    }

    private static Task WriteMarkerAsync(string directory, Guid operationId, Guid serverId, string destination) =>
        CreationOwnershipMarker.WriteAsync(directory, new CreationOwnershipMarker(
            CreationOwnershipMarker.CurrentSchemaVersion, operationId, serverId,
            CreationPathSafety.Canonical(destination), DateTimeOffset.UtcNow), CancellationToken.None);

    private static async Task WriteRawJournalAsync(AppDataPaths paths, Guid operationId, int version, string json)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO creation_journal(operation_id, server_id, destination, phase, schema_version, json, updated_utc)
            VALUES($id, $server, $destination, $phase, $version, $json, $updated)
            """;
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        command.Parameters.AddWithValue("$server", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$destination", "C:\\nowhere");
        command.Parameters.AddWithValue("$phase", CreationPhase.Activating.ToString());
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateServerZip(string path)
    {
        using var archive = System.IO.Compression.ZipFile.Open(
            path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry("server.jar");
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes("fixture server jar"));
        return path;
    }

    private sealed class CreationFixture
    {
        public CreationFixture(
            ChunkPilotStore store,
            Guid operationId,
            Guid serverId,
            string instanceRoot,
            string destination,
            string staging,
            string logPath,
            CreationActivationMode? activationMode)
        {
            Store = store;
            OperationId = operationId;
            ServerId = serverId;
            InstanceRoot = instanceRoot;
            Destination = destination;
            Staging = staging;
            LogPath = logPath;
            ActivationMode = activationMode;
            Definition = new ServerDefinition
            {
                Id = serverId,
                Name = "Fixture Server",
                RootPath = destination,
                WorkingDirectory = destination,
                Executable = "java",
                Arguments = "-jar server.jar nogui",
                Ecosystem = ServerEcosystem.Vanilla,
                MinecraftVersion = "1.21.1",
                IsManaged = true,
                ManagedInstanceRoot = instanceRoot
            };
        }

        public ChunkPilotStore Store { get; }
        public Guid OperationId { get; }
        public Guid ServerId { get; }
        public string InstanceRoot { get; }
        public string Destination { get; }
        public string Staging { get; }
        public string LogPath { get; }
        public CreationActivationMode? ActivationMode { get; }
        public ServerDefinition Definition { get; }
        public ICreationTransactionObserver? Observer { get; set; }
        public Func<Exception>? MaterializeFailure { get; set; }

        public ServerCreationTransaction Transaction =>
            new(Store, new CanonicalPathLockManager(), Observer, ActivationMode);

        public Task<CreationTransactionResult> RunAsync(
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Transaction.RunAsync(
                new CreationTransactionRequest
                {
                    OperationId = OperationId,
                    ServerId = ServerId,
                    ServerName = Definition.Name,
                    CreationKind = "LocalZip",
                    InstanceRoot = InstanceRoot,
                    Destination = Destination,
                    StagingPath = Staging,
                    LogPath = LogPath,
                    EulaAcceptedAt = DateTimeOffset.UtcNow,
                    EulaUrl = ManagedServerInstaller.EulaUrl
                },
                async (context, token) =>
                {
                    if (MaterializeFailure is not null)
                        throw MaterializeFailure();
                    await File.WriteAllTextAsync(
                        Path.Combine(context.StagingPath, "server.jar"), "fixture jar", token);
                    await File.WriteAllTextAsync(
                        Path.Combine(context.StagingPath, "eula.txt"), "eula=true", token);
                    return new CreationCandidate(Definition, "fixture://local", "", "Fixture creation");
                },
                (staging, candidate) =>
                {
                    if (!File.Exists(Path.Combine(staging, "server.jar")))
                        throw new InvalidDataException("The staged candidate has no server jar.");
                },
                progress,
                cancellationToken);
    }

    /// <summary>Runs an action the first time a chosen phase is journalled.</summary>
    private sealed class ObserverAt(CreationPhase phase, Action<CreationJournalEntry> action)
        : ICreationTransactionObserver
    {
        private bool fired;

        public Task OnPhaseAsync(CreationPhase current, CreationJournalEntry entry, CancellationToken cancellationToken)
        {
            if (current == phase && !fired)
            {
                fired = true;
                action(entry);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CollectingProgress(Action<InstallProgress> callback) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => callback(value);
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network access is permitted in this test.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
